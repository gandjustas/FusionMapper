using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FusionMapper.SourceGeneration;

readonly record struct GeneratorOptions(bool IsEnabled, int DotnetVersion);

enum CallKind
{
    SourceTo,
    SourceToExisting,
    ProjectionTo
}

readonly record struct CallSite(string Path, int Line, int Column);

readonly record struct Candidate(
    Location Location,
    InterceptableLocation Interceptable,
    CallKind Kind,
    TypeModel Source,
    TypeModel Target,
    bool IsAnonymous);

readonly record struct TypeModel(
    string Signature,
    string Runtime,
    string Identifier,
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

    public string NullableAnnotatedIndentifier => Annotation == NullableAnnotation.NotAnnotated ? Identifier : (Identifier + "_Nullable");


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
    private static readonly SymbolDisplayFormat IdentifierFormat = new(
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.ExpandNullable
            | SymbolDisplayMiscellaneousOptions.ExpandValueTuple
    );

    public static TypeModel Create(ITypeSymbol type)
    {
        var isNullableValue =
            type.IsValueType &&
            type is INamedTypeSymbol named &&
            named.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;

        return new TypeModel(
            type.ToDisplayString(SignatureFormat),
            type.ToDisplayString(RuntimeFormat),
            MakeValidIdentifier(type.ToDisplayString(IdentifierFormat)),
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
