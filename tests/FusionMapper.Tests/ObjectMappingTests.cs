// SimpleMappingTests.cs
namespace FusionMapper.Tests;

public class ObjectMappingTests
{
    [Test]
    public async Task Map_Simple_Properties()
    {
        var source = new SimpleSource
        {
            Name = "Test",
            Value = 42
        };

        var result = source.Map().To<SimpleTarget>();

        await Assert.That(result.Name).IsEqualTo("Test");
        await Assert.That(result.Value).IsEqualTo(42);
    }

    [Test]
    public async Task Map_Null_Source_Returns_Null()
    {
        SimpleSource? source = null;

        var result = source.Map().To<SimpleTarget>();

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task Map_Case_Insensitive_Members()
    {
        var source = new CaseSource
        {
            nAmE = "abc",
            vAlUe = 7
        };

        var result = source.Map().To<CaseTarget>();

        await Assert.That(result.Name).IsEqualTo("abc");
        await Assert.That(result.Value).IsEqualTo(7);
    }

    [Test]
    public async Task Map_Exact_Match_Wins_Over_Case_Insensitive()
    {
        var source = new ExactWinsSource();

        var result = source.Map().To<ExactWinsTarget>();

        await Assert.That(result.Name).IsEqualTo("exact");
    }

    [Test]
    public async Task Map_Ambiguous_CaseInsensitive_Match_Throws()
    {
        var source = new AmbiguousSource();

        await Assert.That(() =>
            source.Map().To<AmbiguousTarget>()
        ).Throws<MappingException>();
    }

    [Test]
    public async Task Map_Nullable_Value_WithValue()
    {
        var source = new NullableSource
        {
            Value = 5
        };

        var result = source.Map().To<NullableTarget>();

        await Assert.That(result.Value).IsEqualTo(5);
    }

    [Test]
    public async Task Map_Nullable_Value_WithNull()
    {
        var source = new NullableSource
        {
            Value = null
        };

        var result = source.Map().To<NullableTarget>();

        await Assert.That(result.Value).IsNull();
    }
}
