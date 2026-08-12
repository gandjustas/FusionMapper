using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace FusionMapper;


public static class FusionMapper
{
    public static FusionSource<TSource> Map<TSource>(this TSource source)
        => new(source);

    public static FusionProjection<TSource> Project<TSource>(this IQueryable<TSource> source)
        => new(source);

    static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapDelegates = new();
    static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapToExistingDelegates = new();
    static readonly ConcurrentDictionary<(Type Source, Type Target), LambdaExpression> MapLambdaExpressions = new();

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

        var del = MapToExistingDelegates.GetOrAdd((typeof(TSource), typeof(TTarget)), 
            key => MappingBuilder.BuildAssignmentExpression(key.Source, key.Target).Compile());
        var action = (Action<TSource, TTarget>)del;
        action(source, target);
        return target;
    }

    internal static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var rewrittenSource = Rewrite<TSource, TSource>(source);
        return rewrittenSource.Select(GetCreationLambda<TSource, TTarget>());
    }
    internal static IQueryable<TTarget> Rewrite<TSource, TTarget>(IQueryable<TTarget> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (new ExpressionRewriter().Visit(query.Expression) is not { } newExpression)
        {
            return query;
        }

        if (!typeof(IQueryable<TTarget>).IsAssignableFrom(newExpression.Type))
        {
            throw new MappingException(
                $"Expression rewriting produced an expression of type '{newExpression.Type.FullName}' " +
                $"which is not assignable to '{typeof(IQueryable<TTarget>).FullName}'.");
        }

        return query.Provider.CreateQuery<TTarget>(newExpression);
    }

    static Expression<Func<TSource, TTarget>> GetCreationLambda<TSource, TTarget>()
    {
        return (Expression<Func<TSource, TTarget>>)MapLambdaExpressions.GetOrAdd((typeof(TSource), typeof(TTarget)),
            key => MappingBuilder.BuildCreationLambda(key.Source, key.Target));
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
