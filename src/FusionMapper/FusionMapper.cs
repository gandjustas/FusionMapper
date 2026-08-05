namespace FusionMapper;

public static class FusionMapper
{
    public static FusionSource<TSource> Map<TSource>(this TSource source)
        => new(source);

    public static FusionProjection<TSource> Project<TSource>(this IQueryable<TSource> source)
        => new(source);

}

public readonly struct FusionSource<TSource>(TSource Value)
{
    public TTarget To<TTarget>() => FusionEngine.Map<TSource, TTarget>(Value);
    public TTarget To<TTarget>(TTarget target) => FusionEngine.Map(Value, target);
}

public readonly struct FusionProjection<TSource>(IQueryable<TSource> Value)
{
    public IQueryable<TTarget> To<TTarget>() => FusionEngine.Project<TSource, TTarget>(Value);
}
