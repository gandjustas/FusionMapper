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

internal enum AggregateKind
{
    Count,
    Any,
    All,
    Sum,
    Average,
    Max,
    Min,
    First,
    Last,
    FirstOrDefault,
    LastOrDefault
}

internal sealed record AggregateMapping : Mapping
{
    public required AggregateKind Kind { get; init; }
    public required TypeModel ElementType { get; init; }

    /// <summary>
    /// Предикат для Where/Any/All.
    /// </summary>
    public required AggregatePredicate? Predicate { get; init; }

    /// <summary>
    /// Проекция элемента перед агрегатом.
    /// Для First/Last сюда может быть опущен элемент целиком.
    /// Для Sum/Max/Min сюда опускается селектор.
    /// </summary>
    public required AggregateProjection? Projection { get; init; }

    /// <summary>
    /// Финальный маппинг результата агрегата в target.
    /// </summary>
    public required Mapping? ResultMapping { get; init; }

    /// <summary>
    /// Для Count: можно использовать .Count вместо Enumerable.Count().
    /// </summary>
    public required bool UseCountProperty { get; init; }

    /// <summary>
    /// Для FirstOrDefault/LastOrDefault в non-nullable reference target.
    /// </summary>
    public required bool RequiresNullForgiving { get; init; }
}

internal sealed record AggregatePredicate(SourcePath Path);

internal sealed record AggregateProjection(SourcePath? Path, Mapping Mapping);

internal sealed record ObjectMapping : Mapping
{
    /// <summary>
    /// Конструктор уже выбран builder'ом.
    /// </summary>
    public required SelectedConstructor Constructor { get; init; }

    /// <summary>
    /// Все члены, которые можно использовать для creation/existing mutation.
    /// </summary>
    public required ImmutableArray<MemberBinding> Members { get; init; }

    public required ImmutableArray<MemberBinding> CreationMembers { get; init; }
}

readonly record struct SelectedConstructor
{
    public required ImmutableArray<ConstructorArgument> Arguments { get; init; }
    public required ImmutableHashSet<string> AssignedMemberNames { get; init; }
}

readonly record struct ConstructorArgument
{
    public required TypeModel ParameterType { get; init; }
    public required bool IsMapped { get; init; }
    public SourcePath? Source { get; init; }
    public Mapping? Value { get; init; }
}

readonly record struct SourcePath
{
    public required ImmutableArray<SourcePathSegment> Segments { get; init; }
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
    public required Mapping Value { get; init; }

    public required bool CanWrite { get; init; }

    /// <summary>
    /// Как вести себя при маппинге в существующий объект.
    /// </summary>
    public required MemberMutationKind MutationKind { get; init; }
}

internal enum MemberMutationKind
{
    Skip,
    Assign,
    MutateObject,
    MutateCollection
}

internal sealed record CollectionMapping : Mapping
{
    public required TypeModel ElementTypeName { get; init; }
    public required Mapping ElementMapping { get; init; }

    /// <summary>
    /// Конкретные стратегии создания и мутации коллекции.
    /// </summary>
    public required CollectionPlan Plan { get; init; }
}

readonly record struct CollectionPlan
{
    public required bool IsArray { get; init; }

    public required CollectionCreationKind MethodBodyCreation { get; init; }
    public required CollectionCreationKind ExpressionTreeCreation { get; init; }

    public required CollectionMutationKind Mutation { get; init; }
}

internal enum CollectionCreationKind
{
    Unsupported,

    /// <summary>
    /// Enumerable.ToArray
    /// </summary>
    Array,

    /// <summary>
    /// Enumerable.ToList
    /// </summary>
    List,

    /// <summary>
    /// [.. items]
    /// </summary>
    CollectionExpression,

    /// <summary>
    /// new Target(items)
    /// </summary>
    EnumerableConstructor,

    /// <summary>
    /// IIFE/выражение с AddRange.
    /// </summary>
    AddRangeClosure,

    /// <summary>
    /// IIFE/выражение с Add.
    /// </summary>
    AddLoopClosure,
}

internal enum CollectionMutationKind
{
    None,
    ClearAddRange,
    ClearAdd
}