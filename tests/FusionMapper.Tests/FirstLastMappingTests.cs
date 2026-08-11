using FusionMapper;

namespace FusionMapper.Tests;

public class FirstLastMappingTests
{
    [Test]
    public async Task Map_First_FirstOrDefault_Last_LastOrDefault()
    {
        var source = new AggregateSource();

        source.Items.Add(new ItemSource { Name = "First", Value = 1m });
        source.Items.Add(new ItemSource { Name = "Last", Value = 2m });

        source.Prices.Add(10m);
        source.Prices.Add(20m);

        var result = source.Map().To<FirstLastTarget>();

        await Assert.That(result.ItemsFirst.Name).IsEqualTo("First");
        await Assert.That(result.ItemsFirst.Value).IsEqualTo(1m);

        await Assert.That(result.ItemsFirstOrDefault).IsNotNull();
        await Assert.That(result.ItemsFirstOrDefault!.Name).IsEqualTo("First");
        await Assert.That(result.ItemsFirstOrDefault.Value).IsEqualTo(1m);

        await Assert.That(result.ItemsLast.Name).IsEqualTo("Last");
        await Assert.That(result.ItemsLast.Value).IsEqualTo(2m);

        await Assert.That(result.ItemsLastOrDefault).IsNotNull();
        await Assert.That(result.ItemsLastOrDefault!.Name).IsEqualTo("Last");
        await Assert.That(result.ItemsLastOrDefault.Value).IsEqualTo(2m);

        await Assert.That(result.PricesFirst).IsEqualTo(10m);
        await Assert.That(result.PricesFirstOrDefault).IsEqualTo(10m);
        await Assert.That(result.PricesLast).IsEqualTo(20m);
        await Assert.That(result.PricesLastOrDefault).IsEqualTo(20m);
    }

    [Test]
    public async Task Map_First_Last_With_Selector()
    {
        var source = new AggregateSource();

        source.Items.Add(new ItemSource { Name = "First", Value = 1m });
        source.Items.Add(new ItemSource { Name = "Last", Value = 2m });

        var result = source.Map().To<FirstLastSelectorTarget>();

        await Assert.That(result.ItemsNameFirst).IsEqualTo("First");
        await Assert.That(result.ItemsNameFirstOrDefault).IsEqualTo("First");
        await Assert.That(result.ItemsNameLast).IsEqualTo("Last");
        await Assert.That(result.ItemsNameLastOrDefault).IsEqualTo("Last");

        await Assert.That(result.ItemsValueFirst).IsEqualTo(1m);
        await Assert.That(result.ItemsValueLast).IsEqualTo(2m);
    }

    [Test]
    public async Task Map_FirstOrDefault_LastOrDefault_Empty_Collections()
    {
        var source = new AggregateSource();

        var result = source.Map().To<EmptyFirstLastTarget>();

        await Assert.That(result.ItemsFirstOrDefault).IsNull();
        await Assert.That(result.ItemsLastOrDefault).IsNull();

        await Assert.That(result.PricesFirstOrDefault).IsEqualTo(0m);
        await Assert.That(result.PricesLastOrDefault).IsEqualTo(0m);

        await Assert.That(result.ItemsNameFirstOrDefault).IsNull();
        await Assert.That(result.ItemsNameLastOrDefault).IsNull();
    }

    [Test]
    public async Task Map_Deep_Flattening_First_Last()
    {
        var source = new NestedFirstLastSource();

        source.Order.Items.Add(new ItemSource { Name = "First", Value = 1m });
        source.Order.Items.Add(new ItemSource { Name = "Last", Value = 2m });

        var result = source.Map().To<NestedFirstLastTarget>();

        await Assert.That(result.OrderItemsFirst.Name).IsEqualTo("First");
        await Assert.That(result.OrderItemsFirstOrDefault).IsNotNull();
        await Assert.That(result.OrderItemsFirstOrDefault!.Name).IsEqualTo("First");

        await Assert.That(result.OrderItemsLast.Name).IsEqualTo("Last");
        await Assert.That(result.OrderItemsLastOrDefault).IsNotNull();
        await Assert.That(result.OrderItemsLastOrDefault!.Name).IsEqualTo("Last");

        await Assert.That(result.OrderItemsNameFirst).IsEqualTo("First");
        await Assert.That(result.OrderItemsNameLast).IsEqualTo("Last");
    }

