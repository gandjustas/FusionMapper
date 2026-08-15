using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FusionMapper.SourceGenerator;

readonly record struct GeneratorOptions(bool IsEnabled, int DotnetVersion);

enum CallKind
{
    SourceTo,
    SourceToExisting,
    ProjectionTo
}


readonly record struct RawCandidate(
    Location Location,
    InterceptableLocation Interceptable,
    CallKind Kind,
    ITypeSymbol SourceSymbol,
    ITypeSymbol TargetSymbol,
    TypeModel Source,
    TypeModel Target,
    bool IsInsideExpressionTree);

readonly record struct Interceptable(
    InterceptableLocation Location,
    CallKind Kind,
    TypeModel Source,
    TypeModel Target);

readonly record struct TypeModel(
    string FullName,
    string Signature,
    string Runtime,
    string SafeIdentifier,
    bool IsReference,
    bool IsValueType,
    bool IsNullableValue,
    NullableAnnotation Annotation,
    bool IsAnonymous)
{
    public bool IsNullableByNullability =>
        IsNullableValue ||
        (IsReference && Annotation is NullableAnnotation.Annotated or NullableAnnotation.None);

    public bool CanBeNullRuntime =>
        IsReference || IsNullableValue;

    public string NullableAnnotatedIndentifier => Annotation == NullableAnnotation.NotAnnotated ? SafeIdentifier : (SafeIdentifier + "_Nullable");


    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly SymbolDisplayFormat RuntimeFormat = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions:
            SymbolDisplayMiscellaneousOptions.UseSpecialTypes |
            SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    private static readonly SymbolDisplayFormat ExpandedIdentifierFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.ExpandNullable
            | SymbolDisplayMiscellaneousOptions.ExpandValueTuple
    );

    private static readonly SymbolDisplayFormat FullNameFormat = new(
    typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
    genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
    miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
);

    public static TypeModel Create(ITypeSymbol type)
    {
        var isNullableValue =
            type.IsValueType &&
            type is INamedTypeSymbol named &&
            named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;

        return new TypeModel(
            type.ToDisplayString(FullNameFormat),
            type.ToDisplayString(SignatureFormat),
            type.ToDisplayString(RuntimeFormat),
            MakeValidIdentifier(type.ToDisplayString(ExpandedIdentifierFormat)),
            type.IsReferenceType,
            type.IsValueType,
            isNullableValue,
            type.NullableAnnotation,
            type.IsAnonymousType);
    }

    private static string MakeValidIdentifier(string candidate)
    {
        if (string.IsNullOrEmpty(candidate))
            return "_";

        
        // Заменяем все символы, кроме букв, цифр и подчёркивания, на '_'
        var sb = new StringBuilder(candidate.Length);
        foreach (char c in candidate)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
            else
                sb.Append('_');
        }

        return sb.ToString();
    }

}

internal record Mapping
{
}

internal class ImmutableDictionaryComparer<T1, T2> : IEqualityComparer<ImmutableDictionary<T1, T2>> where T1 : IEquatable<T1> where T2 : IEquatable<T2>
{
#pragma warning disable S2743
    public static IEqualityComparer<ImmutableDictionary<(TypeModel Source, TypeModel Target), Mapping>> Default { get; } = new ImmutableDictionaryComparer<(TypeModel Source, TypeModel Target), Mapping>();
#pragma warning restore S2743

    public bool Equals(ImmutableDictionary<T1, T2> x, ImmutableDictionary<T1, T2> y)
    {
        if (ReferenceEquals(x, y))
            return true;
        
        if(x.Count != y.Count) return false;

        foreach (var kvp in x)
        {
            if (!y.TryGetValue(kvp.Key, out var value) || !kvp.Value.Equals(value))
            {
                return false;
            }
        }
        return true;
    }

    public int GetHashCode(ImmutableDictionary<T1, T2> obj)
    {
        HashCode hash = new();
        foreach (var kvp in obj)
        {
            hash.Add(kvp.Key);
            hash.Add(kvp.Value);
        }
        return hash.ToHashCode();
    }
}