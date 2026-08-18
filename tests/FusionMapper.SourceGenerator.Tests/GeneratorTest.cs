using System.Collections;
using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.Extensions.FileProviders;
using TUnit.Assertions.Enums;

namespace FusionMapper.SourceGenerator.Tests;

public class GeneratorTest
{
    [Test]
    public async Task Is_Caching()
    {
        string[] trackingNames = [TrackingNames.RawCandidates, TrackingNames.Mapped, TrackingNames.Initialized, TrackingNames.Intercepted];

        var manifestEmbeddedProvider = new ManifestEmbeddedFileProvider(typeof(GeneratorTest).Assembly);
        var files = manifestEmbeddedProvider.GetDirectoryContents("/")
                                                   .Where(f => f.Name.EndsWith(".cs"))
                                                   .Select(f => f.CreateReadStream())
                                                   .Select(s => new StreamReader(s).ReadToEnd());
        var compilation = CreateCompilation(files);
        var precomileDiagnostics = compilation.GetDiagnostics();
        await Assert.That(precomileDiagnostics.Where(x => x.Severity == DiagnosticSeverity.Error).Count()).IsEqualTo(0);

        var generator = new FusionMapperInterceptorGenerator();

        await IsGeneratorCachingStages(generator, compilation, new OptionsProvider(), trackingNames);
    }

    private static async Task IsGeneratorCachingStages(IIncrementalGenerator generator, CSharpCompilation compilation, AnalyzerConfigOptionsProvider optionsProvider, params IEnumerable<string> trackingNames)
    {
        // ⚠ Tell the driver to track all the incremental generator outputs
        // without this, you'll have no tracked outputs!
        var opts = new GeneratorDriverOptions(
            disabledOutputs: IncrementalGeneratorOutputKind.None,
            trackIncrementalGeneratorSteps: true);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            [generator.AsSourceGenerator()],
            optionsProvider: optionsProvider,
            driverOptions: opts);

        var compilationClone = compilation.Clone();

        driver = driver.RunGenerators(compilation);
        var steps1 = GetTrackedSteps(driver.GetRunResult(), trackingNames);
        var steps2 = GetTrackedSteps(driver.RunGenerators(compilationClone).GetRunResult(), trackingNames);

        await Assert.That(steps1).Count().IsEqualTo(steps2.Count);

        // Get the IncrementalGeneratorRunStep collection for each run
        foreach (var tn in trackingNames)
        {
            await Assert.That(steps1).ContainsKey(tn);
            await Assert.That(steps2).ContainsKey(tn);
            // Assert that both runs produced the same outputs
            await AssertEqual(steps1[tn], steps2[tn], tn);
        }
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

    // Local function that extracts the tracked steps
    static Dictionary<string, ImmutableArray<IncrementalGeneratorRunStep>> GetTrackedSteps(
        GeneratorDriverRunResult runResult, IEnumerable<string> trackingNames)
        => runResult
                .Results[0] // We're only running a single generator, so this is safe
                .TrackedSteps // Get the pipeline outputs
                .Where(step => trackingNames.Contains(step.Key)) // filter to known steps
                .ToDictionary(x => x.Key, x => x.Value); // Convert to a dictionary


    private static async Task AssertEqual(
    ImmutableArray<IncrementalGeneratorRunStep> runSteps1,
    ImmutableArray<IncrementalGeneratorRunStep> runSteps2,
    string stepName)
    {
        await Assert.That(runSteps1).Count().IsEqualTo(runSteps2.Length);

        for (var i = 0; i < runSteps1.Length; i++)
        {
            var runStep1 = runSteps1[i];
            var runStep2 = runSteps2[i];

            // The outputs should be equal between different runs
            IEnumerable<object> outputs1 = runStep1.Outputs.Select(x => x.Value);
            IEnumerable<object> outputs2 = runStep2.Outputs.Select(x => x.Value);

            await Assert.That(outputs1)
                        .IsEquivalentTo(outputs2, CollectionOrdering.Matching)  
                        .Using(EqualityComparer<object>.Default)
                        .Because($"{stepName} should produce cacheable outputs");

            // Therefore, on the second run the results should always be cached or unchanged!
            // - Unchanged is when the _input_ has changed, but the output hasn't
            // - Cached is when the the input has not changed, so the cached output is used 
            await Assert.That(runStep2.Outputs)
                        .All(x => x.Reason is IncrementalStepRunReason.Cached or IncrementalStepRunReason.Unchanged)
                        .Because($"{stepName} expected to have reason {IncrementalStepRunReason.Cached} or {IncrementalStepRunReason.Unchanged}");

            // Make sure we're not using anything we shouldn't
            await AssertObjectGraph(runStep1, stepName);
        }
    }

    static async Task AssertObjectGraph(IncrementalGeneratorRunStep runStep, string stepName)
    {
        // Including the stepName in error messages to make it easy to isolate issues
        var because = $"{stepName} shouldn't contain banned symbols";
        var visited = new HashSet<object>();

        // Check all of the outputs - probably overkill, but why not
        foreach (var (obj, _) in runStep.Outputs)
        {
            await Visit(obj);
        }

        async Task Visit(object? node)
        {
            // If we've already seen this object, or it's null, stop.
            if (node is null || !visited.Add(node))
            {
                return;
            }

            // Make sure it's not a banned type
            await Assert.That(node)
                        .IsNotTypeOf<Compilation>()
                        .And.IsNotTypeOf<ISymbol>()
                        .And.IsNotTypeOf<SyntaxNode>()
                        .Because(because);

            // Examine the object
            Type type = node.GetType();
            if (type.IsPrimitive || type.IsEnum || type == typeof(string))
            {
                return;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ImmutableArray<>))
            {
                var isDefault = (bool)type.GetProperty("IsDefault")!.GetValue(node)!;
                if (isDefault)
                {
                    return; // Пропускаем default(ImmutableArray<T>), чтобы не получить InvalidOperationException
                }
            }

            // If the object is a collection, check each of the values
            if (node is IEnumerable collection and not string)
            {
                foreach (object element in collection)
                {
                    // recursively check each element in the collection
                    await Visit(element);
                }

                return;
            }

            // Recursively check each field in the object
            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                object? fieldValue = field.GetValue(node);
                await Visit(fieldValue);
            }
        }
    }

}
