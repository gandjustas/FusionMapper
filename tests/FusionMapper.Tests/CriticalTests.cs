using System.Collections;
using System.Linq.Expressions;

namespace FusionMapper.Tests;

public class CriticalTests
{
    internal class IntSource
    {
        public int Value { get; init; }
    }

    internal class ObjectTarget
    {
        public object? Value { get; init; }
    }

    [Test]
    public async Task Map_Should_Box_Value_Type_To_Object_Member()
    {
        var source = new IntSource
        {
            Value = 42
        };

        var target = source.Map().To<ObjectTarget>();

        await Assert.That(target.Value).IsEqualTo(42);
    }

    internal class ItemSource
    {
        public string Name { get; init; } = string.Empty;
    }

    internal class OrderSource
    {
        public List<ItemSource> Items { get; init; } = [];
    }

    internal class OrderTarget
    {
        public int ItemsCount { get; init; }
    }

    [Test]
    public async Task Map_Should_Support_Collection_Count_Aggregate()
    {
        var source = new OrderSource
        {
            Items =
            [
                new ItemSource(),
                new ItemSource()
            ]
        };

        var target = source.Map().To<OrderTarget>();

        await Assert.That(target.ItemsCount).IsEqualTo(2);
    }

    internal class AddOnlyCollection<T> : IEnumerable<T>
    {
        private readonly List<T> _items = [];

        public IReadOnlyList<T> Values => _items;

        public void Clear() => _items.Clear();

        public void Add(T item) => _items.Add(item);

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    internal class ReadOnlySource
    {
        public List<string> Items { get; init; } = [];
    }

    internal class ReadOnlyTarget
    {
        public AddOnlyCollection<string> Items { get; } = new();
    }

    [Test]
    public async Task Map_To_Existing_Should_Update_AddOnly_ReadOnly_Collection()
    {
        var source = new ReadOnlySource
        {
            Items =
            [
                "a",
                "b"
            ]
        };

        var target = new ReadOnlyTarget();

        source.Map().To(target);

        await Assert.That(target.Items.Values.Count).IsEqualTo(2);
        await Assert.That(target.Items.Values).Contains("a");
        await Assert.That(target.Items.Values).Contains("b");
    }    
}