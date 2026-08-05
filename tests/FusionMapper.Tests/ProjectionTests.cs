namespace FusionMapper.Tests;

public class ProjectionTests
{
    [Test]
    public async Task Project_Simple_Objects()
    {
        var source = new[]
        {
            new SimpleSource { Name = "A", Value = 1 },
            new SimpleSource { Name = "B", Value = 2 }
        }.AsQueryable();

        var result = source
            .Project()
            .To<SimpleTarget>()
            .ToList();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Name).IsEqualTo("A");
        await Assert.That(result[1].Name).IsEqualTo("B");
    }

    [Test]
    public async Task Project_With_Flattening_And_Null()
    {
        var source = new[]
        {
            new FlattenNullSource
            {
                Nested = new NestedObject
                {
                    City = "X"
                }
            },
            new FlattenNullSource
            {
                Nested = null
            }
        }.AsQueryable();

        var result = source
            .Project()
            .To<FlattenNullTarget>()
            .ToList();

        await Assert.That(result[0].NestedCity).IsEqualTo("X");
        await Assert.That(result[1].NestedCity).IsNull();
    }

    [Test]
    public async Task Project_Record_To_Record()
    {
        var source = new[]
        {
            new RecordSource("A", 1)
        }.AsQueryable();

        var result = source
            .Project()
            .To<RecordTarget>()
            .ToList();

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Name).IsEqualTo("A");
        await Assert.That(result[0].Value).IsEqualTo(1);
    }

    [Test]
    public async Task Project_With_Aggregates()
    {
        var sourceItem = new AggregateSource();

        sourceItem.Items.Add(new ItemSource { Value = 1.5m });
        sourceItem.Items.Add(new ItemSource { Value = 2.5m });

        sourceItem.Prices.Add(10m);
        sourceItem.Prices.Add(20m);

        var source = new[]
        {
            sourceItem
        }.AsQueryable();

        var result = source
            .Project()
            .To<AggregateTarget>()
            .ToList();

        await Assert.That(result.Count).IsEqualTo(1);

        var item = result[0];

        await Assert.That(item.ItemsCount).IsEqualTo(2);
        await Assert.That(item.ItemsAny).IsTrue();
        await Assert.That(item.ItemsValueSum).IsEqualTo(4.0m);

        await Assert.That(item.PricesSum).IsEqualTo(30m);
        await Assert.That(item.PricesAverage).IsEqualTo(15m);
        await Assert.That(item.PricesMax).IsEqualTo(20m);
        await Assert.That(item.PricesMin).IsEqualTo(10m);
    }

    [Test]
    public async Task Project_Does_Not_Contain_Map_Method_Calls()
    {
        var source = new[]
        {
            new SimpleSource { Name = "A", Value = 1 }
        }.AsQueryable();

        var projected = source
            .Project()
            .To<SimpleTarget>();

        var containsMap = ExpressionHelper.ContainsMethodName(projected.Expression, "Map");

        await Assert.That(containsMap).IsFalse();
    }

    [Test]
    public async Task Project_Required_Member_Missing_Source_Throws()
    {
        var source = new[]
        {
            new RequiredMissingSource()
        }.AsQueryable();

        await Assert.That(() =>
            source
                .Project()
                .To<RequiredTarget>()
                .ToList()
        ).Throws<MappingException>();
    }

    [Test]
    public async Task Project_Cyclic_Graph_Throws_By_Default()
    {
        var parent = new CycleSource
        {
            Name = "parent"
        };

        var child = new CycleSource
        {
            Name = "child",
            Parent = parent
        };

        parent.Children.Add(child);

        var source = new[]
        {
            parent
        }.AsQueryable();

        await Assert.That(() =>
            source
                .Project()
                .To<CycleTarget>()
                .ToList()
        ).Throws<MappingException>();
    }
}
