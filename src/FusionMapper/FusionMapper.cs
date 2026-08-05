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

    internal static TTarget Map<TSource, TTarget>(TSource source)
    {
        var del = MapDelegates.GetOrAdd((typeof(TSource), typeof(TTarget)), _ => CompileMapping<TSource, TTarget>());
        var func = (Func<TSource, TTarget>)del;
        return func(source);
    }

    internal static TTarget Map<TSource, TTarget>(TSource source, TTarget target)
    {
        if (source == null)
        {
            ArgumentNullException.ThrowIfNull(target);
            return target;
        }
        ArgumentNullException.ThrowIfNull(target);

        var del = MapToExistingDelegates.GetOrAdd((typeof(TSource), typeof(TTarget)), _ => CompileMappingToExisting<TSource, TTarget>());
        var action = (Action<TSource, TTarget>)del;
        action(source, target);
        return target;
    }

    internal static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source)
    {
        // Проекции будут реализованы в Milestone 3
        throw new NotImplementedException("FusionMapper runtime projection engine is not implemented yet.");
    }


    static Delegate CompileMapping<TSource, TTarget>()
    {
        var sourceParam = Expression.Parameter(typeof(TSource), "source");
        var body = MappingBuilder.BuildCreationExpression<TSource, TTarget>(sourceParam);
        var lambda = Expression.Lambda<Func<TSource, TTarget>>(body, sourceParam);
        return lambda.Compile();
    }

    static Delegate CompileMappingToExisting<TSource, TTarget>()
    {
        var sourceParam = Expression.Parameter(typeof(TSource), "source");
        var targetParam = Expression.Parameter(typeof(TTarget), "target");
        var body = MappingBuilder.BuildAssignmentExpression<TSource, TTarget>(sourceParam, targetParam);
        var lambda = Expression.Lambda<Action<TSource, TTarget>>(body, sourceParam, targetParam);
        return lambda.Compile();
    }

}

public readonly struct FusionSource<TSource>(TSource Value)
{
    public TTarget To<TTarget>() => FusionMapper.Map<TSource, TTarget>(Value);
    public TTarget To<TTarget>(TTarget target) => FusionMapper.Map(Value, target);
}

public readonly struct FusionProjection<TSource>(IQueryable<TSource> Value)
{
    public IQueryable<TTarget> To<TTarget>() => FusionMapper.Project<TSource, TTarget>(Value);
}
