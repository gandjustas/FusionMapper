namespace FusionMapper.Tests;

public class CollectionMappingTests
{
    [Test]
    public async Task Map_Object_With_List_Property()
    {
        var source = new OrderSource();
        source.Items.Add(new ItemSource { Name = "A", Value = 1m });
        source.Items.Add(new ItemSource { Name = "B", Value = 2m });

        var result = source.Map().To<OrderTarget>();

        await Assert.That(result.Items.Count).IsEqualTo(2);
        await Assert.That(result.Items[0].Name).IsEqualTo("A");
        await Assert.That(result.Items[1].Name).IsEqualTo("B");
    }

    [Test]
    public async Task Map_Object_With_ReadOnly_List_Property()
    {
        var source = new OrderSource();
        source.Items.Add(new ItemSource { Name = "A" });
        source.Items.Add(new ItemSource { Name = "B" });

        var result = source.Map().To<OrderReadOnlyItemsTarget>();

        await Assert.That(result.Items.Count).IsEqualTo(2);
        await Assert.That(result.Items[0].Name).IsEqualTo("A");
        await Assert.That(result.Items[1].Name).IsEqualTo("B");
    }

    [Test]
    public async Task Map_Null_Collection_To_Null_Collection()
    {
        var source = new NullItemsSource
        {
            Items = null
        };

        var result = source.Map().To<NullItemsTarget>();

        await Assert.That(result.Items).IsNull();
    }

    [Test]
    public async Task Map_Source_List_To_Target_List()
    {
        var source = new List<ItemSource>
        {
            new() { Name = "A" },
            new() { Name = "B" }
        };

        var result = source.Map().To<List<ItemTarget>>();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Name).IsEqualTo("A");
        await Assert.That(result[1].Name).IsEqualTo("B");
    }

    [Test]
    public async Task Map_Source_Array_To_Target_Array()
    {
        var source = new[]
        {
            new ItemSource { Name = "A" },
            new ItemSource { Name = "B" }
        };

        var result = source.Map().To<ItemTarget[]>();

        await Assert.That(result.Length).IsEqualTo(2);
        await Assert.That(result[0].Name).IsEqualTo("A");
        await Assert.That(result[1].Name).IsEqualTo("B");
    }

    [Test]
    public async Task Map_Source_List_To_IReadOnlyList()
    {
        var source = new List<ItemSource>
        {
            new() { Name = "A" },
            new() { Name = "B" }
        };

        var result = source.Map().To<IReadOnlyList<ItemTarget>>();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Name).IsEqualTo("A");
        await Assert.That(result[1].Name).IsEqualTo("B");
    }

    [Test]
    public async Task Map_Source_List_To_IEnumerable()
    {
        var source = new List<ItemSource>
        {
            new() { Name = "A" },
            new() { Name = "B" }
        };

        var result = source.Map().To<IEnumerable<ItemTarget>>();

        await Assert.That(result.Count()).IsEqualTo(2);
        await Assert.That(result.First().Name).IsEqualTo("A");
        await Assert.That(result.Last().Name).IsEqualTo("B");
    }
}
