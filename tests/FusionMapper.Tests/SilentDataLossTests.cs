namespace FusionMapper.Tests;

public class SilentDataLossTests
{
    // Source collection whose element type cannot reach any target member.
    internal class SourceWithIntList
    {
        public List<int> Items { get; set; } = [];
    }

    // Has an Add, but its parameter type is incompatible with the element type.
    internal class TargetWithIncompatibleAdd
    {
        public BadAddCollection Items { get; set; } = null!;
    }

    internal class BadAddCollection
    {
        public void Add(string s) { }
    }

    internal class SourceWithLines
    {
        public List<Line> Lines { get; set; } = [];
    }

    internal class Line
    {
        public string Name { get; set; } = string.Empty;
    }

    internal class TargetWithNonNullableLine
    {
        public Line LinesFirstOrDefault { get; set; } = null!;
    }

    [Test]
    public async Task Map_To_Incompatible_Add_Throws()
    {
        var source = new SourceWithIntList { Items = [1, 2, 3] };

        // The generator rejects this pair at compile time (FMAP001); call the
        // runtime reflection path directly to verify it rejects it as well.
        await Assert.That(() => FusionMapper<SourceWithIntList, TargetWithIncompatibleAdd>.Map(source))
            .Throws<MappingException>();
    }

    [Test]
    public async Task Empty_Collection_FirstOrDefault_To_NonNullable_Target_Throws()
    {
        var source = new SourceWithLines { Lines = [] };

        await Assert.That(() => source.Map().To<TargetWithNonNullableLine>())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task NonEmpty_Collection_FirstOrDefault_To_NonNullable_Target_Maps()
    {
        var source = new SourceWithLines { Lines = [new Line { Name = "first" }] };

        var result = source.Map().To<TargetWithNonNullableLine>();

        await Assert.That(result.LinesFirstOrDefault).IsNotNull();
        await Assert.That(result.LinesFirstOrDefault.Name).IsEqualTo("first");
    }

    [Test]
    public async Task Empty_Collection_FirstOrDefault_To_Nullable_Target_Maps_Null()
    {
        var source = new SourceWithLines { Lines = [] };

        var result = source.Map().To<TargetWithNullableLine>();

        await Assert.That(result.LinesFirstOrDefault).IsNull();
    }

    internal class TargetWithNullableLine
    {
        public Line? LinesFirstOrDefault { get; set; }
    }

    [Test]
    public async Task Source_Without_Members_To_Target_Maps()
    {
        // A source without any readable members is allowed to produce an empty target.
        var result = new EmptySource().Map().To<EmptyTarget>();

        await Assert.That(result).IsNotNull();
    }

    internal class EmptySource
    {
    }

    internal class EmptyTarget
    {
    }
}
