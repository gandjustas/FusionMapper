using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace FusionMapper;


public static class FusionMapper
{
    public static FusionSource<TSource> Map<TSource>(this TSource source)
        => new(source);

    public static FusionProjection<TSource> Project<TSource>(this IQueryable<TSource> source)
        => new(source);

    static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapDelegates = new();
    static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapToExistingDelegates = new();
    static readonly ConcurrentDictionary<(Type Source, Type Target), Expression> MapLambdaExpressions = new();

    #pragma warning disable S2955

    internal static TTarget Map<TSource, TTarget>(TSource source)
    {
        Type sourceType = typeof(TSource);
        Type targetType = typeof(TTarget);

        if (source == null && (targetType.IsClass ||  Nullable.GetUnderlyingType(targetType) != null))
        {
            return default!;
        }

        var func = (Func<TSource, TTarget>)MapDelegates.GetOrAdd((sourceType, targetType), 
                _ => GetCreationLambda<TSource, TTarget>().Compile());
        return func(source);
    }

    internal static TTarget Map<TSource, TTarget>(TSource source, TTarget target)
    {
#pragma warning disable S2955
        if (source == null)
        {
            return target;
        }
#pragma warning restore S2955
        ArgumentNullException.ThrowIfNull(target);

        var del = MapToExistingDelegates.GetOrAdd((typeof(TSource), typeof(TTarget)), _ =>
            MappingBuilder.BuildAssignmentExpression<TSource, TTarget>().Compile());
        var action = (Action<TSource, TTarget>)del;
        action(source, target);
        return target;
    }

    internal static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source)
    {
        // TODO: Add rewrite expression to inline calls .Map().To<T> and Project().To<T> 
        return source.Select(GetCreationLambda<TSource, TTarget>());
    }

    static Expression<Func<TSource, TTarget>> GetCreationLambda<TSource, TTarget>()
    {
        return (Expression<Func<TSource, TTarget>>)MapLambdaExpressions.GetOrAdd((typeof(TSource), typeof(TTarget)),
            _ => MappingBuilder.BuildCreationLambda<TSource, TTarget>());
    }


}
#pragma warning restore S2955


public readonly struct FusionSource<TSource>(TSource Value)
{
    public TTarget To<TTarget>() => FusionMapper.Map<TSource, TTarget>(Value);
    public TTarget To<TTarget>(TTarget target) => FusionMapper.Map(Value, target);
}

public readonly struct FusionProjection<TSource>(IQueryable<TSource> Value)
{
    public IQueryable<TTarget> To<TTarget>() => FusionMapper.Project<TSource, TTarget>(Value);
}
