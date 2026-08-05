namespace FusionMapper.Tests;

public class ProjectionCollectionTests
{
    [Test]
    public async Task Project_Object_With_List_Property()
    {
        var source = new[]
        {
            new OrderSource
            {
                Items =
                {
                    new ItemSource { Name = "A", Value = 1m },
                    new ItemSource { Name = "B", Value = 2m }
                }
            }
        }.AsQueryable();

        var result = source
            .Project()
            .To<OrderTarget>()
            .ToList();

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Items.Count).IsEqualTo(2);
        await Assert.That(result[0].Items[0].Name).IsEqualTo("A");
        await Assert.That(result[0].Items[1].Name).IsEqualTo("B");
    }

    [Test]
    public async Task Project_Object_With_Null_List_Property()
    {
        var source = new[]
        {
            new NullItemsSource
            {
                Items = null
            }
        }.AsQueryable();

        var result = source
            .Project()
            .To<NullItemsTarget>()
            .ToList();

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Items).IsNull();
    }
}
