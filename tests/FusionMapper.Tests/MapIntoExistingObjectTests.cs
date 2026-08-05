namespace FusionMapper.Tests;

public class MapIntoExistingObjectTests
{
    [Test]
    public async Task Map_To_Existing_Object_Updates_Members()
    {
        var source = new SimpleSource
        {
            Name = "New",
            Value = 10
        };

        var target = new SimpleTarget
        {
            Name = "Old",
            Value = 1
        };

        source.Map().To(target);

        await Assert.That(target.Name).IsEqualTo("New");
        await Assert.That(target.Value).IsEqualTo(10);
    }

    [Test]
    public async Task Map_To_Existing_Object_Returns_Same_Instance()
    {
        var source = new SimpleSource
        {
            Name = "New",
            Value = 10
        };

        var target = new SimpleTarget();

        var returned = source.Map().To(target);

        await Assert.That(ReferenceEquals(returned, target)).IsTrue();
    }

    [Test]
    public async Task Map_To_Existing_Object_With_Collection_Replaces_Items()
    {
        var source = new OrderSource();
        source.Items.Add(new ItemSource { Name = "A" });
        source.Items.Add(new ItemSource { Name = "B" });

        var target = new OrderTarget();
        target.Items.Add(new ItemTarget { Name = "Old" });

        source.Map().To(target);

        await Assert.That(target.Items.Count).IsEqualTo(2);
        await Assert.That(target.Items[0].Name).IsEqualTo("A");
        await Assert.That(target.Items[1].Name).IsEqualTo("B");
    }

    [Test, Skip("Not implemented call AddRange")]
    public async Task Map_To_Existing_Object_With_ReadOnly_Collection_Mutates_Collection()
    {
        var source = new OrderSource();
        source.Items.Add(new ItemSource { Name = "A" });
        source.Items.Add(new ItemSource { Name = "B" });

        var target = new OrderReadOnlyItemsTarget();

        source.Map().To(target);

        await Assert.That(target.Items.Count).IsEqualTo(2);
        await Assert.That(target.Items[0].Name).IsEqualTo("A");
        await Assert.That(target.Items[1].Name).IsEqualTo("B");
    }
}
