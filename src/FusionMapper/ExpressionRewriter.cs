using System.Collections.Concurrent;
using System.Linq.Expressions;

namespace FusionMapper;

public sealed class ExpressionRewriter : ExpressionVisitor
{
    static readonly ExpressionRewriter instance = new();
    static readonly ConcurrentDictionary<(Type Source, Type Target), LambdaExpression> cache = [];
    private ExpressionRewriter() { }

    protected override Expression VisitMethodCall(MethodCallExpression node) => node switch
    {
        _ when IsFusionSourceTo(node) => RewriteSourceTo(node),
        _ when IsFusionProjectionTo(node) => RewriteProjectionTo(node),
        _ when IsFusionMap(node) => throw new MappingException(
            "Unsupported FusionMapper call in expression tree. " +
            "'Map()' must be immediately followed by '.To<T>()'."),
        _ when IsFusionProject(node) => throw new MappingException(
            "Unsupported FusionMapper call in expression tree. " +
            "'Project()' must be immediately followed by '.To<T>()'."),
        _ when node.Method.DeclaringType == typeof(FusionMapper) => throw new MappingException(
            $"Unsupported FusionMapper call '{node.Method.Name}' inside query expression tree."),
        _ => base.VisitMethodCall(node) ?? node
    };

    private Expression RewriteSourceTo(MethodCallExpression node)
    {
        if (node.Arguments.Count != 0)
        {
            throw new MappingException(
                "Mapping into an existing object using 'Map().To(target)' is not supported inside query expression trees.");
        }

        if (UnwrapConversion(node.Object!) is MethodCallExpression mapCall && IsFusionMap(mapCall))
        {
            var sourceExpression = Visit(mapCall.Arguments[0]) ?? mapCall.Arguments[0];
            var sourceType = mapCall.Method.GetGenericArguments()[0];
            var targetType = node.Method.GetGenericArguments()[0];

            return InlineProjection(sourceExpression, sourceType, targetType);
        }

        throw new MappingException(
            "Unsupported FusionMapper call in expression tree. " +
            "'To<T>()' can only be rewritten when it is called on 'x.Map()'.");
    }

    private MethodCallExpression RewriteProjectionTo(MethodCallExpression node)
    {
        if (node.Arguments.Count != 0)
        {
            throw new MappingException(
                "Only the parameterless 'Project().To<T>()' form is supported inside query expression trees.");
        }

        if (UnwrapConversion(node.Object!) is MethodCallExpression projectCall && IsFusionProject(projectCall))
        {
            var queryExpression = Visit(projectCall.Arguments[0]) ?? projectCall.Arguments[0];
            var sourceType = projectCall.Method.GetGenericArguments()[0];
            var targetType = node.Method.GetGenericArguments()[0];

            return BuildQueryableSelect(queryExpression, sourceType, targetType);
        }

        throw new MappingException(
            "Unsupported FusionMapper call in expression tree. " +
            "'To<T>()' can only be rewritten when it is called on 'queryable.Project()'.");
    }

    private Expression InlineProjection(Expression sourceExpression, Type sourceType, Type targetType)
    {
        var lambda =  cache.GetOrAdd((sourceType, targetType), key => MappingBuilder.BuildCreationLambda(key.Source, key.Target));
        var parameter = lambda.Parameters[0];

        if (parameter.Type != sourceExpression.Type &&
            !parameter.Type.IsAssignableFrom(sourceExpression.Type))
        {
            throw new MappingException(
                $"Cannot rewrite 'Map().To<{GetName(targetType)}>()' because source expression type " +
                $"'{GetName(sourceExpression.Type)}' is not assignable to '{GetName(parameter.Type)}'.");
        }

        Expression replacement = parameter.Type == sourceExpression.Type
            ? sourceExpression
            : Expression.Convert(sourceExpression, parameter.Type);

        var body = new ParameterReplacer(parameter, replacement).Visit(lambda.Body) ?? lambda.Body;

        return Visit(body) ?? body;
    }

    private static MethodCallExpression BuildQueryableSelect(
        Expression queryExpression,
        Type sourceType,
        Type targetType)
    {
        var lambda = cache.GetOrAdd((sourceType, targetType), key => MappingBuilder.BuildCreationLambda(key.Source, key.Target)); ;
        var expectedQueryableType = typeof(IQueryable<>).MakeGenericType(sourceType);

        if (!expectedQueryableType.IsAssignableFrom(queryExpression.Type))
        {
            throw new MappingException(
                $"Cannot rewrite 'Project().To<{GetName(targetType)}>()' because the source query type " +
                $"'{GetName(queryExpression.Type)}' is not assignable to '{GetName(expectedQueryableType)}'.");
        }

        Expression source = queryExpression.Type == expectedQueryableType
            ? queryExpression
            : Expression.Convert(queryExpression, expectedQueryableType);

        return Expression.Call(
            typeof(Queryable),
            nameof(Queryable.Select),
            [sourceType, targetType],
            source,
            Expression.Quote(lambda));
    }

    private static bool IsFusionSourceTo(MethodCallExpression node) =>
        node.Object is not null &&
        node.Method.Name == "To" &&
        node.Method.DeclaringType is { IsGenericType: true } declaringType &&
        declaringType.GetGenericTypeDefinition() == typeof(FusionSource<>);

    private static bool IsFusionProjectionTo(MethodCallExpression node) =>
        node.Object is not null &&
        node.Method.Name == "To" &&
        node.Method.DeclaringType is { IsGenericType: true } declaringType &&
        declaringType.GetGenericTypeDefinition() == typeof(FusionProjection<>);

    private static bool IsFusionMap(MethodCallExpression node) =>
        node.Object is null &&
        node.Method.IsGenericMethod &&
        node.Method.DeclaringType == typeof(FusionMapper) &&
        node.Method.Name == "Map" &&
        node.Method.GetGenericArguments().Length == 1 &&
        node.Method.GetParameters().Length == 1;

    private static bool IsFusionProject(MethodCallExpression node) =>
        node.Object is null &&
        node.Method.IsGenericMethod &&
        node.Method.DeclaringType == typeof(FusionMapper) &&
        node.Method.Name == "Project" &&
        node.Method.GetGenericArguments().Length == 1 &&
        node.Method.GetParameters().Length == 1;

    private static Expression UnwrapConversion(Expression expression) =>
        expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            ? UnwrapConversion(unary.Operand)
            : expression;

    private static string GetName(Type type) =>
        type.FullName ?? type.Name;

    public static IQueryable<TTarget> Rewrite<TTarget>(IQueryable<TTarget> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (instance.Visit(query.Expression) is not { } newExpression)
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

    private sealed class ParameterReplacer(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? replacement : base.VisitParameter(node);
    }
}