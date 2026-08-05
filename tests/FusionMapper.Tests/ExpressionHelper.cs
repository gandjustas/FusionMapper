using System.Linq.Expressions;

namespace FusionMapper.Tests;

public static class ExpressionHelper
{
    public static bool ContainsMethodName(Expression? expression, string methodName)
    {
        if (expression is null)
            return false;

        var scanner = new MethodNameScanner(methodName);
        scanner.Visit(expression);
        return scanner.Found;
    }

    private sealed class MethodNameScanner(string methodName) : ExpressionVisitor
    {
        private readonly string _methodName = methodName;

        public bool Found { get; private set; }

        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (string.Equals(node.Method.Name, _methodName, StringComparison.Ordinal))
            {
                Found = true;
            }

            return base.VisitMethodCall(node);
        }
    }
}
