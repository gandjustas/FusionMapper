using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FusionMapper.SourceGenerator.Tests;

public class GeneratorDiagnosticsTests
{
    [Test]
    public async Task Anonymous_Type_Should_Produce_FMAP002()
    {
        var source = """
            using FusionMapper;

            public static class TestClass
            {
                public static object MapAnonymous()
                {
                    var anonymous = new { Id = 1 };
                    return anonymous.Map().To<object>();
                }
            }
            """;

        var compilation = CreateCompilation(source);
        var precomileDiagnostics = compilation.GetDiagnostics();
        await Assert.That(precomileDiagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Count()).IsEqualTo(0);

        var generator = new FusionMapperInterceptorGenerator();

        var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], optionsProvider: new OptionsProvider());
        var result = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        await Assert.That(result).IsNotNull();
        await Assert.That(diagnostics.Any(d => d.Id == "FMAP002")).IsTrue();
    }

    private static CSharpCompilation CreateCompilation(string source)
    {
        var references = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.Location))
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                .Cast<MetadataReference>()
                .ToList();

        references.AddRange(
            typeof(FusionMapper).Assembly.GetReferencedAssemblies()
                .Select(System.Reflection.Assembly.Load)
                .Select(a => MetadataReference.CreateFromFile(a.Location))
                );

        return CSharpCompilation.Create(
            "TestAssembly",
            [CSharpSyntaxTree.ParseText(source)],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    }
}