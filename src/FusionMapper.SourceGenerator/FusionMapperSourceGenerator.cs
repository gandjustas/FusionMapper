using System.Collections.Immutable;
using System.Runtime.CompilerServices;
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

    static readonly ConditionalWeakTable<Compilation, MappingBuilder> cache = new ();

    public const string FusionSourceType = "FusionSource";
    public const string FusionProjectionType = "FusionProjection";
    public static readonly DiagnosticDescriptor IncompatibleMappingRule = new(
        id: "FMAP001",
        title: "Cannot generate mapping",
        messageFormat: "Cannot generate mapping from '{0}' to '{1}': {2}",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedInEpressionTree = new(
        id: "FMAP002",
        title: "Unsupported mapping inside expression tree",
        messageFormat: "Unsupported Map<{0}>().To<{1}>(existing) inside expression tree",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor AnonymousSourceRule = new(
        id: "FMAP003",
        title: "Cannot generate mapper for anonymous source",
        messageFormat: "Cannot generate an mapper because the source type is anonymous",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor AccessorFieldNotResolvedRule = new(
        id: "FMAP004",
        title: "FusionMapper cannot resolve backing field",
        messageFormat: "Cannot resolve backing field for '{0}'. Using fallback field name '{1}'.",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);


    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var candidates = context.SyntaxProvider
            .CreateSyntaxProvider(IsCandidate, Transform)
            .Where(static c => c.HasValue)
            .Select(static (c, _) => c!.Value)
            .WithTrackingName(TrackingNames.RawCandidates);

        // Report all diagnostics
        var anonymousLocations = candidates
            .SelectMany(static (c, _) => c.Diagnostics.AsImmutableArray());

        context.RegisterImplementationSourceOutput(anonymousLocations, static (spc, diagnostic) =>
        {
            spc.ReportDiagnostic(Diagnostic.Create(diagnostic.Descriptor, diagnostic.Location, diagnostic.MessageArgs));
        });

        var mapped = candidates
            .Where(static c => c.Source is { IsAnonymous: false } && c.Target is { IsAnonymous: false } && c.MappingCode.HasValue)
            .Select(static (c, _) => new Mapped(c.Kind, c.Source!, c.Target!, c.MappingCode!.Value))
            .Collect()
            .WithTrackingName(TrackingNames.Mapped);

        var csharpSufficient = context.CompilationProvider
            .Select((x, _) => x is CSharpCompilation { LanguageVersion: LanguageVersion.Default or >= LanguageVersion.CSharp12 })
            .WithTrackingName(TrackingNames.CSharpVersion);

        context.RegisterImplementationSourceOutput(mapped.Combine(csharpSufficient), static (spc, input) =>
        {
            var (candidates, csharpSufficient) = input;
            if (!csharpSufficient) return;
            if (candidates.Length == 0) return;

            var source = SourceEmmiter.EmitMappers([.. candidates.Distinct()]);
            spc.AddSource("FusionMapper.g.cs", SourceText.From(source, Encoding.UTF8));

        });

        var initialized = candidates
            .Where(static c => c.Source is { IsAnonymous: false } && c.Target is { IsAnonymous: false } && c.MappingCode.HasValue)
            .Select(static (c, _) => new Initialized(c.Kind, c.Source!, c.Target!, c.IsInsideExpressionTree))
            .Collect()
            .WithTrackingName(TrackingNames.Initialized);

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



        context.RegisterImplementationSourceOutput(initialized.Combine(csharpSufficient).Combine(targetFrameworkProvider), static (spc, input) =>
        {
            var ((candidates, csharpSufficient), dotnetVersion) = input;
            if (!csharpSufficient) return;
            if (candidates.Length == 0) return;

            var initalizerSource = SourceEmmiter.EmitInitializer(candidates, dotnetVersion);
            spc.AddSource("FusionMapper.Initializer.g.cs", SourceText.From(initalizerSource, Encoding.UTF8));

        });


        var interceptionEnabledSetting = context.AnalyzerConfigOptionsProvider
            .Select((x, _) =>
                x.GlobalOptions.TryGetValue("build_property.EnableFusionMapperInterceptor", out var enableSwitch)
                && !enableSwitch.Equals("false", StringComparison.Ordinal))
            .WithTrackingName(TrackingNames.InterceptorsIsEnabled);


        var interceptionEnabled = interceptionEnabledSetting
                .Combine(csharpSufficient)
                .Combine(targetFrameworkProvider)
                .Select((t, _) => t.Left.Left && t.Left.Right && t.Right >= 9);

        var interceptable = candidates
            .Where(static c => c.Source is { IsAnonymous: false } && c.Target is { IsAnonymous: false } && c.Interceptable is not null && !c.IsInsideExpressionTree)
            .Select(static (c, _) => new Interceptable(c.Kind, c.Source!, c.Target!, c.Interceptable!))
            .Collect()
            .WithTrackingName(TrackingNames.Intercepted);


        var accessorFields =
            context.CompilationProvider
            .Select(static (compilation, ct) => FusionAccessorMetadata.Resolve(compilation, ct))
            .WithTrackingName(TrackingNames.AccessorFields);


        context.RegisterImplementationSourceOutput(interceptable.Combine(interceptionEnabled).Combine(accessorFields),
        static (spc, input) =>
        {
            var ((candidates, enabled),  fields) = input;
            if (!enabled) return;
            if (candidates.Length == 0) return;

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

            var interceptorStore = SourceEmmiter.EmitInterceptors(candidates, fields);
            spc.AddSource("FusionMapper.Interceptors.g.cs", SourceText.From(interceptorStore, Encoding.UTF8));
        });
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

            var location = ctx.Node.GetLocation();
            //var span = location.GetLineSpan();
            //var mappedSpan = location.GetMappedLineSpan();
            //location = Location.Create(span.Path, location.SourceSpan, span.Span, mappedSpan.Path, mappedSpan.Span);

            var isInsideExpresionTree = IsInsideExpressionTree(ctx.SemanticModel, invocation, ct);
            if(sourceType.IsAnonymousType || targetType.IsAnonymousType)
            {
                return new RawCandidate(
                    kind, isInsideExpresionTree,
                    null, null, null, null,
                    ImmutableArray.Create(new GeneratorDiagnostic(AnonymousSourceRule, location))
                );
            }

            List<GeneratorDiagnostic> diagnostics = [];
            if(isInsideExpresionTree)
            {
                if(kind == CallKind.SourceToExisting)
                {
                    diagnostics.Add(new (UnsupportedInEpressionTree, location, sourceType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat), targetType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
                }
                else
                {
                    kind = CallKind.ProjectionTo;
                }
            }



            var builder = cache.GetValue(ctx.SemanticModel.Compilation, key => new MappingBuilder(key));
            Mapping? mapping = null;
            EquatableArray<string>? code = null;
            try
            {
                mapping = builder.Build(sourceType, targetType);
                code = MappingEmitter.Emit(kind, mapping).ToImmutableArray();
            }
            catch (MappingGenerationException ex)
            {
                diagnostics.Add(new (IncompatibleMappingRule,
                    location,
                    sourceType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                    targetType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                    ex.Message));
            }

            var interceptLocation = ctx.SemanticModel.GetInterceptableLocation(invocation);
            return new RawCandidate(
                kind, isInsideExpresionTree,
                mapping?.SourceType, mapping?.TargetType,
                interceptLocation,
                code, diagnostics.ToImmutableArray()
                );

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