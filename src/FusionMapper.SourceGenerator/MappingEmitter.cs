using System.Text;
using Microsoft.CodeAnalysis;

namespace FusionMapper.SourceGenerator;

internal static class MappingEmitter
{
    private enum EmitContext
    {
        MethodBody,
        ExpressionTree
    }

    public static IEnumerable<string> Emit(CallKind kind, Mapping mapping)
    {
        return kind switch
        {
            CallKind.SourceTo => EmitCreateBody(mapping),
            CallKind.SourceToExisting => EmitExistingBody(mapping),
            CallKind.ProjectionTo => [EmitExpressionLambda(mapping)],
            _ => throw new InvalidOperationException()
        };
    }

    // ------------------------------------------------------------------
    // Creation body: SourceTo
    // ------------------------------------------------------------------
    private static IEnumerable<string> EmitCreateBody(Mapping mapping)
    {
        if (mapping.SourceType.IsNullableByNullability && !IsNullableValueToNonNullableValue(mapping))
        {
            if (mapping.TargetType.IsReference || mapping.TargetType.IsNullableByNullability)
            {
                yield return $"if (source == null) return {DefaultLiteral(mapping.TargetType)};";
            }
            else
            {
                yield return "global::System.ArgumentNullException.ThrowIfNull(source);";
            }
        }

        if (mapping is CollectionMapping collection && !CanUseCollectionExpression(collection))
        {
            foreach (var line in EmitCustomCollectionCreation(collection, "source"))
            {
                yield return line;
            }

            yield break;
        }

        yield return $"return {EmitCore(mapping, "source", EmitContext.MethodBody)};";
    }

    // ------------------------------------------------------------------
    // Existing body: SourceToExisting
    // ------------------------------------------------------------------
    private static IEnumerable<string> EmitExistingBody(Mapping mapping)
    {
        if (mapping.SourceType.CanBeNullRuntime && !IsNullableValueToNonNullableValue(mapping))
        {
            yield return "if (source == null) return target;";
        }

        if (mapping.TargetType.CanBeNullRuntime)
        {
            yield return $"if (target == null) return {EmitCore(mapping, "source", EmitContext.MethodBody)};";
        }

        switch (mapping)
        {
            case CollectionMapping collection:
                foreach (var line in EmitCollectionMutation(collection))
                {
                    yield return line;
                }

                yield break;

            case ObjectMapping objectMapping:
                var assignments = objectMapping.Bindings
                    .Where(static binding => !binding.IsInitOnly)
                    .Select(binding =>
                        $"target.{binding.TargetMemberName} = {EmitMemberBinding(binding, "source", EmitContext.MethodBody)};")
                    .ToList();

                if (assignments.Count == 0)
                {
                    yield return
                        $"throw new global::FusionMapper.MappingException(\"Mapping into an existing instance of '{objectMapping.TargetType.FullName}' is not supported.\");";

                    yield break;
                }

                foreach (var assignment in assignments)
                {
                    yield return assignment;
                }

                yield return "return target;";
                yield break;

            default:
                yield return $"return {EmitCore(mapping, "source", EmitContext.MethodBody)};";
                yield break;
        }
    }

    // ------------------------------------------------------------------
    // Expression lambda: ProjectionTo
    // ------------------------------------------------------------------
    private static string EmitExpressionLambda(Mapping mapping)
    {
        var body = EmitCore(mapping, "source", EmitContext.ExpressionTree);

        if (mapping.SourceType.IsNullableByNullability && !IsNullableValueToNonNullableValue(mapping))
        {
            body = $"source == null ? {DefaultLiteral(mapping.TargetType)} : {body}";
        }

        return $"static source => {body}";
    }

    // ------------------------------------------------------------------
    // Core emitter
    // ------------------------------------------------------------------
    private static string EmitCore(
        Mapping mapping,
        string sourceExpression,
        EmitContext context)
    {
        return mapping switch
        {
            AssignMapping assignMapping =>
                EmitAssign(assignMapping, sourceExpression),

            ObjectMapping objectMapping =>
                EmitObject(objectMapping, sourceExpression, context),

            CollectionMapping collectionMapping =>
                EmitCollection(collectionMapping, sourceExpression, context),

            _ => throw new InvalidOperationException(
                $"Unsupported mapping type '{mapping.GetType().Name}'.")
        };
    }

    private static string EmitAssign(AssignMapping mapping, string sourceExpression)
    {
        return mapping.Kind switch
        {
            AssignmentKind.SameType => sourceExpression,

            AssignmentKind.ImplicitConversion => sourceExpression,

            AssignmentKind.ExplicitCast =>
                $"({mapping.TargetType.Runtime}){sourceExpression}",

            AssignmentKind.EnumToString =>
                $"{sourceExpression}.ToString()",

            AssignmentKind.StringToEnum =>
                $"global::System.Enum.Parse<{mapping.TargetType.Runtime}>({sourceExpression})",

            _ => throw new InvalidOperationException(
                $"Unsupported assignment kind '{mapping.Kind}'.")
        };
    }

