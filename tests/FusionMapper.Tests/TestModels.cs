namespace FusionMapper.Tests;

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

public class CtorSource
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
}

public class CtorTarget(string name, int age)
{
    public string Name { get; } = name;
    public int Age { get; } = age;
}

public class CtorMissingSource
{
    public string Name { get; set; } = string.Empty;
}

public class CtorMissingTarget(string missing)
{
    public string Missing { get; } = missing;
}

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

public class Level1Source
{
    public string Name { get; set; } = string.Empty;
    public Level2Source? Level2 { get; set; }
    public ExtraData? ExtraData { get; set; }
}

public class Level2Source
{
    public string Title { get; set; } = string.Empty;
    public Level3Source? Level3 { get; set; }
}

public class Level3Source
{
    public string Description { get; set; } = string.Empty;
    public Level4Source? Level4 { get; set; }
}

public class Level4Source
{
    public int Value { get; set; }
}

public class ExtraData
{
    public string Metadata { get; set; } = string.Empty;
}

public class Level1Target
{
    public string Name { get; set; } = string.Empty;
    public Level2Target? Level2 { get; set; }
    public ExtraData? ExtraData { get; set; }
}

public class Level2Target
{
    public string Title { get; set; } = string.Empty;
    public Level3Target? Level3 { get; set; }
}

public class Level3Target
{
    public string Description { get; set; } = string.Empty;
    public Level4Target? Level4 { get; set; }
}

public class Level4Target
{
    public int Value { get; set; }
}

public class EmployeeSource
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public List<string>? Skills { get; set; }
}

public class EmployeeTarget
{
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public List<string>? Skills { get; set; }
}

public class DepartmentSource
{
    public string Name { get; set; } = string.Empty;
    public List<EmployeeSource>? Employees { get; set; }
    public Dictionary<string, DepartmentSource>? Departments { get; set; }
}

public class DepartmentTarget
{
    public string Name { get; set; } = string.Empty;
    public List<EmployeeTarget>? Employees { get; set; }
    public Dictionary<string, DepartmentTarget>? Departments { get; set; }
}

public class AddressSource
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}

public class AddressTarget
{
    public string Street { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
}

public class MixedTypeSource
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Price { get; set; }
    public bool Active { get; set; }
    public string? Description { get; set; }
}

public class MixedTypeTarget
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
    public decimal Price { get; set; }
    public bool Active { get; set; }
    public string? Description { get; set; }
}

public class ProductCategorySource
{
    public string Type { get; set; } = string.Empty;
    public string? Subtype { get; set; }
}

public class ProductCategoryTarget
{
    public string Type { get; set; } = string.Empty;
    public string? Subtype { get; set; }
}

public class ProductSource
{
    public string Name { get; set; } = string.Empty;
    public ProductCategorySource? Category { get; set; }
    public List<SupplierSource>? SupplierCosts { get; set; }
}

public class ProductTarget
{
    public string Name { get; set; } = string.Empty;
    public ProductCategoryTarget? Category { get; set; }
    public List<ProductTarget>? SupplierCosts { get; set; }
}

public class SupplierSource
{
    public string Name { get; set; } = string.Empty;
    public AddressSource? Address { get; set; }
    public List<ProductSource>? Products { get; set; }
}

public class SupplierTarget
{
    public string Name { get; set; } = string.Empty;
    public AddressTarget? Address { get; set; }
    public List<ProductTarget>? Products { get; set; }
}

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
    public List<CycleSource> Children { get; set; } = [];
}

public class CycleTarget
{
    public string Name { get; set; } = string.Empty;
    public CycleTarget? Parent { get; set; }
    public List<CycleTarget> Children { get; set; } = [];
}

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
    public List<ItemSource> Items { get; set; } = [];
}

public class OrderTarget
{
    public List<ItemTarget> Items { get; set; } = [];
}

public class OrderReadOnlyItemsTarget
{
    public List<ItemTarget> Items { get; } = [];
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
    public List<ItemSource> Items { get; set; } = [];
}

public class ReadOnlyListTarget
{
    public IReadOnlyList<ItemTarget> Items { get; set; } = [];
}

public class AggregateSource
{
    public List<ItemSource> Items { get; set; } = [];
    public List<decimal> Prices { get; set; } = [];
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
