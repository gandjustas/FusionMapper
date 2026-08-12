using System.Linq;
using System.Linq.Expressions;

namespace FusionMapper;

internal sealed class ExpressionRewriter : ExpressionVisitor
{
    protected override Expression VisitMethodCall(MethodCallExpression node)
    {
        if (IsFusionSourceTo(node))
        {
            if (node.Arguments.Count != 0)
            {
                throw new MappingException(
                    "Mapping into an existing object using 'Map().To(target)' is not supported inside query expression trees.");
            }

            var instance = UnwrapConversion(node.Object!);

            if (instance is MethodCallExpression mapCall && IsFusionMap(mapCall))
            {
                var sourceExpression = Visit(mapCall.Arguments[0]) ?? mapCall.Arguments[0];
                var sourceType = mapCall.Method.GetGenericArguments()[0];
                var targetType = node.Method.GetGenericArguments()[0];

                return InlineProjection(sourceExpression, sourceType, targetType);
            }

            throw new MappingException(
                "Unsupported FusionMapper call in expression tree. 'To<T>()' can only be rewritten when it is called on 'x.Map()'.");
        }

        if (IsFusionProjectionTo(node))
        {
            if (node.Arguments.Count != 0)
            {
                throw new MappingException(
                    "Only the parameterless 'Project().To<T>()' form is supported inside query expression trees.");
            }

            var instance = UnwrapConversion(node.Object!);

            if (instance is MethodCallExpression projectCall && IsFusionProject(projectCall))
            {
                var queryExpression = Visit(projectCall.Arguments[0]) ?? projectCall.Arguments[0];
                var sourceType = projectCall.Method.GetGenericArguments()[0];
                var targetType = node.Method.GetGenericArguments()[0];

                return BuildQueryableSelect(queryExpression, sourceType, targetType);
            }

            throw new MappingException(
                "Unsupported FusionMapper call in expression tree. 'To<T>()' can only be rewritten when it is called on 'queryable.Project()'.");
        }

        if (IsFusionMap(node))
        {
            throw new MappingException(
                "Unsupported FusionMapper call in expression tree. 'Map()' must be immediately followed by '.To<T>()'.");
        }

        if (IsFusionProject(node))
        {
            throw new MappingException(
                "Unsupported FusionMapper call in expression tree. 'Project()' must be immediately followed by '.To<T>()'.");
        }

        if (node.Method.DeclaringType == typeof(FusionMapper))
        {
            throw new MappingException(
                $"Unsupported FusionMapper call '{node.Method.Name}' inside query expression tree.");
        }

        return base.VisitMethodCall(node)!;
    }

    private Expression InlineProjection(Expression sourceExpression, Type sourceType, Type targetType)
    {
        var lambda = MappingBuilder.BuildCreationLambda(sourceType, targetType);
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
        var lambda = MappingBuilder.BuildCreationLambda(sourceType, targetType);
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

    private sealed class ParameterReplacer(ParameterExpression parameter, Expression replacement) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == parameter ? replacement : base.VisitParameter(node);
    }
}