    // ------------------------------------------------------------------
    // Object mapping
    // ------------------------------------------------------------------
    private static string EmitObject(
        ObjectMapping mapping,
        string sourceExpression,
        EmitContext context)
    {
        var arguments = mapping.Constructor.Arguments
            .Select(argument => EmitConstructorArgument(argument, sourceExpression, context));

        var bindings = mapping.Bindings
            .Select(binding =>
                $"{binding.TargetMemberName} = {EmitMemberBinding(binding, sourceExpression, context)}");

        var argumentList = string.Join(", ", arguments);

        var initializer = bindings.Any()
            ? $" {{ {string.Join(", ", bindings)} }}"
            : string.Empty;

        return $"new {mapping.TargetType.Runtime}({argumentList}){initializer}";
    }

    private static string EmitConstructorArgument(
        ConstructorArgument argument,
        string rootExpression,
        EmitContext context)
    {
        if (argument.IsDefault)
        {
            return DefaultLiteral(argument.ArgumentType);
        }

        if (argument.Source is null || argument.Value is null)
        {
            throw new InvalidOperationException(
                "Non-default constructor argument must have both source path and value mapping.");
        }

        var accessExpression = BuildAccessExpression(rootExpression, argument.Source.Value);

        var mappedValue = EmitValue(
            argument.Value,
            accessExpression,
            context);

        return WrapIntermediateNullChecks(
            rootExpression,
            argument.Source.Value,
            argument.ArgumentType,
            mappedValue);
    }

    private static string EmitMemberBinding(
        MemberBinding binding,
        string rootExpression,
        EmitContext context)
    {
        var accessExpression = BuildAccessExpression(rootExpression, binding.Source);

        var mappedValue = EmitValue(
            binding.Value,
            accessExpression,
            context);

        return WrapIntermediateNullChecks(
            rootExpression,
            binding.Source,
            binding.Value.TargetType,
            mappedValue);
    }

    private static string EmitValue(
        Mapping mapping,
        string sourceExpression,
        EmitContext context)
    {
        var core = EmitCore(mapping, sourceExpression, context);

        if (!mapping.SourceType.IsNullableByNullability || IsNullableValueToNonNullableValue(mapping))
        {
            return core;
        }

        return $"({sourceExpression} == null ? {DefaultLiteral(mapping.TargetType)} : {core})";
    }

    // ------------------------------------------------------------------
    // Collection mapping
    // ------------------------------------------------------------------
    private static string EmitCollection(
        CollectionMapping mapping,
        string sourceExpression,
        EmitContext context)
    {
        var itemMapping = EmitValue(
            mapping.ElementMapping,
            "__item",
            context);

        var itemsExpression = itemMapping == "__item"
            ? sourceExpression
            : $"global::System.Linq.Enumerable.Select({sourceExpression}, static __item => {itemMapping})";

        if (context == EmitContext.ExpressionTree)
        {
            return EmitCollectionMaterialization(mapping, itemsExpression);
        }

        if (CanUseCollectionExpression(mapping))
        {
            return $"[.. {itemsExpression}]";
        }

        return EmitCustomCollectionExpression(mapping, sourceExpression);
    }

    private static string EmitCollectionMaterialization(
        CollectionMapping mapping,
        string itemsExpression)
    {
        var elementTypeName = mapping.ElementType.Runtime;

        if (IsArray(mapping.TargetType))
        {
            return $"global::System.Linq.Enumerable.ToArray<{elementTypeName}>({itemsExpression})";
        }

        if (IsKnownCollectionInterface(mapping.TargetType) || IsGenericList(mapping.TargetType))
        {
            return $"global::System.Linq.Enumerable.ToList<{elementTypeName}>({itemsExpression})";
        }

        return $"new {mapping.TargetType.Runtime}({itemsExpression})";
    }

    private static IEnumerable<string> EmitCollectionMutation(CollectionMapping mapping)
    {
        if (!mapping.HasClearMethod || (!mapping.HasAddMethod && !mapping.HasAddRangeMethod))
        {
            yield return "return target;";
            yield break;
        }

        var itemMapping = EmitValue(
            mapping.ElementMapping,
            "__item",
            EmitContext.MethodBody);

        var selectExpression = itemMapping == "__item"
            ? "source"
            : $"global::System.Linq.Enumerable.Select(source, static __item => {itemMapping})";

        yield return
            $"var __mappedItems = global::System.Linq.Enumerable.ToList<{mapping.ElementType.Runtime}>({selectExpression});";

        yield return "target.Clear();";

        if (mapping.HasAddRangeMethod)
        {
            yield return "target.AddRange(__mappedItems);";
        }
        else
        {
            yield return "foreach (var __mappedItem in __mappedItems)";
            yield return "{";
            yield return "    target.Add(__mappedItem);";
            yield return "}";
        }

        yield return "return target;";
    }

