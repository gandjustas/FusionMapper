using System.Collections.Concurrent;
using System.ComponentModel;
using System.Linq.Expressions;

namespace FusionMapper;

#pragma warning disable S2955

public static class FusionMapper
{
    [EditorBrowsable(EditorBrowsableState.Never)]
    public sealed class State
    {
        private State()
        {
        }

        internal static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapDelegates = new();
        internal static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapToExistingDelegates = new();
        internal static readonly ConcurrentDictionary<(Type Source, Type Target), LambdaExpression> MapLambdaExpressions = new();

    }

    public static FusionSource<TSource> Map<TSource>(this TSource source)
        => new(source);

    public static FusionProjection<TSource> Project<TSource>(this IQueryable<TSource> source)
        => new(source);

    internal static TTarget Map<TSource, TTarget>(TSource source)
    {
        Type sourceType = typeof(TSource);
        Type targetType = typeof(TTarget);

        if (source == null && (targetType.IsClass || Nullable.GetUnderlyingType(targetType) != null))
        {
            return default!;
        }

        var func = (Func<TSource, TTarget>)State.MapDelegates.GetOrAdd(
            (sourceType, targetType),
            _ => GetCreationLambda<TSource, TTarget>().Compile());

        return func(source);
    }

    internal static TTarget Map<TSource, TTarget>(TSource source, TTarget target)
    {
        if (source == null)
        {
            return target;
        }

        ArgumentNullException.ThrowIfNull(target);

        var del = State.MapToExistingDelegates.GetOrAdd(
            (typeof(TSource), typeof(TTarget)),
            key => MappingBuilder.BuildAssignmentExpression(key.Source, key.Target).Compile());

        var action = (Action<TSource, TTarget>)del;
        action(source, target);

        return target;
    }

    internal static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var rewrittenSource = Rewrite(source);

        return rewrittenSource.Select(GetCreationLambda<TSource, TTarget>());
    }

    internal static IQueryable<TTarget> Rewrite<TTarget>(IQueryable<TTarget> query)
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
        => (Expression<Func<TSource, TTarget>>)GetCreationLambda(typeof(TSource), typeof(TTarget));

    internal static LambdaExpression GetCreationLambda(Type source, Type target) =>
        State.MapLambdaExpressions.GetOrAdd(
            (source, target),
            key => MappingBuilder.BuildCreationLambda(key.Source, key.Target));
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
