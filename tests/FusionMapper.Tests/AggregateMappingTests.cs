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

    [Test]
    public async Task Map_Deep_Flattening_Aggregates_Count_Any_Sum_Average_Max_Min()
    {
        var source = new DeepAggregateSource();

        source.Customer.Order.Lines.Add(new DeepOrderLineSource { Amount = 10m });
        source.Customer.Order.Lines.Add(new DeepOrderLineSource { Amount = 20m });

        var result = source.Map().To<DeepAggregateTarget>();

        await Assert.That(result.CustomerOrderLinesCount).IsEqualTo(2);
        await Assert.That(result.CustomerOrderLinesAny).IsTrue();
        await Assert.That(result.CustomerOrderLinesAmountSum).IsEqualTo(30m);
        await Assert.That(result.CustomerOrderLinesAmountAverage).IsEqualTo(15m);
        await Assert.That(result.CustomerOrderLinesAmountMax).IsEqualTo(20m);
        await Assert.That(result.CustomerOrderLinesAmountMin).IsEqualTo(10m);
    }

    [Test]
    public async Task Map_Deep_Flattening_Empty_Collection_Aggregates()
    {
        var source = new DeepAggregateSource();

        var result = source.Map().To<DeepEmptyAggregateTarget>();

        await Assert.That(result.CustomerOrderLinesCount).IsEqualTo(0);
        await Assert.That(result.CustomerOrderLinesAny).IsFalse();
        await Assert.That(result.CustomerOrderLinesAmountSum).IsEqualTo(0m);
    }

    [Test]
    public async Task Map_Deep_Flattening_Aggregate_Sum_Exact_Scenario()
    {
        var source = new DeepAggregateSource();

        source.Customer.Order.Lines.Add(new DeepOrderLineSource { Amount = 1.5m });
        source.Customer.Order.Lines.Add(new DeepOrderLineSource { Amount = 2.25m });

        var result = new DeepAggregateTarget
        {
            CustomerOrderLinesAmountSum = source.Customer.Order.Lines.Sum(l => l.Amount)
        };

        var mapped = source.Map().To<DeepAggregateTarget>();

        await Assert.That(mapped.CustomerOrderLinesAmountSum)
            .IsEqualTo(result.CustomerOrderLinesAmountSum)
            .And.IsEqualTo(3.75m);
    }

    public class DeepOrderLineSource
    {
        public decimal Amount { get; set; }
    }

    public class DeepOrderSource
    {
        public List<DeepOrderLineSource> Lines { get; set; } = [];
    }

    public class DeepCustomerSource
    {
        public DeepOrderSource Order { get; set; } = new();
    }

    public class DeepAggregateSource
    {
        public DeepCustomerSource Customer { get; set; } = new();
    }

    public class DeepAggregateTarget
    {
        public int CustomerOrderLinesCount { get; set; }
        public bool CustomerOrderLinesAny { get; set; }
        public decimal CustomerOrderLinesAmountSum { get; set; }
        public decimal CustomerOrderLinesAmountAverage { get; set; }
        public decimal CustomerOrderLinesAmountMax { get; set; }
        public decimal CustomerOrderLinesAmountMin { get; set; }
    }

    public class DeepEmptyAggregateTarget
    {
        public int CustomerOrderLinesCount { get; set; }
        public bool CustomerOrderLinesAny { get; set; }
        public decimal CustomerOrderLinesAmountSum { get; set; }
    }
}
