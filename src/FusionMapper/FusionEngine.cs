using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace FusionMapper;

internal static class FusionEngine
{
    static readonly ConcurrentDictionary<TypePair, Delegate> MapDelegates = new();
    static readonly ConcurrentDictionary<TypePair, Delegate> MapToExistingDelegates = new();

    public static TTarget Map<TSource, TTarget>(TSource source)
    {
        if (source == null)
        {
            var targetType = typeof(TTarget);
            if (targetType.IsClass || Nullable.GetUnderlyingType(targetType) != null)
                return default!;
            throw new MappingException($"Cannot map null source to non-nullable value type '{targetType.FullName}'.");
        }
        var del = MapDelegates.GetOrAdd(new (typeof(TSource), typeof(TTarget)), _ => CompileMapping<TSource, TTarget>());
        var func = (Func<TSource, TTarget>)del;
        return func(source);
    }

    public static TTarget Map<TSource, TTarget>(TSource source, TTarget target)
    {
        if (source == null)
        {
            ArgumentNullException.ThrowIfNull(target);
            return target;
        }
        ArgumentNullException.ThrowIfNull(target);
        
        var del = MapToExistingDelegates.GetOrAdd(new (typeof(TSource), typeof(TTarget)), _ => CompileMappingToExisting<TSource, TTarget>());
        var action = (Action<TSource, TTarget>)del;
        action(source, target);
        return target;
    }

    public static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source)
    {
        // Проекции будут реализованы в Milestone 3
        throw new NotImplementedException("FusionMapper runtime projection engine is not implemented yet.");
    }


    public static Delegate CompileMapping<TSource, TTarget>()
    {
        var sourceParam = Expression.Parameter(typeof(TSource), "source");
        var body = MappingPlanBuilder.BuildCreationExpression<TSource, TTarget>(sourceParam);
        var lambda = Expression.Lambda<Func<TSource, TTarget>>(body, sourceParam);
        return lambda.Compile();
    }

    public static Delegate CompileMappingToExisting<TSource, TTarget>()
    {
        var sourceParam = Expression.Parameter(typeof(TSource), "source");
        var targetParam = Expression.Parameter(typeof(TTarget), "target");
        var body = MappingPlanBuilder.BuildAssignmentExpression<TSource, TTarget>(sourceParam, targetParam);
        var lambda = Expression.Lambda<Action<TSource, TTarget>>(body, sourceParam, targetParam);
        return lambda.Compile();
    }
    readonly record struct  TypePair (Type SourceType, Type TargetType);
}

