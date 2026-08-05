namespace FusionMapper.Tests;

public class RecursiveMappingTests
{
    [Test]
    public async Task Map_Direct_Recursive_Type_Throws()
    {
        var source = new NodeSource
        {
            Name = "Root",
            Child = new NodeSource
            {
                Name = "Child"
            }
        };

        await Assert.That(() => source.Map().To<NodeTarget>())
            .Throws<MappingException>();
    }

    [Test]
    public async Task Map_Indirect_Recursive_Type_Throws()
    {
        var source = new ProductSource
        {
            Name = "Product",
            SupplierCosts =
            [
                new() {
                    Name = "Supplier",
                    Products =
                    [
                        new() { Name = "NestedProduct" }
                    ]
                }
            ]
        };

        // ProductSource -> ProductTarget, SupplierSource -> SupplierTarget,
        // при этом SupplierSource.Products ссылается на ProductSource,
        // что создаёт косвенную рекурсию.
        await Assert.That(() => source.Map().To<ProductTarget>())
            .Throws<MappingException>();
    }

    [Test]
    public async Task Map_Recursive_Collection_Element_Throws()
    {
        var source = new CycleSource
        {
            Name = "Parent",
            Children =
            [
                new() { Name = "Child" }
            ]
        };

        // CycleSource.Children содержит элементы того же типа,
        // что и контейнер → рекурсивный маппинг.
        await Assert.That(() => source.Map().To<CycleTarget>())
            .Throws<MappingException>();
    }
}
