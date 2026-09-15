using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FusionMapper.SourceGenerator.Tests;

public class UnmappedMembersDiagnosticsTests
{
    [Test]
    public async Task Unmapped_Target_Member_Should_Produce_FMAP005()
    {
        var source = """
            using FusionMapper;

            public record Source(int Id, string Name);

            public record Target
            {
                public int Id { get; init; }
                public string Description { get; init; } = default!;
            }

            public static class TestClass
            {
                public static Target Map(Source source) => source.Map().To<Target>();
            }
            """;

        var diagnostics = RunGenerator(source);

        var warning = diagnostics.FirstOrDefault(d => d.Id == "FMAP005");
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!.GetMessage()).Contains("Target");
        await Assert.That(warning!.GetMessage()).Contains("Description");
    }

    [Test]
    public async Task Unmapped_Members_Should_Be_Listed_In_One_Diagnostic()
    {
        var source = """
            using FusionMapper;

            public record Source(int Id);

            public record Target
            {
                public int Id { get; init; }
                public string Name { get; init; } = default!;
                public string Description { get; init; } = default!;
            }

            public static class TestClass
            {
                public static Target Map(Source source) => source.Map().To<Target>();
            }
            """;

        var diagnostics = RunGenerator(source);

        var warnings = diagnostics.Where(d => d.Id == "FMAP005").ToArray();
        await Assert.That(warnings.Length).IsEqualTo(1);
        await Assert.That(warnings[0].GetMessage()).Contains("Name");
        await Assert.That(warnings[0].GetMessage()).Contains("Description");
    }

    [Test]
    public async Task Fully_Mapped_Target_Should_Not_Produce_FMAP005()
    {
        var source = """
            using FusionMapper;

            public record Source(int Id, string Name);

            public record Target(int Id, string Name);

            public static class TestClass
            {
                public static Target Map(Source source) => source.Map().To<Target>();
            }
            """;

        var diagnostics = RunGenerator(source);

        await Assert.That(diagnostics.Any(d => d.Id == "FMAP005")).IsFalse();
    }

    [Test]
    public async Task Constructor_Assigned_Members_Should_Not_Produce_FMAP005()
    {
        var source = """
            using FusionMapper;

            public record Source(int Id, string Name);

            // Id и Name заполняются через конструктор позиционного record'а.
            public record Target(int Id, string Name);

            public static class TestClass
            {
                public static Target Map(Source source) => source.Map().To<Target>();
            }
            """;

        var diagnostics = RunGenerator(source);

        await Assert.That(diagnostics.Any(d => d.Id == "FMAP005")).IsFalse();
    }

    [Test]
    public async Task FusionMapperIgnore_Member_Should_Not_Produce_FMAP005()
    {
        var source = """
            using FusionMapper;

            public record Source(int Id);

            public record Target
            {
                public int Id { get; init; }

                [FusionMapperIgnore]
                public string Description { get; init; } = default!;
            }

            public static class TestClass
            {
                public static Target Map(Source source) => source.Map().To<Target>();
            }
            """;

        var diagnostics = RunGenerator(source);

        await Assert.That(diagnostics.Any(d => d.Id == "FMAP005")).IsFalse();
    }

    [Test]
    public async Task Suppressed_By_Build_Property_Should_Not_Produce_FMAP005()
    {
        var source = """
            using FusionMapper;

            public record Source(int Id, string Name);

            public record Target
            {
                public int Id { get; init; }
                public string Description { get; init; } = default!;
            }

            public static class TestClass
            {
                public static Target Map(Source source) => source.Map().To<Target>();
            }
            """;

        var diagnostics = RunGenerator(source, suppressUnmappedWarnings: true);

        await Assert.That(diagnostics.Any(d => d.Id == "FMAP005")).IsFalse();
    }

    [Test]
    public async Task Projection_With_Unmapped_Member_Should_Produce_FMAP005()
    {
        var source = """
            using System.Linq;
            using FusionMapper;

            public record Source(int Id, string Name);

            public record Target
            {
                public int Id { get; init; }
                public string Description { get; init; } = default!;
            }

            public static class TestClass
            {
                public static object Project(IQueryable<Source> query) => query.Project().To<Target>();
            }
            """;

        var diagnostics = RunGenerator(source);

        var warning = diagnostics.FirstOrDefault(d => d.Id == "FMAP005");
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!.GetMessage()).Contains("Description");
    }

    private static ImmutableArray<Diagnostic> RunGenerator(string source, bool suppressUnmappedWarnings = false)
    {
        var compilation = CreateCompilation(source);
        var generator = new FusionMapperInterceptorGenerator();

        var driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            optionsProvider: new OptionsProvider(suppressUnmappedWarnings));

        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);

        return diagnostics;
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
