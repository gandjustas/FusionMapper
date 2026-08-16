using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.FileProviders;

namespace FusionMapper.SourceGenerator.Tests;

public class GeneratorTest
{
    [Test]
    public async Task All_Tests_Compiled_Successfuly()
    {
        var manifestEmbeddedProvider = new ManifestEmbeddedFileProvider(typeof(GeneratorTest).Assembly);
        var files = manifestEmbeddedProvider.GetDirectoryContents("/")
                                                   .Where(f => f.Name.EndsWith(".cs"))
                                                   .Select(f => f.CreateReadStream())
                                                   .Select(s => new StreamReader(s).ReadToEnd());
        var compilation = CreateCompilation(files);
        var precomileDiagnostics = compilation.GetDiagnostics();
        await Assert.That(precomileDiagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Count()).IsEqualTo(0);

        var generator = new FusionMapperInterceptorGenerator();

        var driver = CSharpGeneratorDriver.Create([generator.AsSourceGenerator()], optionsProvider: new OptionsProvider());
        driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        await Assert.That(diagnostics.Any(d => d.Id == "FMAP001")).IsTrue(); 
    }

    private static CSharpCompilation CreateCompilation(IEnumerable<string> sources)
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

        var usings = new[] {
                "global using System;",
                "global using System.Collections.Generic;",
                "global using System.IO;",
                "global using System.Linq;",
                "global using System.Net.Http;",
                "global using System.Threading;",
                "global using System.Threading.Tasks;",
                "global using TUnit.Assertions;",
                "global using TUnit.Assertions.Extensions;",
                "global using TUnit.Core;",
                "global using static TUnit.Core.HookType;",
            };

        return CSharpCompilation.Create(
            "TestAssembly",
            sources.Concat(usings).Select(s => CSharpSyntaxTree.ParseText(s)),
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary,
            nullableContextOptions: NullableContextOptions.Enable
           ));
    }
}
