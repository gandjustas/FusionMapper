namespace FusionMapper.Tests;

public class NestedObjectMappingTests
{
    [Test]
    public async Task Map_Nested_Object_Property()
    {
        var source = new ProductSource
        {
            Name = "Laptop",
            Category = new ProductCategorySource
            {
                Type = "Electronics",
                Subtype = "Computers"
            }
        };

        var result = source.Map().To<ProductTarget>();

        await Assert.That(result.Name).IsEqualTo("Laptop");
        await Assert.That(result.Category).IsNotNull();
        await Assert.That(result.Category.Type).IsEqualTo("Electronics");
        await Assert.That(result.Category.Subtype).IsEqualTo("Computers");
    }

    [Test]
    public async Task Map_Nested_Object_With_Null_SubObject()
    {
        var source = new ProductSource
        {
            Name = "Book",
            Category = null
        };

        var result = source.Map().To<ProductTarget>();

        await Assert.That(result.Name).IsEqualTo("Book");
        await Assert.That(result.Category).IsNull();
    }

    [Test]
    public async Task Map_Multiple_Levels_Without_Flattening()
    {
        var source = new Level1Source
        {
            Name = "Root",
            Level2 = new Level2Source
            {
                Title = "Child",
                Level3 = new Level3Source
                {
                    Description = "Grandchild"
                }
            }
        };

        var result = source.Map().To<Level1Target>();

        await Assert.That(result.Name).IsEqualTo("Root");
        await Assert.That(result.Level2).IsNotNull();
        await Assert.That(result.Level2.Title).IsEqualTo("Child");
        await Assert.That(result.Level2.Level3).IsNotNull();
        await Assert.That(result.Level2.Level3.Description).IsEqualTo("Grandchild");
    }
}
