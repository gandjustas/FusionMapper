namespace FusionMapper;

internal static class EnumParser
{
    public static TEnum Parse<TEnum>(string value) where TEnum : struct, Enum
    {
        return Enum.TryParse(value, out TEnum result)
            ? result
            : throw new MappingException(
                $"Cannot convert '{value}' to enum '{typeof(TEnum).FullName}'.");
    }
}
