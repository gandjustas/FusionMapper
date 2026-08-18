using System.Collections.Immutable;

namespace FusionMapper.SourceGenerator;

readonly struct EquatableArray<T>(ImmutableArray<T> array) : IEquatable<EquatableArray<T>> where T : IEquatable<T>
{
    private readonly ImmutableArray<T> array = array;

    public ImmutableArray<T> AsImmutableArray() => array;

    public bool Equals(EquatableArray<T> other) => array.AsSpan().SequenceEqual(other.array.AsSpan());

    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    public override int GetHashCode()
    {
        HashCode hash = new();
        foreach (var item in array)
        {
            hash.Add(item);
        }
        return hash.ToHashCode();
    }

    public static implicit operator EquatableArray<T>(ImmutableArray<T> array) => new(array);
    public static implicit operator ImmutableArray<T>(EquatableArray<T> array) => array.array;
}