    [Test]
    public async Task Map_First_Last_Into_Existing_Target()
    {
        var source = new AggregateSource();

        source.Items.Add(new ItemSource { Name = "First", Value = 1m });
        source.Items.Add(new ItemSource { Name = "Last", Value = 2m });

        source.Prices.Add(10m);
        source.Prices.Add(20m);

        var target = new FirstLastTarget();

        var result = source.Map().To(target);

        await Assert.That(ReferenceEquals(result, target)).IsTrue();

        await Assert.That(result.ItemsFirst.Name).IsEqualTo("First");
        await Assert.That(result.ItemsLast.Name).IsEqualTo("Last");

        await Assert.That(result.PricesFirst).IsEqualTo(10m);
        await Assert.That(result.PricesLast).IsEqualTo(20m);
    }


    public class FirstLastTarget
    {
        public ItemTarget ItemsFirst { get; set; } = new();
        public ItemTarget? ItemsFirstOrDefault { get; set; }
        public ItemTarget ItemsLast { get; set; } = new();
        public ItemTarget? ItemsLastOrDefault { get; set; }

        public decimal PricesFirst { get; set; }
        public decimal PricesFirstOrDefault { get; set; }
        public decimal PricesLast { get; set; }
        public decimal PricesLastOrDefault { get; set; }
    }

    public class EmptyFirstLastTarget
    {
        public ItemTarget? ItemsFirstOrDefault { get; set; }
        public ItemTarget? ItemsLastOrDefault { get; set; }

        public decimal PricesFirstOrDefault { get; set; }
        public decimal PricesLastOrDefault { get; set; }

        public string? ItemsNameFirstOrDefault { get; set; }
        public string? ItemsNameLastOrDefault { get; set; }
    }

    public class FirstLastSelectorTarget
    {
        public string ItemsNameFirst { get; set; } = string.Empty;
        public string? ItemsNameFirstOrDefault { get; set; }
        public string ItemsNameLast { get; set; } = string.Empty;
        public string? ItemsNameLastOrDefault { get; set; }

        public decimal ItemsValueFirst { get; set; }
        public decimal ItemsValueLast { get; set; }
    }

    public class NestedFirstLastSource
    {
        public OrderSource Order { get; set; } = new();
    }

    public class NestedFirstLastTarget
    {
        public ItemTarget OrderItemsFirst { get; set; } = new();
        public ItemTarget? OrderItemsFirstOrDefault { get; set; }
        public ItemTarget OrderItemsLast { get; set; } = new();
        public ItemTarget? OrderItemsLastOrDefault { get; set; }

        public string OrderItemsNameFirst { get; set; } = string.Empty;
        public string OrderItemsNameLast { get; set; } = string.Empty;
    }

    public class OrderItemsNameTarget
    {
        public string OrderItemsFirstName { get; set; } = string.Empty;
        public string OrderItemsLastName { get; set; } = string.Empty;
        public string? OrderItemsFirstOrDefaultName { get; set; }
        public string? OrderItemsLastOrDefaultName { get; set; }
    }

    public class NullableOrderItemsNameTarget
    {
        public string? OrderItemsFirstOrDefaultName { get; set; }
        public string? OrderItemsLastOrDefaultName { get; set; }
    }

    [Test]
    public async Task Map_Deep_Flattening_First_Last_With_Property_Selector()
    {
        var source = new NestedFirstLastSource(); // Has Order -> Items

        source.Order.Items.Add(new ItemSource { Name = "Alpha", Value = 1m });
        source.Order.Items.Add(new ItemSource { Name = "Beta", Value = 2m });
        source.Order.Items.Add(new ItemSource { Name = "Omega", Value = 3m });

        var result = source.Map().To<OrderItemsNameTarget>();

        // source.Order.Items.First().Name
        await Assert.That(result.OrderItemsFirstName).IsEqualTo("Alpha");

        // source.Order.Items.Last().Name
        await Assert.That(result.OrderItemsLastName).IsEqualTo("Omega");

        // source.Order.Items.Select(x => x.Name).FirstOrDefault()
        await Assert.That(result.OrderItemsFirstOrDefaultName).IsEqualTo("Alpha");

        // source.Order.Items.Select(x => x.Name).LastOrDefault()
        await Assert.That(result.OrderItemsLastOrDefaultName).IsEqualTo("Omega");
    }

    [Test]
    public async Task Map_Deep_Flattening_First_Last_Empty_Paths()
    {
        var emptySource = new NestedFirstLastSource();
        var emptyResult = emptySource.Map().To<NullableOrderItemsNameTarget>();

        await Assert.That(emptyResult.OrderItemsFirstOrDefaultName).IsNull();
        await Assert.That(emptyResult.OrderItemsLastOrDefaultName).IsNull();
    }
}