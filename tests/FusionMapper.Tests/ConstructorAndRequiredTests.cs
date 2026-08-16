namespace FusionMapper.Tests;

public class ConstructorAndRequiredTests
{
    [Test]
    public async Task Map_To_Type_With_Constructor()
    {
        var source = new CtorSource
        {
            Name = "John",
            Age = 30
        };

        var result = source.Map().To<CtorTarget>();

        await Assert.That(result.Name).IsEqualTo("John");
        await Assert.That(result.Age).IsEqualTo(30);
    }

    [Test]
    public async Task Map_To_Type_With_Missing_Constructor_Parameter_Throws()
    {
        var source = new CtorMissingSource
        {
            Name = "John"
        };

        await Assert.That(() =>
            source.Map().To<CtorMissingTarget>()
        ).Throws<MappingException>();
    }

    [Test]
    public async Task Map_Record_To_Record()
    {
        var source = new RecordSource("A", 1);

        var result = source.Map().To<RecordTarget>();

        await Assert.That(result.Name).IsEqualTo("A");
        await Assert.That(result.Value).IsEqualTo(1);
    }

    [Test]
    public async Task Map_Record_With_Extra_Init_Property()
    {
        var source = new RecordExtraSource
        {
            Name = "A",
            Description = "D"
        };

        var result = source.Map().To<RecordExtraTarget>();

        await Assert.That(result.Name).IsEqualTo("A");
        await Assert.That(result.Description).IsEqualTo("D");
    }

    [Test]
    public async Task Map_Required_Member_Success()
    {
        var source = new RequiredSource
        {
            Name = "Required"
        };

        var result = source.Map().To<RequiredTarget>();

        await Assert.That(result.Name).IsEqualTo("Required");
    }

#if !FUSION_MAPPER_SOURCE_GENERATOR
    [Test]
    public async Task Map_Required_Member_Missing_Source_Throws()
    {
        var source = new RequiredMissingSource
        {
            Title = "Title"
        };

        var ex = await Assert.That(() =>
            source.Map().To<RequiredTarget>()
        ).Throws<MappingException>();

        await Assert.That(ex!.Message.Contains("Name", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }
#endif

    [Test]
    public async Task Map_Init_Only_Properties()
    {
        var source = new InitSource
        {
            Name = "Init",
            Value = 3
        };

        var result = source.Map().To<InitTarget>();

        await Assert.That(result.Name).IsEqualTo("Init");
        await Assert.That(result.Value).IsEqualTo(3);
    }

}
