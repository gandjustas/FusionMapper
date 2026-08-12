
namespace FusionMapper.Tests;

public class EnumMappingTests
{
    // Модели для тестов
    private enum Color { Red, Green, Blue }
    private enum Status { Active = 1, Inactive = 0 }

    private class SourceWithEnum
    {
        public Color Color { get; set; }
        public Status? Status { get; set; }
    }

    private class TargetWithEnumInt
    {
        public int Color { get; set; }
        public int? Status { get; set; }
    }

    private class TargetWithEnumString
    {
        public string Color { get; set; } = string.Empty;
        public string? Status { get; set; }
    }

    [Test]
    public async Task Map_Enum_To_Int()
    {
        var source = new SourceWithEnum { Color = Color.Blue, Status = Status.Active };
        var result = source.Map().To<TargetWithEnumInt>();

        await Assert.That(result.Color).IsEqualTo((int)Color.Blue);
        await Assert.That(result.Status).IsEqualTo((int)Status.Active);
    }

    [Test]
    public async Task Map_Int_To_Enum()
    {
        var source = new TargetWithEnumInt { Color = 2, Status = 0 };
        var result = source.Map().To<SourceWithEnum>();

        await Assert.That(result.Color).IsEqualTo(Color.Blue);
        await Assert.That(result.Status).IsEqualTo(Status.Inactive);
    }

    [Test]
    public async Task Map_Enum_To_String()
    {
        var source = new SourceWithEnum { Color = Color.Red, Status = null };
        var result = source.Map().To<TargetWithEnumString>();

        await Assert.That(result.Color).IsEqualTo("Red");
        await Assert.That(result.Status).IsNull();
    }

    [Test]
    public async Task Map_String_To_Enum_When_Valid()
    {
        var source = new TargetWithEnumString { Color = "Green", Status = "Active" };
        var result = source.Map().To<SourceWithEnum>();

        await Assert.That(result.Color).IsEqualTo(Color.Green);
        await Assert.That(result.Status).IsEqualTo(Status.Active);
    }

    [Test]
    public async Task Map_Invalid_String_To_Enum_Throws()
    {
        var source = new TargetWithEnumString { Color = "Invalid" };
        await Assert.That(() => source.Map().To<SourceWithEnum>())
            .Throws<ArgumentException>();
    }
}