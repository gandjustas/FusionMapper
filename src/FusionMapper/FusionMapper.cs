using System.Linq.Expressions;

namespace FusionMapper;


public static class FusionMapper
{
    public static FusionSource<TSource> Map<TSource>(this TSource source)
        => new(source);

    public static FusionProjection<TSource> Project<TSource>(this IQueryable<TSource> source)
        => new(source);
}

public readonly struct FusionSource<TSource>(TSource value)
{
    public TTarget To<TTarget>() => FusionMapper<TSource, TTarget>.Map(value);
    public TTarget To<TTarget>(TTarget target) => FusionMapper<TSource, TTarget>.Map(value, target);
}

public readonly struct FusionProjection<TSource>(IQueryable<TSource> value)
{
    public IQueryable<TTarget> To<TTarget>() => FusionMapper<TSource, TTarget>.Project(value);
}

public class FusionMapper<TSource, TTarget>
{
    private FusionMapper() { }
    public static TTarget Map(TSource source)
    {
        if (source is null)
        {
            var targetType = typeof(TTarget);
            if (targetType.IsClass || Nullable.GetUnderlyingType(targetType) != null) return default!;
        }
        creator ??= (Func<TSource, TTarget>)MappingBuilder.BuildCreationLambda(typeof(TSource), typeof(TTarget)).Compile();
        return creator(source);
    }

    public static TTarget Map(TSource source, TTarget target)
    {
        if (source is null)
        {
            return target;
        }

        assigner ??= (Func<TSource, TTarget, TTarget>)MappingBuilder.BuildAssignmentFuncLambda(typeof(TSource), typeof(TTarget)).Compile();
        return assigner(source, target);
    }

    public static IQueryable<TTarget> Project(IQueryable<TSource> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rewrittenSource = ExpressionRewriter.Rewrite(source);
        projector ??= (Expression<Func<TSource, TTarget>>)MappingBuilder.BuildCreationLambda(typeof(TSource), typeof(TTarget));
        return rewrittenSource.Select(projector);
    }

    private static Func<TSource, TTarget>? creator = null;
    private static Func<TSource, TTarget, TTarget>? assigner = null;
    private static Expression<Func<TSource, TTarget>>? projector = null;
}

