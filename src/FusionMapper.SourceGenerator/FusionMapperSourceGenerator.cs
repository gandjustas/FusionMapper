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

    static readonly ConditionalWeakTable<Compilation, MappingBuilder> cache = new();

    public const string FusionSourceType = "FusionSource";
    public const string FusionProjectionType = "FusionProjection";
    public static readonly DiagnosticDescriptor IncompatibleMappingRule = new(
        id: "FMAP001",
        title: "Cannot generate mapping",
        messageFormat: "Cannot generate mapping from '{0}' to '{1}': {2}",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // Runtime-fallback variant: the mapping is resolved at runtime (and throws
    // MappingException), so a build failure is not justified.
    public static readonly DiagnosticDescriptor IncompatibleMappingRuleRuntime = new(
        id: "FMAP001",
        title: "Cannot generate mapping",
        messageFormat: "Cannot generate mapping from '{0}' to '{1}': {2}",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedInExpressionTree = new(
        id: "FMAP002",
        title: "Unsupported mapping inside expression tree",
        messageFormat: "Unsupported Map<{0}>().To<{1}>(existing) inside expression tree",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedInExpressionTreeRuntime = new(
        id: "FMAP002",
        title: "Unsupported mapping inside expression tree",
        messageFormat: "Unsupported Map<{0}>().To<{1}>(existing) inside expression tree",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Warning,
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

    public static readonly DiagnosticDescriptor UnmappedTargetMembersRule = new(
        id: "FMAP005",
        title: "Target members have no matching source members",
        messageFormat:
            "The following members of '{0}' have no matching source members and will keep their default values: {1}. " +
            "Rename the members, mark them with [FusionMapperIgnore], " +
            "or set <FusionMapperSuppressUnmappedWarnings>true</FusionMapperSuppressUnmappedWarnings> to suppress.",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    private const string IncompatibleMappingRuleId = "FMAP001";
    private const string UnsupportedInExpressionTreeRuleId = "FMAP002";
    private const string UnmappedTargetMembersRuleId = "FMAP005";


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

        var csharpSufficient = context.CompilationProvider
            .Select((x, _) => x is CSharpCompilation { LanguageVersion: LanguageVersion.Default or >= LanguageVersion.CSharp12 })
            .WithTrackingName(TrackingNames.CSharpVersion);

        IncrementalValueProvider<int> targetFrameworkProvider = context.AnalyzerConfigOptionsProvider
            .Select((options, _) =>
            {
                // The TFM may have no dot separator ("net472", "net8") or no numeric
                // major part at all ("netstandard2.0"), so parse defensively.
                if (options.GlobalOptions.TryGetValue("build_property.TargetFramework", out var tfm))
                {
                    var versionPart = tfm.StartsWith("net", StringComparison.Ordinal) ? tfm[3..] : tfm;
                    var majorSeparator = versionPart.IndexOf('.');
                    var majorPart = majorSeparator >= 0 ? versionPart[..majorSeparator] : versionPart;

                    if (int.TryParse(majorPart, out var version))
                    {
                        return version;
                    }
                }

                return 8;
            })
            .WithTrackingName(TrackingNames.DotnetVersion);

        var interceptionEnabledSetting = context.AnalyzerConfigOptionsProvider
            .Select((x, _) =>
                x.GlobalOptions.TryGetValue("build_property.EnableFusionMapperInterceptor", out var enableSwitch)
                && !enableSwitch.Equals("false", StringComparison.Ordinal))
            .WithTrackingName(TrackingNames.InterceptorsIsEnabled);

        var interceptionEnabled = interceptionEnabledSetting
                .Combine(csharpSufficient)
                .Combine(targetFrameworkProvider)
                .Select((t, _) => t.Left.Left && t.Left.Right && t.Right >= 9);

        var unmappedWarningsSuppressed = context.AnalyzerConfigOptionsProvider
            .Select(static (options, _) =>
                options.GlobalOptions.TryGetValue("build_property.FusionMapperSuppressUnmappedWarnings", out var value)
                && value.Equals("true", StringComparison.OrdinalIgnoreCase))
            .WithTrackingName(TrackingNames.UnmappedWarningsSuppressed);

        context.RegisterImplementationSourceOutput(anonymousLocations.Combine(interceptionEnabled).Combine(unmappedWarningsSuppressed), static (spc, input) =>
        {
            var ((diagnostic, interceptorsActive), suppressed) = input;

            if (suppressed && diagnostic.Descriptor.Id == UnmappedTargetMembersRuleId)
            {
                return;
            }

            // With interceptors the generator owns the call site, so an impossible
            // mapping is a compile error. Otherwise it is resolved by the runtime
            // fallback (and throws MappingException) — only a warning is justified.
            var descriptor = interceptorsActive
                ? diagnostic.Descriptor
                : diagnostic.Descriptor.Id switch
                {
                    IncompatibleMappingRuleId => IncompatibleMappingRuleRuntime,
                    UnsupportedInExpressionTreeRuleId => UnsupportedInExpressionTreeRuntime,
                    _ => diagnostic.Descriptor,
                };

            spc.ReportDiagnostic(Diagnostic.Create(descriptor, diagnostic.Location, diagnostic.MessageArgs.AsImmutableArray().OfType<object>().ToArray()));
        });

        var mapped = candidates
            .Where(static c => c.Source is { IsAnonymous: false } && c.Target is { IsAnonymous: false } && c.MappingCode.HasValue)
            .Select(static (c, _) => new Mapped(c.Kind, c.Source!, c.Target!, c.MappingCode!.Value))
            .Collect()
            .WithTrackingName(TrackingNames.Mapped);

        context.RegisterImplementationSourceOutput(mapped.Combine(csharpSufficient), static (spc, input) =>
        {
            var (candidates, csharpSufficient) = input;
            if (!csharpSufficient) return;
            if (candidates.Length == 0) return;

            var source = SourceEmitter.EmitMappers([.. candidates.Distinct()]);
            spc.AddSource("FusionMapper.g.cs", SourceText.From(source, Encoding.UTF8));

        });

        var initialized = candidates
            .Where(static c => c.Source is { IsAnonymous: false } && c.Target is { IsAnonymous: false } && c.MappingCode.HasValue)
            .Select(static (c, _) => new Initialized(c.Kind, c.Source!, c.Target!, c.IsInsideExpressionTree))
            .Collect()
            .WithTrackingName(TrackingNames.Initialized);

        context.RegisterImplementationSourceOutput(initialized.Combine(csharpSufficient).Combine(targetFrameworkProvider), static (spc, input) =>
        {
            var ((candidates, csharpSufficient), dotnetVersion) = input;
            if (!csharpSufficient) return;
            if (candidates.Length == 0) return;

            var initalizerSource = SourceEmitter.EmitInitializer(candidates, dotnetVersion);
            spc.AddSource("FusionMapper.Initializer.g.cs", SourceText.From(initalizerSource, Encoding.UTF8));

        });

        var interceptable = candidates
            .Where(static c => c.Source is { IsAnonymous: false } && c.Target is { IsAnonymous: false } && c.Interceptable is not null && !c.IsInsideExpressionTree)
            .Select(static (c, _) => new Interceptable(c.Kind, c.Source!, c.Target!, c.Interceptable!))
            .Collect()
            .WithTrackingName(TrackingNames.Intercepted);


        var accessorFields =
            context.CompilationProvider
            .Select(static (compilation, ct) => FusionAccessorMetadata.Resolve(compilation))
            .WithTrackingName(TrackingNames.AccessorFields);


        context.RegisterImplementationSourceOutput(interceptable.Combine(interceptionEnabled).Combine(accessorFields),
        static (spc, input) =>
        {
            var ((candidates, enabled), fields) = input;
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

            var interceptorStore = SourceEmitter.EmitInterceptors(candidates, fields);
            spc.AddSource("FusionMapper.Interceptors.g.cs", SourceText.From(interceptorStore, Encoding.UTF8));
        });
    }

    private static bool IsCandidate(SyntaxNode node, CancellationToken ct) =>
        node is InvocationExpressionSyntax
        {
            ArgumentList.Arguments.Count: <= 1,
            Expression: MemberAccessExpressionSyntax
            {                
                Name.Identifier.Value: "To",
            }
        };

    private static RawCandidate? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
    {
        if (ctx.Node is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax
                {
                    Name.Identifier.ValueText: "To"
                }
            } invocation)
        {
            return null;
        }

        if (invocation.ArgumentList.Arguments.Count > 1)
        {
            return null;
        }

        if (ctx.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method)
        {
            return null;
        }

        if (method.Name != "To")
        {
            return null;
        }

        if (method.TypeArguments.Length != 1)
        {
            return null;
        }

        if (method.ContainingType is not INamedTypeSymbol containingType)
        {
            return null;
        }

        if (containingType.TypeArguments.Length != 1)
        {
            return null;
        }

        if (containingType.Name is not (
            FusionSourceType or
            FusionProjectionType))
        {
            return null;
        }

        if (!IsFusionMapperNamespace(containingType.ContainingNamespace))
        {
            return null;
        }

        CallKind kind;
        if (containingType.Name == FusionSourceType)
        {
            if (method.Parameters.Length == 0)
            {
                kind = CallKind.SourceTo;
            }
            else if (method.Parameters.Length == 1)
            {
                kind = CallKind.SourceToExisting;
            }
            else
            {
                return null;
            }
        }
        else if (containingType.Name == FusionProjectionType)
        {
            kind = CallKind.ProjectionTo;
        }
        else
        {
            return null;
        }


        var sourceType = containingType.TypeArguments[0];
        var targetType = method.TypeArguments[0];

        if (IsUnsupported(sourceType) || IsUnsupported(targetType))
            return null;

        var location = ctx.Node.GetLocation();
        var isInsideExpresionTree = IsInsideExpressionTree(ctx.SemanticModel, invocation, ct);
        if (sourceType.IsAnonymousType || targetType.IsAnonymousType)
        {
            return new RawCandidate(
                kind, isInsideExpresionTree,
                null, null, null, null,
                ImmutableArray.Create(new GeneratorDiagnostic(AnonymousSourceRule, location))
            );
        }

        List<GeneratorDiagnostic> diagnostics = [];
        if (isInsideExpresionTree)
        {
            if (kind == CallKind.SourceToExisting)
            {
                diagnostics.Add(new(
                    UnsupportedInExpressionTree,
                    location,
                    sourceType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                    targetType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
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
            diagnostics.Add(new(IncompatibleMappingRule,
                location,
                sourceType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                targetType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                ex.Message));
        }

        if (mapping is ObjectMapping { UnmappedMemberNames.Length: > 0 } objectMapping)
        {
            diagnostics.Add(new(
                UnmappedTargetMembersRule,
                location,
                targetType.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                string.Join(", ", objectMapping.UnmappedMemberNames)));
        }

        var interceptLocation = isInsideExpresionTree
            ? null
            : ctx.SemanticModel.GetInterceptableLocation(invocation);
        return new RawCandidate(
            kind, isInsideExpresionTree,
            mapping?.SourceType, mapping?.TargetType,
            interceptLocation,
            code, diagnostics.ToImmutableArray()
            );

    }

    private static bool IsFusionMapperNamespace(INamespaceSymbol? ns)
    {
        return ns is
        {
            Name: "FusionMapper",
            ContainingNamespace.IsGlobalNamespace: true
        };
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

    private static bool IsInsideExpressionTree(
        SemanticModel model,
        SyntaxNode node,
        CancellationToken ct)
    {
        var maybeInside = false;

        for (var current = node.Parent; current is not null; current = current.Parent)
        {
            if (current is AnonymousFunctionExpressionSyntax or QueryExpressionSyntax)
            {
                maybeInside = true;
                break;
            }

            if (current is MemberDeclarationSyntax or AccessorDeclarationSyntax or AttributeSyntax)
            {
                return false;
            }
        }

        if (!maybeInside)
        {
            return false;
        }

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
                {
                    return true;
                }
            }

            if (current is QueryExpressionSyntax query && insideQueryBodyClause)
            {
                var typeInfo = model.GetTypeInfo(query, ct);

                if (IsQueryable(typeInfo.Type) || IsQueryable(typeInfo.ConvertedType))
                {
                    return true;
                }
            }

            if (current is MemberDeclarationSyntax or AccessorDeclarationSyntax or AttributeSyntax)
            {
                break;
            }
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