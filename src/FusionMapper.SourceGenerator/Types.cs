using System.Collections.Immutable;
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

readonly record struct GeneratorDiagnostic(DiagnosticDescriptor Descriptor, Location? Location, EquatableArray<string> MessageArgs)
{
    public GeneratorDiagnostic(DiagnosticDescriptor Descriptor, Location? Location, params string[] MessageArgs) : this(Descriptor, Location, MessageArgs.ToImmutableArray())
    {
    }
}

readonly record struct RawCandidate(
    CallKind Kind,
    bool IsInsideExpressionTree,
    TypeModel? Source,
    TypeModel? Target,
    InterceptableLocation? Interceptable,
    EquatableArray<string>? MappingCode,
    EquatableArray<GeneratorDiagnostic> Diagnostics 
    );

readonly record struct Mapped(
    CallKind Kind, 
    TypeModel Source, 
    TypeModel Target,
    EquatableArray<string> Code);

readonly record struct Initialized(
    CallKind Kind,
    TypeModel Source,
    TypeModel Target,
    bool IsInsideExpressionTree);

readonly record struct Interceptable(
    CallKind Kind,
    TypeModel Source,
    TypeModel Target,
    InterceptableLocation Location);


readonly record struct AccessorFieldNames(
    string SourceValueField,
    string ProjectionValueField,
    bool SourceValueFieldResolved,
    bool ProjectionValueFieldResolved)
{
    public static AccessorFieldNames Fallback { get; } = new(
        "<value>P",
        "<value>P",
        false,
        false);
}
