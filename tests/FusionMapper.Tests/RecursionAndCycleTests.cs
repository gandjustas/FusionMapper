// RecursionAndCycleTests.cs
namespace FusionMapper.Tests;

public class RecursionAndCycleTests
{
    [Test]
    public async Task Map_Recursive_Object_Graph()
    {
        var source = new NodeSource
        {
            Name = "root",
            Child = new NodeSource
            {
                Name = "child"
            }
        };

        var result = source.Map().To<NodeTarget>();

        await Assert.That(result.Name).IsEqualTo("root");
        await Assert.That(result.Child is not null).IsTrue();
        await Assert.That(result.Child!.Name).IsEqualTo("child");
    }

    [Test]
    public async Task Map_Cyclic_Object_Graph_Does_Not_StackOverflow()
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

        var result = parent.Map().To<CycleTarget>();

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Children.Count).IsEqualTo(1);
        await Assert.That(result.Children[0].Name).IsEqualTo("child");
    }
}
