namespace FusionMapper.Tests;

public class PrimitiveMappingTests
{
    [Test]
    public async Task Map_Int_To_Int()
    {
        var source = 42;
        await Assert.That(source.Map().To<int>()).IsEqualTo(42);
    }

    [Test]
    public async Task Map_Int_To_Long()
    {
        var source = 42;
        await Assert.That(source.Map().To<long>()).IsEqualTo(42L);
    }

    [Test]
    public async Task Map_Long_To_Int_When_In_Range()
    {
        var source = 123L;
        await Assert.That(source.Map().To<int>()).IsEqualTo(123);
    }

    [Test]
    public async Task Map_Float_To_Double()
    {
        var source = 3.14f;
        await Assert.That(source.Map().To<double>()).IsCloseTo(3.14, 0.000001);
    }

    [Test]
    public async Task Map_Decimal_To_Decimal()
    {
        var source =  99.99m;
        await Assert.That(source.Map().To<decimal>()).IsEqualTo(99.99m);
    }

    [Test]
    public async Task Map_Bool_To_Bool()
    {
        var source = true;
        await Assert.That(source.Map().To<bool>()).IsTrue();
    }

    [Test]
    public async Task Map_String_To_String()
    {
        var source = "Hello";
        await Assert.That(source.Map().To<string>()).IsEqualTo("Hello");
    }

    [Test]
    public async Task Map_Char_To_Char()
    {
        var source = 'A';
        await Assert.That(source.Map().To<char>()).IsEqualTo('A');
    }

    [Test]
    public async Task Map_DateTime_To_DateTime()
    {
        var date = new DateTime(2025, 1, 15, 10, 30, 0);
        await Assert.That(date.Map().To<DateTime>()).IsEqualTo(date);
    }

    [Test]
    public async Task Map_Guid_To_Guid()
    {
        var guid = Guid.NewGuid();
        await Assert.That(guid.Map().To<Guid>()).IsEqualTo(guid);
    }

    [Test]
    public async Task Map_TimeSpan_To_TimeSpan()
    {
        var ts = TimeSpan.FromHours(5);
        await Assert.That(ts.Map().To<TimeSpan>()).IsEqualTo(ts);
    }

    [Test]
    public async Task Map_Enum_To_Enum()
    {
        await Assert.That(Color.Blue.Map().To<Color>()).IsEqualTo(Color.Blue);
    }

    [Test]
    public async Task Map_Enum_To_Int()
    {
        await Assert.That(Color.Green.Map().To<int>()).IsEqualTo(1);
    }

    [Test]
    public async Task Map_Int_To_Enum()
    {
        var source = 1 ;
        await Assert.That(source.Map().To<Color>()).IsEqualTo(Color.Green);
    }

    [Test]
    public async Task Map_Nullable_Int_To_Nullable_Int_With_Value()
    {
        int? source = 10;
        await Assert.That(source.Map().To<int?>()).IsEqualTo(10);
    }

    [Test]
    public async Task Map_Nullable_Int_To_Nullable_Int_With_Null()
    {
        int? source = null;
        await Assert.That(source.Map().To<int?>()).IsNull();
    }

    [Test]
    public async Task Map_Nullable_Int_To_Int_With_Value()
    {
        int? source = 7;
        await Assert.That(source.Map().To<int>()).IsEqualTo(7);
    }

    [Test]
    public async Task Map_Nullable_Int_To_Int_With_Null_Throws()
    {
        int? source = null;
        await Assert.That(() => source.Map().To<int>())
            .Throws<MappingException>(); // Convert выбросит InvalidOperationException при runtime, но мы обернём в MappingException?
    }

    [Test]
    public async Task Map_Int_To_Nullable_Int()
    {
        var source = 5;
        await Assert.That(source.Map().To<int?>()).IsEqualTo(5);
    }

    [Test]
    public async Task Map_Null_String_To_String_Returns_Null()
    {
        string? source = null;
        await Assert.That(source.Map().To<string>()).IsNull();
    }

    [Test]
    public async Task Map_Null_DateTime_To_DateTime_Returns_Null()
    {
        DateTime? source = null;
        await Assert.That(source.Map().To<DateTime?>()).IsNull();
    }

    [Test]
    public async Task Map_Null_Source_To_PrimitiveTarget_Returns_Null()
    {
        object? source = null;
        await Assert.That(source.Map().To<int?>()).IsNull();
    }

    public enum Color
    {
        Red = 0,
        Green = 1,
        Blue = 2
    }
}
