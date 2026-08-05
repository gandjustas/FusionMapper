namespace FusionMapper.Tests;

public class AggregateMappingTests
{
    [Test]
    public async Task Map_Aggregates_Count_Any_Sum_Average_Max_Min()
    {
        var source = new AggregateSource();

        source.Items.Add(new ItemSource { Value = 1.5m });
        source.Items.Add(new ItemSource { Value = 2.5m });

        source.Prices.Add(10m);
        source.Prices.Add(20m);

        var result = source.Map().To<AggregateTarget>();

        await Assert.That(result.ItemsCount).IsEqualTo(2);
        await Assert.That(result.ItemsAny).IsTrue();
        await Assert.That(result.ItemsValueSum).IsEqualTo(4.0m);

        await Assert.That(result.PricesSum).IsEqualTo(30m);
        await Assert.That(result.PricesAverage).IsEqualTo(15m);
        await Assert.That(result.PricesMax).IsEqualTo(20m);
        await Assert.That(result.PricesMin).IsEqualTo(10m);
    }

    [Test]
    public async Task Map_Empty_Collection_Aggregates()
    {
        var source = new AggregateSource();

        var result = source.Map().To<EmptyAggregateTarget>();

        await Assert.That(result.ItemsCount).IsEqualTo(0);
        await Assert.That(result.ItemsAny).IsFalse();
        await Assert.That(result.ItemsValueSum).IsEqualTo(0m);
    }
}
