namespace FusionMapper;

/// <summary>
/// Suppresses the FMAP005 "unmapped target member" warning for the member it is applied to.
/// Use it on target members that intentionally have no matching source member.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
public sealed class FusionMapperIgnoreAttribute : Attribute
{
}
