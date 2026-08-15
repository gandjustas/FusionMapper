
namespace FusionMapper.Tests;

public class PrivateMemberMappingTests
{
    internal class SourceWithPrivate
    {
        public string PublicName { get; set; } = "Public";
        private string PrivateName { get; set; } = "Private";
        protected string ProtectedName { get; set; } = "Protected";
        internal string InternalName { get; set; } = "Internal";
    }

    internal class TargetWithPrivate
    {
        public string PublicName { get; set; } = string.Empty;
        public string PrivateName { get; set; } = string.Empty;
        public string ProtectedName { get; set; } = string.Empty;
        public string InternalName { get; set; } = string.Empty;
    }

    [Test]
    public async Task Map_Ignores_Private_And_Protected_Members()
    {
        var source = new SourceWithPrivate();
        var result = source.Map().To<TargetWithPrivate>();

        // Публичное свойство должно быть замаплено
        await Assert.That(result.PublicName).IsEqualTo("Public");

        // Приватные, защищённые и внутренние — не должны (если нет специальной настройки)
        await Assert.That(result.PrivateName).IsEqualTo(string.Empty);
        await Assert.That(result.ProtectedName).IsEqualTo(string.Empty);
        // Internal может быть доступен в той же сборке, но маппер обычно использует публичные члены
        // Если маппер использует BindingFlags.Public, то Internal не будет доступен,
        // если только не используется непубличный binding.
        await Assert.That(result.InternalName).IsEqualTo(string.Empty);
    }

    // Дополнительный тест: если у источника есть публичное поле (не свойство)
    internal class SourceWithPublicField
    {
        public string Field = "FieldValue";
    }

    internal class TargetWithPublicField
    {
        public string Field { get; set; } = string.Empty;
    }

    [Test]
    public async Task Map_Public_Fields_Are_Mapped()
    {
        var source = new SourceWithPublicField();
        var result = source.Map().To<TargetWithPublicField>();
        await Assert.That(result.Field).IsEqualTo("FieldValue");
    }
}