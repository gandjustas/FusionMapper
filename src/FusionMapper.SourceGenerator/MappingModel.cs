using System.Collections.Immutable;

namespace FusionMapper.SourceGenerator;

internal abstract record Mapping
{
    public required TypeModel SourceType { get; init; }
    public required TypeModel TargetType { get; init; }
}

internal enum AssignmentKind
{
    SameType,
    ImplicitConversion,
    ExplicitCast,
    EnumToString,
    StringToEnum
}

internal sealed record AssignMapping : Mapping
{
    public required AssignmentKind Kind { get; init; }
}

internal sealed record ObjectMapping : Mapping
{
    public required ObjectConstructor Constructor { get; init; }

    // Используется при создании нового объекта:
    // new Target { Member = ... }
    public required ImmutableArray<MemberBinding> Bindings { get; init; }

}
readonly record struct ObjectConstructor
{
    public required ImmutableArray<ConstructorArgument> Arguments { get; init; }
}

readonly record struct ConstructorArgument
{
    public required TypeModel ArgumentType { get; init; }

    public required bool IsDefault { get; init; }

    public SourcePath? Source { get; init; }
    public Mapping? Value { get; init; }
}

readonly record struct SourcePath
{
    public required ImmutableArray<SourcePathSegment> Segments { get; init; }

    public TypeModel Type => Segments[^1].Type;
}

readonly record struct SourcePathSegment
{
    public required string MemberName { get; init; }
    public required TypeModel Type { get; init; }
}

readonly record struct MemberBinding
{
    public required string TargetMemberName { get; init; }

    public required SourcePath Source { get; init; }

    // Как маппится финальное значение source member -> target member.
    public required Mapping Value { get; init; }

    public required bool IsRequired{ get; init; }
    public required bool IsInitOnly { get; init; }
}

internal sealed record CollectionMapping : Mapping
{
    public required TypeModel ElementTypeName { get; init; }
    public required bool HasClearMethod { get; init; }
    public required bool HasAddMethod { get; init; }
    public required bool HasAddRangeMethod { get; init; }

    public required Mapping ElementMapping { get; init; }


}