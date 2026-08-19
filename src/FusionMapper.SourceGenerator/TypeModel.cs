using System.Text;
using Microsoft.CodeAnalysis;

namespace FusionMapper.SourceGenerator;

record TypeModel(
    string FullName,
    string Signature,
    string Runtime,
    string SafeIdentifier,
    bool IsReference,
    bool IsValueType,
    bool IsNullableValue,
    NullableAnnotation Annotation,
    bool IsAnonymous,
    string? NullableUnderlyingRuntime)
{
    public bool IsNullableByNullability =>
        IsNullableValue ||
        (IsReference && Annotation is NullableAnnotation.Annotated or NullableAnnotation.None);

    public bool CanBeNullRuntime =>
        IsReference || IsNullableValue;

    public string NullableAnnotatedIdentifier => Annotation == NullableAnnotation.NotAnnotated ? SafeIdentifier : (SafeIdentifier + "_Nullable");


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

        string? nullableUnderlyingRuntime = null;

        if (isNullableValue && type is INamedTypeSymbol nullableNamed)
        {
            nullableUnderlyingRuntime = nullableNamed.TypeArguments[0].ToDisplayString(RuntimeFormat);
        }

        return new TypeModel(
            type.ToDisplayString(FullNameFormat),
            type.ToDisplayString(SignatureFormat),
            type.ToDisplayString(RuntimeFormat),
            MakeValidIdentifier(type.ToDisplayString(ExpandedIdentifierFormat)),
            type.IsReferenceType,
            type.IsValueType,
            isNullableValue,
            type.NullableAnnotation,
            type.IsAnonymousType,
            nullableUnderlyingRuntime);
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
