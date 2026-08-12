namespace FusionMapper.Tests;

public class AdvancedFlatteningTests
{
    private class Order
    {
        public Customer Customer { get; set; } = new();
    }

    private class Customer
    {
        public string Name { get; set; } = string.Empty;
        public List<Address> Addresses { get; set; } = [];
    }

    private class Address
    {
        public string City { get; set; } = string.Empty;
    }

    private class OrderDto
    {
        public string CustomerName { get; set; } = string.Empty;
        public int CustomerAddressesCount { get; set; }
        public string CustomerAddressesFirstOrDefaultCity { get; set; } = string.Empty;
    }

    [Test]
    public async Task Map_Flattening_With_Collection_Aggregates()
    {
        var source = new Order();
        source.Customer.Name = "Alice";
        source.Customer.Addresses.Add(new Address { City = "New York" });
        source.Customer.Addresses.Add(new Address { City = "London" });

        var result = source.Map().To<OrderDto>();

        await Assert.That(result.CustomerName).IsEqualTo("Alice");
        await Assert.That(result.CustomerAddressesCount).IsEqualTo(2);
        await Assert.That(result.CustomerAddressesFirstOrDefaultCity).IsEqualTo("New York");
    }

    [Test]
    public async Task Map_Flattening_With_Null_Intermediate_And_Collection()
    {
        var source = new Order(); // Customer is not null, but Addresses is empty
        source.Customer.Name = "Bob";
        // Addresses остаётся пустым

        var result = source.Map().To<OrderDto>();
        await Assert.That(result.CustomerName).IsEqualTo("Bob");
        await Assert.That(result.CustomerAddressesCount).IsEqualTo(0);
        await Assert.That(result.CustomerAddressesFirstOrDefaultCity).IsNull(); // или пустая строка
    }
}