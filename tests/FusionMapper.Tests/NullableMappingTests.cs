namespace FusionMapper.Tests;

public class NullableMappingTests
{
    private class SourceWithNullable
    {
        public int? NullableInt { get; set; }
        public string? NullableString { get; set; }
        public DateTime? NullableDateTime { get; set; }
    }

    private class TargetWithNonNullable
    {
        public int NullableInt { get; set; }
        public string NullableString { get; set; } = string.Empty;
        public DateTime NullableDateTime { get; set; }
    }

    [Test]
    public async Task Map_Nullable_To_NonNullable_With_Value()
    {
        var source = new SourceWithNullable { NullableInt = 42, NullableString = "Test", NullableDateTime = DateTime.Now };
        var result = source.Map().To<TargetWithNonNullable>();
        await Assert.That(result.NullableInt).IsEqualTo(42);
        await Assert.That(result.NullableString).IsEqualTo("Test");
        await Assert.That(result.NullableDateTime).IsEqualTo(source.NullableDateTime.Value);
    }

    [Test]
    public async Task Map_Nullable_To_NonNullable_With_Null_Throws()
    {
        var source = new SourceWithNullable { NullableInt = null, NullableString = null, NullableDateTime = null };
        await Assert.That(() => source.Map().To<TargetWithNonNullable>())
            .Throws<MappingException>();
    }

    [Test]
    public async Task Map_NonNullable_To_Nullable()
    {
        var source = new TargetWithNonNullable { NullableInt = 99, NullableString = "Hello", NullableDateTime = DateTime.UtcNow };
        var result = source.Map().To<SourceWithNullable>();
        await Assert.That(result.NullableInt).IsEqualTo(99);
        await Assert.That(result.NullableString).IsEqualTo("Hello");
        await Assert.That(result.NullableDateTime).IsEqualTo(source.NullableDateTime);
    }

    [Test]
    public async Task Map_Nullable_Reference_To_String_With_Null()
    {
        var source = new SourceWithNullable
        {
            NullableInt = 0,
            NullableDateTime = DateTime.MinValue,
            NullableString = null
        };

        var result = source.Map().To<TargetWithNonNullable>();

        await Assert.That((string?)result.NullableString).IsNull();
    }
}