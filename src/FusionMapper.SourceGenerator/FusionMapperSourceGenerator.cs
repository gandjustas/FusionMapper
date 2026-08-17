using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace FusionMapper.SourceGenerator;

[Generator(LanguageNames.CSharp)]
public sealed class FusionMapperInterceptorGenerator : IIncrementalGenerator
{
    public const string FusionSourceType = "FusionSource";
    public const string FusionProjectionType = "FusionProjection";
    public static readonly DiagnosticDescriptor IncompatibleMappingRule = new(
        id: "FMAP001",
        title: "FusionMapper cannot generate mapping",
        messageFormat: "Cannot generate mapping from '{0}' to '{1}': {2}",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AnonymousSourceRule = new(
        id: "FMAP002",
        title: "FusionMapper cannot intercept anonymous source",
        messageFormat: "Cannot generate an interceptor for FusionMapper call because the source or target type is anonymous",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor AccessorFieldNotResolvedRule = new(
        id: "FMAP003",
        title: "FusionMapper cannot resolve backing field",
        messageFormat: "Cannot resolve backing field for '{0}'. Using fallback field name '{1}'.",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);


    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var interceptionEnabledSetting = context.AnalyzerConfigOptionsProvider
            .Select((x, _) =>
                x.GlobalOptions.TryGetValue("build_property.EnableFusionMapperInterceptor", out var enableSwitch)
                && !enableSwitch.Equals("false", StringComparison.Ordinal))
            .WithTrackingName(TrackingNames.InterceptorsIsEnabled);

        var csharpSufficient = context.CompilationProvider
            .Select((x, _) => x is CSharpCompilation { LanguageVersion: LanguageVersion.Default or >= LanguageVersion.CSharp12 })
            .WithTrackingName(TrackingNames.CSharpVersion);

        var interceptionEnabled = interceptionEnabledSetting.Combine(csharpSufficient).Select((t, _) => t.Left && t.Right);

        var rawCandidates = context.SyntaxProvider
            .CreateSyntaxProvider(IsCandidate, Transform)
            .Combine(interceptionEnabled)
            .Where(static c => c.Right && c.Left is not null)
            .Select(static (c, _) => c.Left!.Value)
            .WithTrackingName(TrackingNames.RawCandidates);

        // Anonymous warnings
        var anonymousLocations = rawCandidates
            .Where(static c => c.SourceSymbol.IsAnonymousType || c.TargetSymbol.IsAnonymousType)
            .Select(static (c, _) => c.Location);

        context.RegisterImplementationSourceOutput(anonymousLocations, static (spc, location) =>
        {
            spc.ReportDiagnostic(Diagnostic.Create(AnonymousSourceRule, location));
        });


        var builder = context.CompilationProvider.Select(static (compilation, ct) => new MappingBuilder(compilation));
        var candidates = rawCandidates
            .Where(static c => !c.SourceSymbol.IsAnonymousType && !c.TargetSymbol.IsAnonymousType)
            .Combine(builder)
            .Select(MapCandidates)
            .WithTrackingName(TrackingNames.Candidates);

        var failed = candidates
            .Where(static c => c is MappingFailed)
            .Select(static (c, _) => (MappingFailed)c);

        context.RegisterImplementationSourceOutput(failed, static (spc, fail) =>
        {
            spc.ReportDiagnostic(Diagnostic.Create(
                IncompatibleMappingRule,
                fail.Location,
                fail.Source.FullName,
                fail.Target.FullName,
                fail.Exception.Message));
        });

        var mapped = candidates
            .Where(static c => c is Mapped)
            .Select(static (c, _) => (Mapped)c)
            .Collect();

        context.RegisterSourceOutput(mapped, static (spc, input) =>
        {
            if (input.Length == 0) return;
            var source = SourceEmmiter.EmitMappers(spc, input);
            spc.AddSource("FusionMapper.g.cs", SourceText.From(source, Encoding.UTF8));
        });


        IncrementalValueProvider<int> targetFrameworkProvider = context.AnalyzerConfigOptionsProvider
            .Select((options, _) =>
            {
                if (options.GlobalOptions.TryGetValue("build_property.TargetFramework", out var tfm)
                    && int.TryParse(tfm[3..tfm.IndexOf('.')], out var version))
                {
                    return version;
                }

                return 8;
            })
            .WithTrackingName(TrackingNames.DotnetVersion);

        var accessorFields =
            context.CompilationProvider
            .Select(static (compilation, ct) => FusionAccessorMetadata.Resolve(compilation, ct))
            .WithTrackingName(TrackingNames.AccessorFields);


        context.RegisterSourceOutput(mapped.Combine(targetFrameworkProvider).Combine(accessorFields),
        static (spc, input) =>
        {
            var ((candidates, dotnetVersion), fields) = input;

            if (candidates.Length == 0) return;

            var initalizerSource = SourceEmmiter.EmitInitializer(candidates, dotnetVersion);
            spc.AddSource("FusionMapper.Initializer.g.cs", SourceText.From(initalizerSource, Encoding.UTF8));


            if (dotnetVersion >= 9)
            {
                if (!fields.SourceValueFieldResolved)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        AccessorFieldNotResolvedRule,
                        Location.None,
                        "FusionMapper.FusionSource<T>",
                        fields.SourceValueField));
                }

                if (!fields.ProjectionValueFieldResolved)
                {
                    spc.ReportDiagnostic(Diagnostic.Create(
                        AccessorFieldNotResolvedRule,
                        Location.None,
                        "FusionMapper.FusionProjection<T>",
                        fields.ProjectionValueField));
                }

                var interceptorStore = SourceEmmiter.EmitInterceptors(candidates.OfType<Interceptable>(), fields);
                spc.AddSource("FusionMapper.Interceptors.g.cs", SourceText.From(interceptorStore, Encoding.UTF8));
            }
        });
    }

    private Candidate MapCandidates((RawCandidate Left, MappingBuilder Right) tuple, CancellationToken token)
    {
        var (candidate, builder) = tuple;
        try
        {
            var mapping = builder.Build(candidate.SourceSymbol, candidate.TargetSymbol);
            if (candidate.IsInsideExpressionTree)
            {
                return new Mapped
                {
                    Location = candidate.Location,
                    Kind = candidate.Kind,
                    Source = candidate.Source,
                    Target = candidate.Target,
                    Mapping = mapping,
                    IsInsideExpressionTree = true
                };
            }
            else
            {
                return new Interceptable
                {
                    Location = candidate.Location,
                    InterceptableLocation = candidate.Interceptable,
                    Kind = candidate.Kind,
                    Source = candidate.Source,
                    Target = candidate.Target,
                    Mapping = mapping
                };
            }
        }
        catch (Exception ex)
        {
            return new MappingFailed
            {
                Kind = candidate.Kind,
                Location = candidate.Location,
                Source = candidate.Source,
                Target = candidate.Target,
                Exception = ex
            };
        }
    }

    private static bool IsCandidate(SyntaxNode node, CancellationToken ct) =>
        node is InvocationExpressionSyntax
        {
            Expression: MemberAccessExpressionSyntax
            {
                Name.Identifier.Value: "To",
            }
        };

    private static RawCandidate? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Value: "To" } } invocation
            && ctx.SemanticModel.GetOperation(invocation, ct) is IInvocationOperation targetOperation
            && targetOperation.TargetMethod is
            {
                Name: "To",
                ContainingType:
                {
                    Name: FusionSourceType or FusionProjectionType,
                    ContainingNamespace: { Name: "FusionMapper", ContainingNamespace.IsGlobalNamespace: true }
                } source,
                Parameters: { Length: 0 or 1 } parameters,
            }
            && (source is { Name: FusionSourceType } || parameters is { Length: 0 })
            && targetOperation.Instance?.Type is INamedTypeSymbol type
            && ctx.SemanticModel.GetInterceptableLocation(invocation) is { } location
            )
        {
            CallKind kind;
            if (source.Name == FusionSourceType)
            {
                if (parameters.Length == 0)
                {
                    kind = CallKind.SourceTo;
                }
                else if (parameters.Length == 1)
                {
                    kind = CallKind.SourceToExisting;
                }
                else
                {
                    return null;
                }
            }
            else if (source.Name == FusionProjectionType)
            {
                kind = CallKind.ProjectionTo;
            }
            else
            {
                return null;
            }


            var sourceType = type.TypeArguments[0];
            var targetType = targetOperation.TargetMethod.TypeArguments[0];

            if (IsUnsupported(sourceType) || IsUnsupported(targetType))
                return null;

            return new(
                ctx.Node.GetLocation(),
                location,
                kind,
                sourceType,
                targetType,
                TypeModel.Create(sourceType),
                TypeModel.Create(targetType),
                IsInsideExpressionTree(ctx.SemanticModel, invocation, ct));
        }
        return null;
    }


    private static bool IsUnsupported(ITypeSymbol type)
    {
        if (type.TypeKind is TypeKind.Error or TypeKind.Dynamic or TypeKind.Pointer or TypeKind.FunctionPointer or TypeKind.TypeParameter)
            return true;

        if (type.IsRefLikeType)
            return true;

        if (type.IsTupleType)
            return true;

        if (type.SpecialType == SpecialType.System_Void)
            return true;


        return false;
    }

    private static bool IsInsideExpressionTree(SemanticModel model, SyntaxNode node, CancellationToken ct)
    {
        var insideQueryBodyClause = false;

        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is QueryBodySyntax)
            {
                insideQueryBodyClause = true;
            }

            if (current is AnonymousFunctionExpressionSyntax lambda)
            {
                var convertedType = model.GetTypeInfo(lambda, ct).ConvertedType;
                if (IsExpressionOfT(convertedType))
                    return true;
            }

            if (current is QueryExpressionSyntax query && insideQueryBodyClause)
            {
                var typeInfo = model.GetTypeInfo(query, ct);
                if (IsQueryable(typeInfo.Type) || IsQueryable(typeInfo.ConvertedType))
                    return true;
            }

            if (current is MemberDeclarationSyntax or AccessorDeclarationSyntax or AttributeSyntax)
                break;
        }

        return false;
    }

    private static bool IsExpressionOfT(ITypeSymbol? type) =>
        type is INamedTypeSymbol
        {
            IsGenericType: true,
            ConstructedFrom: { } cf
        } && cf.ToDisplayString() == "System.Linq.Expressions.Expression<TDelegate>";


    private static bool IsQueryable(ITypeSymbol? type) =>
        type is INamedTypeSymbol t
        && (IsGenericQueryable(t)
            || t.AllInterfaces.Any(IsGenericQueryable));

    private static bool IsGenericQueryable(INamedTypeSymbol type) =>
        type is
        {
            IsGenericType: true,
            ConstructedFrom: { } cf
        } && cf.ToDisplayString() == "System.Linq.IQueryable<T>";

}

