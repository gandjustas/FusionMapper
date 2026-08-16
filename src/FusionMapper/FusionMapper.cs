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
        return creator.Value(source);
    }

    public static TTarget Map(TSource source, TTarget target)
    {
        if (source is null)
        {
            return target;
        }

        return assigner.Value(source, target);
    }

    public static IQueryable<TTarget> Project(IQueryable<TSource> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rewrittenSource = ExpressionRewriter.Rewrite(source);
        return rewrittenSource.Select(projector.Value);
    }

    private static readonly Lazy<Func<TSource, TTarget>> creator = new(() => (Func<TSource, TTarget>)MappingBuilder.BuildCreationLambda(typeof(TSource), typeof(TTarget)).Compile(), true);
    private static readonly Lazy<Func<TSource, TTarget, TTarget>> assigner = new(() => (Func<TSource, TTarget, TTarget>)MappingBuilder.BuildAssignmentFuncLambda(typeof(TSource), typeof(TTarget)).Compile(), true);
    private static readonly Lazy<Expression<Func<TSource, TTarget>>> projector = new(() => (Expression<Func<TSource, TTarget>>)MappingBuilder.BuildCreationLambda(typeof(TSource), typeof(TTarget)), true);
}

