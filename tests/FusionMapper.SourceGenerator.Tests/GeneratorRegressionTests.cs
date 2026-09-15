using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FusionMapper.SourceGenerator.Tests;

/// <summary>
/// Regression tests for confirmed generator bugs:
/// TFM parsing crash, parallel Build race, and silent collection data loss.
/// </summary>
public class GeneratorRegressionTests
{
    // ---------- TFM parsing must not fault the generator ----------

    [Test]
    [Arguments("net10.0")]
    [Arguments("net9.0")]
    [Arguments("net8.0")]
    [Arguments("net472")]
    [Arguments("net8")]
    [Arguments("netstandard2.0")]
    public async Task Generator_Does_Not_Fault_On_Tfm(string tfm)
    {
        var driver = CSharpGeneratorDriver.Create(
            [new FusionMapperInterceptorGenerator().AsSourceGenerator()],
            optionsProvider: new TestOptionsProvider(tfm));

        var result = driver
            .RunGenerators(CreateCompilation("public class C { }"))
            .GetRunResult();

        await Assert.That(result.Results[0].Exception).IsNull();
    }

    // ---------- Parallel Build calls on one MappingBuilder ----------

    [Test]
    public async Task Parallel_Builds_Do_Not_Interfere()
    {
        const int depth = 14;
        var sb = new System.Text.StringBuilder();
        for (var i = 1; i < depth; i++)
        {
            sb.AppendLine($"class A{i} {{ public A{i + 1} Next; public int X{i}; }}");
            sb.AppendLine($"class AT{i} {{ public AT{i + 1} Next; public int X{i}; }}");
            sb.AppendLine($"class B{i} {{ public B{i + 1} Next; public int X{i}; }}");
            sb.AppendLine($"class BT{i} {{ public BT{i + 1} Next; public int X{i}; }}");
        }
        sb.AppendLine($"class A{depth} {{ public int X{depth}; }} class AT{depth} {{ public int X{depth}; }}");
        sb.AppendLine($"class B{depth} {{ public int X{depth}; }} class BT{depth} {{ public int X{depth}; }}");

        var compilation = CreateCompilation(sb.ToString());
        var builder = new MappingBuilder(compilation);

        const int iterations = 200;
        var failures = 0;

        for (var i = 0; i < iterations; i++)
        {
            using var barrier = new Barrier(2);
            var t1 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                try { builder.Build(compilation.GetTypeByMetadataName("A1")!, compilation.GetTypeByMetadataName("AT1")!); }
                catch { Interlocked.Increment(ref failures); }
            });
            var t2 = Task.Run(() =>
            {
                barrier.SignalAndWait();
                try { builder.Build(compilation.GetTypeByMetadataName("B1")!, compilation.GetTypeByMetadataName("BT1")!); }
                catch { Interlocked.Increment(ref failures); }
            });

            await Task.WhenAll(t1, t2);
        }

        await Assert.That(failures).IsEqualTo(0);
    }

    // ---------- Silent collection data loss must produce a diagnostic ----------

    [Test]
    public async Task Incompatible_Collection_Add_Produces_Diagnostic()
    {
        const string sources = """
            using System.Collections.Generic;
            using FusionMapper;

            public class Src { public List<int> Items { get; set; } }
            public class BadAdd { public void Add(string s) { } }
            public class Tgt { public BadAdd Items { get; set; } }

            public static class Usage
            {
                public static void M(Src s) { var t = s.Map().To<Tgt>(); }
            }
            """;

        var driver = CSharpGeneratorDriver.Create(
            [new FusionMapperInterceptorGenerator().AsSourceGenerator()],
            optionsProvider: new TestOptionsProvider());

        var runResult = driver
            .RunGenerators(CreateCompilation(sources))
            .GetRunResult();

        var diagnostics = runResult.Diagnostics
            .Where(d => d.Id == "FMAP001")
            .ToArray();

        await Assert.That(diagnostics).IsNotEmpty();
    }

    [Test]
    public async Task Generated_Mapper_Uses_ParseEnum_Helper()
    {
        const string sources = """
            using FusionMapper;

            public enum Color { Red, Green }
            public class Src { public string E { get; set; } = "Green"; }
            public class Tgt { public Color E { get; set; } }

            public static class Usage
            {
                public static void M(Src s) { var t = s.Map().To<Tgt>(); }
            }
            """;

        var driver = CSharpGeneratorDriver.Create(
            [new FusionMapperInterceptorGenerator().AsSourceGenerator()],
            optionsProvider: new TestOptionsProvider());

        var runResult = driver
            .RunGenerators(CreateCompilation(sources))
            .GetRunResult();

        var mappers = runResult.GeneratedTrees
            .First(t => t.FilePath.EndsWith("FusionMapper.g.cs"))
            .GetText()
            .ToString();

        await Assert.That(mappers).Contains("ParseEnum");
        await Assert.That(mappers).DoesNotContain("Enum.Parse");
    }

    [Test]
    public async Task FirstOrDefault_To_NonNullable_Target_Uses_Null_Throw()
    {
        const string sources = """
            using System.Collections.Generic;
            using FusionMapper;

            public class Line { public string Name { get; set; } = ""; }
            public class Src { public List<Line> Lines { get; set; } = []; }
            public class Tgt { public Line LinesFirstOrDefault { get; set; } = null!; }

            public static class Usage
            {
                public static void M(Src s) { var t = s.Map().To<Tgt>(); }
            }
            """;

        var driver = CSharpGeneratorDriver.Create(
            [new FusionMapperInterceptorGenerator().AsSourceGenerator()],
            optionsProvider: new TestOptionsProvider());

        var runResult = driver
            .RunGenerators(CreateCompilation(sources))
            .GetRunResult();

        var mappers = runResult.GeneratedTrees
            .First(t => t.FilePath.EndsWith("FusionMapper.g.cs"))
            .GetText()
            .ToString();

        await Assert.That(mappers).Contains("?? throw");
        await Assert.That(mappers).DoesNotContain(")!");
    }

    // ---------- helpers ----------

    private static CSharpCompilation CreateCompilation(string source)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .Cast<MetadataReference>();

        return CSharpCompilation.Create(
            "RegressionAssembly",
            [CSharpSyntaxTree.ParseText(source, CSharpParseOptions.Default.WithLanguageVersion(LanguageVersion.Latest))],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
    }

    private sealed class TestOptionsProvider(string? tfm = null) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new Options(tfm);

        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;

        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;

        private sealed class Options(string? tfm) : AnalyzerConfigOptions
        {
            public override bool TryGetValue(string key, [NotNullWhen(true)] out string? value)
            {
                switch (key)
                {
                    case "build_property.EnableFusionMapperInterceptor":
                        value = "true";
                        return true;
                    case "build_property.TargetFramework":
                        value = tfm ?? "net10.0";
                        return true;
                    default:
                        value = null;
                        return false;
                }
            }
        }
    }
}
