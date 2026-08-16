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
        
        var r1 = global::System.Linq.Queryable.Select<global::FusionMapper.Tests.NullItemsSource, global::FusionMapper.Tests.NullItemsTarget>(source, (source => new global::FusionMapper.Tests.NullItemsTarget() { Items = (source.Items == null ? default : global::System.Linq.Enumerable.ToList<global::FusionMapper.Tests.ItemTarget>(global::System.Linq.Enumerable.Select(source.Items, static __item => new global::FusionMapper.Tests.ItemTarget() { Name = __item.Name, Value = __item.Value }))) }));
        var r2 = global::System.Linq.Queryable.Select<global::FusionMapper.Tests.NullItemsSource, global::FusionMapper.Tests.NullItemsTarget>(source, (source => new global::FusionMapper.Tests.NullItemsTarget() { Items = (source.Items == null ? default : global::System.Linq.Enumerable.ToList<global::FusionMapper.Tests.ItemTarget>(global::System.Linq.Enumerable.Select(source.Items, static __item => new global::FusionMapper.Tests.ItemTarget() { Name = __item.Name, Value = __item.Value }))) })); 
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Items).IsNull();
    }
}
