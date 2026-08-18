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
    /// Старый селектор вида ItemsNameFirstOrDefault -> Name.
    /// </summary>
    public SourcePath? Selector { get; init; }

    /// <summary>
    /// Предикат для First/Last/FirstOrDefault/LastOrDefault.
    /// Например: ItemsActiveFirstOrDefault -> Active.
    /// </summary>
    public SourcePath? Predicate { get; init; }

    /// <summary>
    /// Путь внутри элемента для пост-агрегатного выражения.
    /// </summary>
    public SourcePath? PostSource { get; init; }

    /// <summary>
    /// Маппинг пост-агрегатного выражения.
    /// </summary>
    public Mapping? PostMapping { get; init; }

    /// <summary>
    /// Маппинг элемента коллекции в целевой тип.
    /// Используется для First/Last без селектора.
    /// </summary>
    public Mapping? ElementMapping { get; init; }

    /// <summary>
    /// Маппинг результата агрегата в целевой тип.
    /// </summary>
    public Mapping? ResultMapping { get; init; }

    /// <summary>
    /// Факт для Emitter: можно ли читать Count как свойство.
    /// </summary>
    public required bool SourceHasCountProperty { get; init; }
}

internal sealed record ObjectMapping : Mapping
{
    /// <summary>
    /// Все допустимые конструкторы.
    /// Выбор конкретного конструктора делает Emitter.
    /// </summary>
    public required EquatableArray<ConstructorCandidate> Constructors { get; init; }

    /// <summary>
    /// Все члены target, которые Builder смог сопоставить с source.
    /// Emitter сам решает, какие из них использовать для создания,
    /// какие для обновления, какие пропустить.
    /// </summary>
    public required EquatableArray<MemberBinding> Members { get; init; }

    public required EquatableArray<string> RequiredMemberNames { get; init; }
}

readonly record struct ConstructorCandidate
{
    public required EquatableArray<ConstructorParameter> Parameters { get; init; }

    public required bool SetsRequiredMembers { get; init; }

    /// <summary>
    /// Имена required/обычных членов, которые закрываются параметрами конструктора.
    /// </summary>
    public required EquatableArray<string> AssignedMemberNames { get; init; }
}

readonly record struct ConstructorParameter
{
    public required TypeModel ParameterType { get; init; }

    public required bool IsMapped { get; init; }

    public required bool CanUseDefault { get; init; }

    public SourcePath? Source { get; init; }

    public Mapping? Value { get; init; }
}

readonly record struct SourcePath
{
    public required EquatableArray<SourcePathSegment> Segments { get; init; }
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

    public required bool IsRequired { get; init; }

    public required bool IsInitOnly { get; init; }

    /// <summary>
    /// Можно ли прочитать член target.
    /// Нужно для existing-mapping: mutate existing object/collection.
    /// </summary>
    public required bool CanRead { get; init; }

    /// <summary>
    /// Можно ли записать член target.
    /// </summary>
    public required bool CanWrite { get; init; }

    /// <summary>
    /// Target member является value type.
    /// Emitter использует это, чтобы не пытаться мутировать struct inplace.
    /// </summary>
    public required bool IsTargetMemberValueType { get; init; }
}

internal sealed record CollectionMapping : Mapping
{
    public required TypeModel ElementTypeName { get; init; }

    public required Mapping ElementMapping { get; init; }

    public required CollectionCapabilities Capabilities { get; init; }
}

readonly record struct CollectionCapabilities
{
    public required bool IsArray { get; init; }
    public required bool IsGenericList { get; init; }
    public required bool IsKnownCollectionInterface { get; init; }

    public required bool HasClearMethod { get; init; }
    public required bool HasAddMethod { get; init; }
    public required bool HasAddRangeMethod { get; init; }

    public required bool HasParameterlessConstructor { get; init; }
    public required bool HasEnumerableConstructor { get; init; }

    public required bool HasCountProperty { get; init; }
}