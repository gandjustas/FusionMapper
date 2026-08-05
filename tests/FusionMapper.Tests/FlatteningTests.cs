namespace FusionMapper.Tests;

public class FlatteningTests
{
    [Test]
    public async Task Map_Flattening_Nested_Property()
    {
        var source = new FlattenSource
        {
            Nested = new NestedObject
            {
                City = "X"
            }
        };

        var result = source.Map().To<FlattenTarget>();

        await Assert.That(result.NestedCity).IsEqualTo("X");
    }

    [Test]
    public async Task Map_Flattening_With_Null_Nested_Object()
    {
        var source = new FlattenNullSource
        {
            Nested = null
        };

        var result = source.Map().To<FlattenNullTarget>();

        await Assert.That(result.NestedCity).IsNull();
    }
}