    private static IEnumerable<string> EmitCustomCollectionCreation(
    CollectionMapping mapping,
    string sourceExpression)
    {
        var targetType = mapping.TargetType.Runtime;

        if (mapping.HasAddRangeMethod)
        {
            var itemMapping = EmitValue(
                mapping.ElementMapping,
                "__item",
                EmitContext.MethodBody);

            var itemsExpression = itemMapping == "__item"
                ? sourceExpression
                : $"global::System.Linq.Enumerable.Select({sourceExpression}, static __item => {itemMapping})";

            yield return $"var __mappedItems = global::System.Linq.Enumerable.ToList<{mapping.ElementType.Runtime}>({itemsExpression});";
            yield return $"var __result = new {targetType}();";
            yield return "__result.AddRange(__mappedItems);";
            yield return "return __result;";

            yield break;
        }

        if (mapping.HasAddMethod)
        {
            var itemMapping = EmitValue(
                mapping.ElementMapping,
                "__item",
                EmitContext.MethodBody);

            yield return $"var __result = new {targetType}();";
            yield return $"foreach (var __item in {sourceExpression})";
            yield return "{";

            if (itemMapping == "__item")
            {
                yield return "    __result.Add(__item);";
            }
            else
            {
                yield return $"    __result.Add({itemMapping});";
            }

            yield return "}";
            yield return "return __result;";

            yield break;
        }

        // Коллекция реализует IEnumerable<T>, но не даёт ни Add, ни AddRange.
        // В таком случае безопаснее создать пустой экземпляр, чем генерировать [..],
        // который требует Add-паттерн.
        yield return $"var __result = new {targetType}();";
        yield return "return __result;";
    }

    private static string EmitCustomCollectionExpression(
    CollectionMapping mapping,
    string sourceExpression)
    {
        var sb = new StringBuilder();

        sb.Append(
            $"((global::System.Func<{mapping.SourceType.Runtime}, {mapping.TargetType.Runtime}>)(static (__source) => {{ ");

        var first = true;

        foreach (var statement in EmitCustomCollectionCreation(mapping, "__source"))
        {
            if (!first)
            {
                sb.Append(' ');
            }

            sb.Append(statement.Trim());
            first = false;
        }

        sb.Append($" }}))({sourceExpression})");

        return sb.ToString();
    }

    private static bool CanUseCollectionExpression(CollectionMapping mapping)
    {
        return IsArray(mapping.TargetType)
            || IsKnownCollectionInterface(mapping.TargetType)
            || IsGenericList(mapping.TargetType)
            || mapping.HasAddMethod;
    }

    // ------------------------------------------------------------------
    // Source path helpers
    // ------------------------------------------------------------------
    private static string BuildAccessExpression(string rootExpression, SourcePath path)
    {
        var sb = new StringBuilder(rootExpression);

        foreach (var segment in path.Segments)
        {
            sb.Append('.').Append(segment.MemberName);
        }

        return sb.ToString();
    }

    private static string BuildPrefix(
        string rootExpression,
        SourcePath path,
        int index)
    {
        var sb = new StringBuilder(rootExpression);

        for (var i = 0; i <= index; i++)
        {
            sb.Append('.').Append(path.Segments[i].MemberName);
        }

        return sb.ToString();
    }

    private static string WrapIntermediateNullChecks(
        string rootExpression,
        SourcePath path,
        TypeModel targetType,
        string mappedExpression)
    {
        var result = mappedExpression;

        for (var i = path.Segments.Length - 2; i >= 0; i--)
        {
            var segment = path.Segments[i];

            if (!segment.Type.IsNullableByNullability)
            {
                continue;
            }

            var prefix = BuildPrefix(rootExpression, path, i);

            result = $"({prefix} == null ? {DefaultLiteral(targetType)} : {result})";
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Nullability / type helpers
    // ------------------------------------------------------------------
    private static string DefaultLiteral(TypeModel target)
    {
        return target.IsReference && !target.IsNullableByNullability
            ? "default!"
            : "default";
    }

    private static bool IsArray(TypeModel type)
    {
        return type.Runtime.EndsWith("[]", StringComparison.Ordinal);
    }

    private static bool IsGenericList(TypeModel type)
    {
        return type.Runtime.StartsWith(
            "global::System.Collections.Generic.List<",
            StringComparison.Ordinal);
    }

    private static bool IsKnownCollectionInterface(TypeModel type)
    {
        return
            type.Runtime.StartsWith("global::System.Collections.Generic.IEnumerable<", StringComparison.Ordinal) ||
            type.Runtime.StartsWith("global::System.Collections.Generic.ICollection<", StringComparison.Ordinal) ||
            type.Runtime.StartsWith("global::System.Collections.Generic.IList<", StringComparison.Ordinal) ||
            type.Runtime.StartsWith("global::System.Collections.Generic.IReadOnlyCollection<", StringComparison.Ordinal) ||
            type.Runtime.StartsWith("global::System.Collections.Generic.IReadOnlyList<", StringComparison.Ordinal);
    }

    private static bool IsNullableValueToNonNullableValue(Mapping mapping)
    {
        return mapping.SourceType.IsNullableValue
            && mapping.TargetType.IsValueType
            && !mapping.TargetType.IsNullableValue;
    }
}