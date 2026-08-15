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

        static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapDelegates = new();
        static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapToExistingDelegates = new();
        static readonly ConcurrentDictionary<(Type Source, Type Target), LambdaExpression> MapLambdaExpressions = new();

        static readonly ExpressionRewriter expressionRewriter = new();

        internal static LambdaExpression GetCreationLambda(Type source, Type target) =>
            MapLambdaExpressions.GetOrAdd((source, target),
                key => MappingBuilder.BuildCreationLambda(key.Source, key.Target));
        internal static Delegate GetCreationDelegate(Type source, Type target) => 
            MapDelegates.GetOrAdd((source, target),
                key => GetCreationLambda(key.Source, key.Target).Compile());

        internal static Delegate GetAssignmentDelegate(Type source, Type target) => 
            MapToExistingDelegates.GetOrAdd((source, target),
                key => MappingBuilder.BuildAssignmentExpression(key.Source, key.Target).Compile());

        internal static IQueryable<TTarget> Rewrite<TTarget>(IQueryable<TTarget> query)
        {
            ArgumentNullException.ThrowIfNull(query);

            if (expressionRewriter.Visit(query.Expression) is not { } newExpression)
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

        var func = (Func<TSource, TTarget>)State.GetCreationDelegate(sourceType, targetType);

        return func(source);
    }

    internal static TTarget Map<TSource, TTarget>(TSource source, TTarget target)
    {
        if (source == null)
        {
            return target;
        }

        ArgumentNullException.ThrowIfNull(target);

        var action = (Action<TSource, TTarget>)State.GetAssignmentDelegate(typeof(TSource), typeof(TTarget));
        action(source, target);

        return target;
    }

    internal static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        var rewrittenSource = State.Rewrite(source);
        var lambda = (Expression<Func<TSource, TTarget>>)State.GetCreationLambda(typeof(TSource), typeof(TTarget));
        return rewrittenSource.Select(lambda);
    }
}
#pragma warning restore S2955


public readonly struct FusionSource<TSource>(TSource value)
{
    public TTarget To<TTarget>() => FusionMapper.Map<TSource, TTarget>(value);
    public TTarget To<TTarget>(TTarget target) => FusionMapper.Map(value, target);
}

public readonly struct FusionProjection<TSource>(IQueryable<TSource> value)
{
    public IQueryable<TTarget> To<TTarget>() => FusionMapper.Project<TSource, TTarget>(value);
}
