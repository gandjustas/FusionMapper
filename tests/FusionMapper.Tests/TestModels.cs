namespace FusionMapper.Tests;

#region Simple

public class SimpleSource
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class SimpleTarget
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

#endregion

#region Case sensitivity

public class CaseSource
{
    public string? nAmE { get; set; }
    public int vAlUe { get; set; }
}

public class CaseTarget
{
    public string? Name { get; set; }
    public int Value { get; set; }
}

public class ExactWinsSource
{
    public string Name { get; set; } = "exact";
    public string NAME { get; set; } = "upper";
}

public class ExactWinsTarget
{
    public string Name { get; set; } = string.Empty;
}

public class AmbiguousSource
{
    public string Name { get; set; } = "a";
    public string NAME { get; set; } = "b";
}

public class AmbiguousTarget
{
    public string? name { get; set; }
}

#endregion

#region Flattening

public class NestedObject
{
    public string City { get; set; } = string.Empty;
}

public class FlattenSource
{
    public NestedObject Nested { get; set; } = new();
}

public class FlattenTarget
{
    public string NestedCity { get; set; } = string.Empty;
}

public class FlattenNullSource
{
    public NestedObject? Nested { get; set; }
}

public class FlattenNullTarget
{
    public string? NestedCity { get; set; }
}

#endregion

#region Constructors

public class CtorSource
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class CtorTarget
{
    public CtorTarget(string name, int age)
    {
        Name = name;
        Age = age;
    }

    public string Name { get; }
    public int Age { get; }
}

public class CtorMissingSource
{
    public string Name { get; set; } = string.Empty;
}

public class CtorMissingTarget
{
    public CtorMissingTarget(string missing)
    {
        Missing = missing;
    }

    public string Missing { get; }
}

#endregion

#region Records

public record RecordSource(string Name, int Value);

public record RecordTarget(string Name, int Value);

public class RecordExtraSource
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
}

public record RecordExtraTarget(string Name)
{
    public string? Description { get; init; }
}

#endregion

#region Required / init

public class RequiredSource
{
    public string Name { get; set; } = string.Empty;
}

public class RequiredTarget
{
    public required string Name { get; set; }
}

public class RequiredMissingSource
{
    public string Title { get; set; } = string.Empty;
}

public class InitSource
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class InitTarget
{
    public required string Name { get; init; }
    public int Value { get; init; }
}

#endregion

#region Nullable

public class NullableSource
{
    public int? Value { get; set; }
}

public class NullableTarget
{
    public int? Value { get; set; }
}

#endregion

#region Recursion / cycles

public class NodeSource
{
    public string Name { get; set; } = string.Empty;
    public NodeSource? Child { get; set; }
}

public class NodeTarget
{
    public string Name { get; set; } = string.Empty;
    public NodeTarget? Child { get; set; }
}

public class CycleSource
{
    public string Name { get; set; } = string.Empty;
    public CycleSource? Parent { get; set; }
    public List<CycleSource> Children { get; set; } = new();
}

public class CycleTarget
{
    public string Name { get; set; } = string.Empty;
    public CycleTarget? Parent { get; set; }
    public List<CycleTarget> Children { get; set; } = new();
}

#endregion

#region Collections

public class ItemSource
{
    public string Name { get; set; } = string.Empty;
    public decimal Value { get; set; }
}

public class ItemTarget
{
    public string Name { get; set; } = string.Empty;
    public decimal Value { get; set; }
}

public class OrderSource
{
    public List<ItemSource> Items { get; set; } = new();
}

public class OrderTarget
{
    public List<ItemTarget> Items { get; set; } = new();
}

public class OrderReadOnlyItemsTarget
{
    public List<ItemTarget> Items { get; } = new();
}

public class NullItemsSource
{
    public List<ItemSource>? Items { get; set; }
}

public class NullItemsTarget
{
    public List<ItemTarget>? Items { get; set; }
}

public class ReadOnlyListSource
{
    public List<ItemSource> Items { get; set; } = new();
}

public class ReadOnlyListTarget
{
    public IReadOnlyList<ItemTarget> Items { get; set; } = new List<ItemTarget>();
}

#endregion

#region Aggregates

public class AggregateSource
{
    public List<ItemSource> Items { get; set; } = new();
    public List<decimal> Prices { get; set; } = new();
}

public class AggregateTarget
{
    public int ItemsCount { get; set; }
    public bool ItemsAny { get; set; }
    public decimal ItemsValueSum { get; set; }

    public decimal PricesSum { get; set; }
    public decimal PricesAverage { get; set; }
    public decimal PricesMax { get; set; }
    public decimal PricesMin { get; set; }
}

public class EmptyAggregateTarget
{
    public int ItemsCount { get; set; }
    public bool ItemsAny { get; set; }
    public decimal ItemsValueSum { get; set; }
}

#endregion
