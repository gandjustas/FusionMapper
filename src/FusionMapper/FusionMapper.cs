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
#pragma warning disable S2955
        if (source == null)            
        {
            var targetType = typeof(TTarget);
            if (targetType.IsClass || Nullable.GetUnderlyingType(targetType) != null) return default!;
        }
#pragma warning restore S2955
        return creator.Value(source);
    }

    public static TTarget Map(TSource source, TTarget target)
    {
        // 1) source == null && target == null -> return null/default
        // 2) source == null && target != null -> leave target as is
        if (source is null)
        {
            return target;
        }

        // 3) target не существует -> создаём новый объект
        if (target is null)
        {
            return creator.Value(source);
        }

        // 4) target существует -> заполняем существующий объект        
        assigner.Value(source, target);
        return target;
    }

    public static IQueryable<TTarget> Project(IQueryable<TSource> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rewrittenSource = ExpressionRewriter.Rewrite(source);
        return rewrittenSource.Select(projector.Value);
    }

    private static readonly Lazy<Func<TSource, TTarget>> creator = new(() => (Func<TSource, TTarget>)MappingBuilder.BuildCreationLambda(typeof(TSource), typeof(TTarget)).Compile(), true);
    private static readonly Lazy<Action<TSource, TTarget>> assigner = new(() => (Action<TSource, TTarget>)MappingBuilder.BuildAssignmentLambda(typeof(TSource), typeof(TTarget)).Compile(), true);
    private static readonly Lazy<Expression<Func<TSource, TTarget>>> projector = new(() => (Expression<Func<TSource, TTarget>>)MappingBuilder.BuildCreationLambda(typeof(TSource), typeof(TTarget)), true);
}

