using System.Collections.ObjectModel;

namespace FusionMapper.Tests;

public class CollectionTypeMappingTests
{
    private class Item
    {
        public string Value { get; set; } = string.Empty;
    }

    private class ItemDto
    {
        public string Value { get; set; } = string.Empty;
    }

    [Test]
    public async Task Map_IEnumerable_To_List()
    {
        var source = new List<Item> { new() { Value = "A" }, new() { Value = "B" } };
        var result = source.Map().To<List<ItemDto>>();
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Value).IsEqualTo("A");
    }

    [Test]
    public async Task Map_Array_To_HashSet()
    {
        var source = new[] { new Item { Value = "X" }, new Item { Value = "Y" } };
        var result = source.Map().To<HashSet<ItemDto>>();
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result.Any(x => x.Value == "X")).IsTrue();
    }

    [Test]
    public async Task Map_Queue_To_List()
    {
        var queue = new Queue<Item>();
        queue.Enqueue(new Item { Value = "First" });
        queue.Enqueue(new Item { Value = "Second" });
        var result = queue.Map().To<List<ItemDto>>();
        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Value).IsEqualTo("First");
        await Assert.That(result[1].Value).IsEqualTo("Second");
    }

    [Test]
    public async Task Map_Stack_To_Array()
    {
        var stack = new Stack<Item>();
        stack.Push(new Item { Value = "Top" });
        stack.Push(new Item { Value = "Bottom" });
        var result = stack.Map().To<ItemDto[]>();
        await Assert.That(result.Length).IsEqualTo(2);
        // Порядок может быть обратным, но для теста важна длина
    }

    [Test]
    public async Task Map_ReadOnlyCollection_To_List()
    {
        var list = new List<Item> { new() { Value = "RO" } };
        var source = new ReadOnlyCollection<Item>(list);
        var result = source.Map().To<List<ItemDto>>();
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Value).IsEqualTo("RO");
    }

    [Test]
    public async Task Map_ICollection_To_IEnumerable()
    {
        ICollection<Item> source = [new Item { Value = "IC" }];
        var result = source.Map().To<IEnumerable<ItemDto>>();
        await Assert.That(result.Count()).IsEqualTo(1);
        await Assert.That(result.First().Value).IsEqualTo("IC");
    }

    // Маппинг словаря (если поддерживается)
    private class SourceWithDictionary
    {
        public Dictionary<string, int> Values { get; set; } = [];
    }

    private class TargetWithDictionary
    {
        public Dictionary<string, int> Values { get; set; } = [];
    }

    [Test]
    public async Task Map_Dictionary_Directly()
    {
        var source = new SourceWithDictionary();
        source.Values["A"] = 1;
        source.Values["B"] = 2;

        var result = source.Map().To<TargetWithDictionary>();
        await Assert.That(result.Values.Count).IsEqualTo(2);
        await Assert.That(result.Values["A"]).IsEqualTo(1);
        await Assert.That(result.Values["B"]).IsEqualTo(2);
    }

    // Маппинг словаря в список пар (если поддерживается)
    private class TargetWithListOfPairs
    {
        public List<KeyValuePair<string, int>> Values { get; set; } = [];
    }

    [Test]
    public async Task Map_Dictionary_To_ListOfKeyValuePairs()
    {
        var source = new SourceWithDictionary();
        source.Values["A"] = 1;
        source.Values["B"] = 2;

        var result = source.Map().To<TargetWithListOfPairs>();
        await Assert.That(result.Values.Count).IsEqualTo(2);
        await Assert.That(result.Values.Any(x => x.Key == "A" && x.Value == 1)).IsTrue();
    }
}