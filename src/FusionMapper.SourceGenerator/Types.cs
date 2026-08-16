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


readonly record struct RawCandidate(
    Location Location,
    InterceptableLocation Interceptable,
    CallKind Kind,
    ITypeSymbol SourceSymbol,
    ITypeSymbol TargetSymbol,
    TypeModel Source,
    TypeModel Target,
    bool IsInsideExpressionTree);

abstract record Candidate
{
    public required Location Location {get; init; }
    public required CallKind Kind {get; init; }
    public required TypeModel Source {get; init; }
    public required TypeModel Target { get; init; }
};

record Mapped : Candidate
{
    public bool IsInsideExpressionTree { get; init; } = false;
    public required Mapping Mapping { get; init; }
}

record Interceptable : Mapped
{
    public required InterceptableLocation InterceptableLocation { get; init; }
}

record MappingFailed : Candidate
{
    public required Exception Exception { get; init;  }
}


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



internal class ImmutableDictionaryComparer<T1, T2> : IEqualityComparer<ImmutableDictionary<T1, T2>> where T1 : IEquatable<T1> where T2 : IEquatable<T2>
{
#pragma warning disable S2743
    public static IEqualityComparer<ImmutableDictionary<(TypeModel Source, TypeModel Target), Mapping>> Default { get; } = new ImmutableDictionaryComparer<(TypeModel Source, TypeModel Target), Mapping>();
#pragma warning restore S2743

    public bool Equals(ImmutableDictionary<T1, T2> x, ImmutableDictionary<T1, T2> y)
    {
        if (ReferenceEquals(x, y))
            return true;

        if (x.Count != y.Count) return false;

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