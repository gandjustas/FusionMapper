using System.Collections.Immutable;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Transactions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;
using Microsoft.CodeAnalysis.Text;

namespace FusionMapper.SourceGeneration;

[Generator(LanguageNames.CSharp)]
public sealed class FusionMapperInterceptorGenerator : IIncrementalGenerator
{
    public const string FusionSourceType = "FusionSource";
    public const string FusionProjectionType = "FusionProjection";

    private static readonly DiagnosticDescriptor AnonymousSourceRule = new(
        id: "FMAP002",
        title: "FusionMapper cannot intercept anonymous source",
        messageFormat: "Cannot generate an interceptor for FusionMapper call because the source type is anonymous",
        category: "FusionMapper",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);



    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var csharpSufficient = context.CompilationProvider
            .Select((x, _) => x is CSharpCompilation { LanguageVersion: LanguageVersion.Default or >= LanguageVersion.CSharp12 })
            .WithTrackingName(TrackingNames.Settings);

        IncrementalValueProvider<int> targetFrameworkProvider = context.AnalyzerConfigOptionsProvider
            .Select((options, _) =>
            {
                // Fetch TargetFramework (e.g., "net8.0")
                if (options.GlobalOptions.TryGetValue("build_property.TargetFramework", out var tfm)
                   && int.TryParse(tfm[3..tfm.IndexOf('.')], out var version))
                {
                    return version;
                }
                return 8; //Minimum version for C#12
            });

        var options = csharpSufficient.Combine(targetFrameworkProvider)
            .Select((t, _) => new GeneratorOptions(t.Left, t.Right));


        var candidates = context.SyntaxProvider
             .CreateSyntaxProvider(IsCandidate, Transform)
             .Where(static c => c is not null)
             .Select(static (c, _) => c!.Value)
             .WithTrackingName(TrackingNames.Candidates);

        // Use the provider inside your source output register step

        context.RegisterSourceOutput(candidates.Collect().Combine(options), static (spc, candidate) =>
        {
            var (candidates, options) = candidate;

            if (!options.IsEnabled) return;

            foreach (var c in candidates.Where(c => c.IsAnonymous))
            {
                spc.ReportDiagnostic(Diagnostic.Create(AnonymousSourceRule, c.Location));
            }

            if(options.DotnetVersion >= 10 && candidates.Length > 0)
            {
                spc.AddSource("FusionMapperInterceptors.g.cs", SourceText.From(InterceptorGenerator.Execute(candidates.Where(c => !c.IsAnonymous)), Encoding.UTF8));
            }
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

    private static Candidate? Transform(GeneratorSyntaxContext ctx, CancellationToken ct)
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

            if (IsInsideExpressionTree(ctx.SemanticModel, invocation, ct))
                return null;

            return new(
                ctx.Node.GetLocation(),
                location,
                kind,
                TypeModel.Create(sourceType),
                TypeModel.Create(targetType),
                sourceType.IsAnonymousType);
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
