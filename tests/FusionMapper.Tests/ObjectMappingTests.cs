// SimpleMappingTests.cs
namespace FusionMapper.Tests;

#region Simple

public class SimpleSource
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

public class SimpleTarget
{
    public string Name { get; set; } = string.Empty;
    public int Value { get; set; }
}

#endregion

#region Case sensitivity

public class CaseSource
{
    public string? nAmE { get; set; }
    public int vAlUe { get; set; }
}

public class CaseTarget
{
    public string? Name { get; set; }
    public int Value { get; set; }
}

public class ExactWinsSource
{
    public string Name { get; set; } = "exact";
    public string NAME { get; set; } = "upper";
}

public class ExactWinsTarget
{
    public string Name { get; set; } = string.Empty;
}

public class AmbiguousSource
{
    public string Name { get; set; } = "a";
    public string NAME { get; set; } = "b";
}

public class AmbiguousTarget
{
    public string? name { get; set; }
}

#endregion

#region Nullable

public class NullableSource
{
    public int? Value { get; set; }
}

public class NullableTarget
{
    public int? Value { get; set; }
}

#endregion

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
