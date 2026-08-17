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
                {
                    if (collection.Capabilities.IsArray)
                    {
                        yield return
                            $"throw new global::FusionMapper.MappingException(\"Mapping into an existing array of '{collection.TargetType.FullName}' is not supported.\");";

                        yield break;
                    }

                    if (!collection.Capabilities.HasClearMethod ||
                        (!collection.Capabilities.HasAddMethod && !collection.Capabilities.HasAddRangeMethod))
                    {
                        yield return "return target;";
                        yield break;
                    }

                    foreach (var line in EmitCollectionMutationStatements(collection, "source", "target"))
                    {
                        yield return line;
                    }

                    yield return "return target;";
                    yield break;
                }

            case ObjectMapping objectMapping:
                {
                    var statements = EmitObjectMutationStatements(objectMapping, "source", "target").ToList();

                    if (statements.Count == 0)
                    {
                        yield return
                            $"throw new global::FusionMapper.MappingException(\"Mapping into an existing instance of '{objectMapping.TargetType.FullName}' is not supported.\");";

                        yield break;
                    }

                    foreach (var statement in statements)
                    {
                        yield return statement;
                    }

                    yield return "return target;";
                    yield break;
                }

            default:
                yield return $"return {EmitCore(mapping, "source", EmitContext.MethodBody)};";
                yield break;
        }
    }

    private static IEnumerable<string> EmitObjectMutationStatements(
        ObjectMapping mapping,
        string sourceExpression,
        string targetExpression)
    {
        foreach (var member in mapping.Members)
        {
            if (member.IsInitOnly)
            {
                continue;
            }

            var sourceAccess = BuildAccessExpression(sourceExpression, member.Source);

            var mappedValue = EmitValue(
                member.Value,
                sourceAccess,
                EmitContext.MethodBody);

            // Вложенный reference object.
            // Если target.Member уже существует, мутируем его.
            // Если null и можно писать — создаём новый.
            if (member.Value is ObjectMapping nestedObject &&
                member.CanRead &&
                !member.IsTargetMemberValueType &&
                nestedObject.TargetType.IsReference)
            {
                yield return "{";
                yield return $"    var __current = {targetExpression}.{member.TargetMemberName};";
                yield return "    if (__current == null)";
                yield return "    {";

                if (member.CanWrite)
                {
                    yield return $"        {targetExpression}.{member.TargetMemberName} = {mappedValue};";
                }

                yield return "    }";
                yield return "    else";
                yield return "    {";

                // Добавляем проверку на null источника перед рекурсивной мутацией
                if (nestedObject.SourceType.IsNullableByNullability)
                {
                    yield return $"        if ({sourceAccess} != null)";
                    yield return "        {";

                    foreach (var nestedLine in EmitObjectMutationStatements(nestedObject, sourceAccess, "__current"))
                    {
                        yield return $"            {nestedLine}";
                    }

                    yield return "        }";
                }
                else
                {
                    foreach (var nestedLine in EmitObjectMutationStatements(nestedObject, sourceAccess, "__current"))
                    {
                        yield return $"        {nestedLine}";
                    }
                }

                yield return "    }";
                yield return "}";

                continue;
            }

            // Вложенная коллекция.
            if (member.Value is CollectionMapping nestedCollection &&
                member.CanRead &&
                !nestedCollection.Capabilities.IsArray)
            {
                var canMutate =
                    nestedCollection.Capabilities.HasClearMethod &&
                    (nestedCollection.Capabilities.HasAddMethod || nestedCollection.Capabilities.HasAddRangeMethod);

                if (!canMutate && !member.CanWrite)
                {
                    continue;
                }

                yield return "{";
                yield return $"    var __current = {targetExpression}.{member.TargetMemberName};";
                yield return "    if (__current == null)";
                yield return "    {";

                if (member.CanWrite)
                {
                    yield return $"        {targetExpression}.{member.TargetMemberName} = {mappedValue};";
                }

                yield return "    }";

                if (canMutate)
                {
                    yield return "    else";
                    yield return "    {";

                    // Добавляем проверку на null источника перед мутацией коллекции
                    if (nestedCollection.SourceType.IsNullableByNullability)
                    {
                        yield return $"        if ({sourceAccess} != null)";
                        yield return "        {";

                        foreach (var collectionLine in EmitCollectionMutationStatements(
                                     nestedCollection,
                                     sourceAccess,
                                     "__current"))
                        {
                            yield return $"            {collectionLine}";
                        }

                        yield return "        }";
                    }
                    else
                    {
                        foreach (var collectionLine in EmitCollectionMutationStatements(
                                     nestedCollection,
                                     sourceAccess,
                                     "__current"))
                        {
                            yield return $"        {collectionLine}";
                        }
                    }

                    yield return "    }";
                }

                yield return "}";

                continue;
            }

            // Простой член или значение, которое заменяем целиком.
            if (member.CanWrite)
            {
                yield return $"{targetExpression}.{member.TargetMemberName} = {mappedValue};";
            }
        }
    }


    private static IEnumerable<string> EmitCollectionMutationStatements(
    CollectionMapping mapping,
    string sourceExpression,
    string targetExpression)
    {
        if (!mapping.Capabilities.HasClearMethod ||
            (!mapping.Capabilities.HasAddMethod && !mapping.Capabilities.HasAddRangeMethod))
        {
            yield break;
        }

        var itemMapping = EmitValue(
            mapping.ElementMapping,
            "__item",
            EmitContext.MethodBody);

        var selectExpression = itemMapping == "__item"
            ? sourceExpression
            : $"global::System.Linq.Enumerable.Select({sourceExpression}, static __item => {itemMapping})";

        yield return
            $"var __mappedItems = global::System.Linq.Enumerable.ToList<{mapping.ElementTypeName.Runtime}>({selectExpression});";

        yield return $"{targetExpression}.Clear();";

        if (mapping.Capabilities.HasAddRangeMethod)
        {
            yield return $"{targetExpression}.AddRange(__mappedItems);";
        }
        else
        {
            yield return "foreach (var __mappedItem in __mappedItems)";
            yield return "{";
            yield return $"    {targetExpression}.Add(__mappedItem);";
            yield return "}";
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

            AggregateMapping aggregateMapping =>
                EmitAggregate(aggregateMapping, sourceExpression, context),

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
                mapping.SourceType.IsNullableValue
                    ? $"{sourceExpression}.Value.ToString()"
                    : $"{sourceExpression}.ToString()",

            AssignmentKind.StringToEnum =>
                mapping.TargetType.IsNullableValue
                    ? $"({mapping.TargetType.Runtime})global::System.Enum.Parse<{mapping.TargetType.NullableUnderlyingRuntime}>({sourceExpression})"
                    : $"global::System.Enum.Parse<{mapping.TargetType.Runtime}>({sourceExpression})",

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
        var constructor = ChooseConstructor(mapping);

        var arguments = constructor.Parameters
            .Select(parameter => EmitConstructorArgument(parameter, sourceExpression, context));

        var assignedByConstructor = new HashSet<string>(
            constructor.AssignedMemberNames,
            StringComparer.OrdinalIgnoreCase);

        var bindings = mapping.Members
            .Where(member => member.CanWrite)
            .Where(member => !assignedByConstructor.Contains(member.TargetMemberName))
            .Select(member =>
                $"{member.TargetMemberName} = {EmitMemberBinding(member, sourceExpression, context)}");

        var argumentList = string.Join(", ", arguments);

        var initializer = bindings.Any()
            ? $" {{ {string.Join(", ", bindings)} }}"
            : string.Empty;

        return $"new {mapping.TargetType.Runtime}({argumentList}){initializer}";
    }

    private static ConstructorCandidate ChooseConstructor(ObjectMapping mapping)
    {
        if (mapping.Constructors.Length == 0)
        {
            throw new MappingGenerationException(
                $"No suitable constructor or required members are not mapped for type '{mapping.TargetType.FullName}'.");
        }

        return mapping.Constructors
            .OrderByDescending(c => c.SetsRequiredMembers)
            .ThenByDescending(c => c.Parameters.Count(p => p.IsMapped))
            .ThenByDescending(c => c.Parameters.Length)
            .First();
    }


    private static string EmitConstructorArgument(
        ConstructorParameter parameter,
        string rootExpression,
        EmitContext context)
    {
        if (!parameter.IsMapped)
        {
            return DefaultLiteral(parameter.ParameterType);
        }

        if (parameter.Source is null || parameter.Value is null)
        {
            throw new InvalidOperationException(
                "Non-default constructor argument must have both source path and value mapping.");
        }

        var accessExpression = BuildAccessExpression(rootExpression, parameter.Source.Value);

        var mappedValue = EmitValue(
            parameter.Value,
            accessExpression,
            context);

        return WrapIntermediateNullChecks(
            rootExpression,
            parameter.Source.Value,
            parameter.ParameterType,
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

        if (mapping.Capabilities.HasAddRangeMethod || mapping.Capabilities.HasAddMethod)
        {
            return EmitCustomCollectionExpression(mapping, sourceExpression);
        }

        if (mapping.Capabilities.HasEnumerableConstructor)
        {
            return $"new {mapping.TargetType.Runtime}({itemsExpression})";
        }

        if (mapping.Capabilities.HasParameterlessConstructor)
        {
            return $"new {mapping.TargetType.Runtime}()";
        }

        throw new MappingGenerationException(
            $"Cannot materialize collection '{mapping.TargetType.FullName}'.");
    }


    private static string EmitCollectionMaterialization(
        CollectionMapping mapping,
        string itemsExpression)
    {
        var elementTypeName = mapping.ElementTypeName.Runtime;

        if (mapping.Capabilities.IsArray)
        {
            return $"global::System.Linq.Enumerable.ToArray<{elementTypeName}>({itemsExpression})";
        }

        if (mapping.Capabilities.IsKnownCollectionInterface || mapping.Capabilities.IsGenericList)
        {
            return $"global::System.Linq.Enumerable.ToList<{elementTypeName}>({itemsExpression})";
        }

        if (mapping.Capabilities.HasEnumerableConstructor)
        {
            return $"new {mapping.TargetType.Runtime}({itemsExpression})";
        }

        if (mapping.Capabilities.HasParameterlessConstructor)
        {
            // Для expression tree нельзя делать statements с Add.
            // Поэтому если нет конструктора от IEnumerable,
            // безопаснее вернуть пустую коллекцию, если тип это допускает.
            return $"new {mapping.TargetType.Runtime}()";
        }

        throw new MappingGenerationException(
            $"Cannot materialize collection '{mapping.TargetType.FullName}' inside expression tree.");
    }

    private static IEnumerable<string> EmitCustomCollectionCreation(
        CollectionMapping mapping,
        string sourceExpression)
    {
        var targetType = mapping.TargetType.Runtime;

        if (mapping.Capabilities.HasAddRangeMethod)
        {
            var itemMapping = EmitValue(
                mapping.ElementMapping,
                "__item",
                EmitContext.MethodBody);

            var itemsExpression = itemMapping == "__item"
                ? sourceExpression
                : $"global::System.Linq.Enumerable.Select({sourceExpression}, static __item => {itemMapping})";

            yield return
                $"var __mappedItems = global::System.Linq.Enumerable.ToList<{mapping.ElementTypeName.Runtime}>({itemsExpression});";

            yield return $"var __result = new {targetType}();";
            yield return "__result.AddRange(__mappedItems);";
            yield return "return __result;";

            yield break;
        }

        if (mapping.Capabilities.HasAddMethod)
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

        yield return $"var __result = new {targetType}();";
        yield return "return __result;";
    }



    private static string EmitAggregate(
    AggregateMapping mapping,
    string sourceExpression,
    EmitContext context)
    {
        return mapping.Kind switch
        {
            AggregateKind.Count =>
                EmitCount(mapping, sourceExpression),

            AggregateKind.Any =>
                EmitAny(mapping, sourceExpression),

            AggregateKind.All =>
                EmitAll(mapping, sourceExpression),

            AggregateKind.Sum =>
                EmitScalarAggregate("Sum", mapping, sourceExpression),

            AggregateKind.Average =>
                EmitScalarAggregate("Average", mapping, sourceExpression),

            AggregateKind.Max =>
                EmitScalarAggregate("Max", mapping, sourceExpression),

            AggregateKind.Min =>
                EmitScalarAggregate("Min", mapping, sourceExpression),

            AggregateKind.First =>
                EmitFirstOrLast("First", mapping, sourceExpression, context),

            AggregateKind.Last =>
                EmitFirstOrLast("Last", mapping, sourceExpression, context),

            AggregateKind.FirstOrDefault =>
                EmitFirstOrLast("FirstOrDefault", mapping, sourceExpression, context),

            AggregateKind.LastOrDefault =>
                EmitFirstOrLast("LastOrDefault", mapping, sourceExpression, context),

            _ => throw new InvalidOperationException(
                $"Unsupported aggregate kind '{mapping.Kind}'.")
        };
    }

    private static string EmitCount(AggregateMapping mapping, string sourceExpression)
    {
        var result = mapping.SourceHasCountProperty
            ? $"{sourceExpression}.Count"
            : $"global::System.Linq.Enumerable.Count({sourceExpression})";

        return ApplyAggregateResultMapping(mapping, result);
    }

    private static string EmitAny(AggregateMapping mapping, string sourceExpression)
    {
        if (mapping.Selector is null)
        {
            return ApplyAggregateResultMapping(
                mapping,
                $"global::System.Linq.Enumerable.Any({sourceExpression})");
        }

        var predicateBody = BuildAccessExpression("__item", mapping.Selector.Value);

        return ApplyAggregateResultMapping(
            mapping,
            $"global::System.Linq.Enumerable.Any({sourceExpression}, static __item => {predicateBody})");
    }

    private static string EmitAll(AggregateMapping mapping, string sourceExpression)
    {
        var predicateBody = mapping.Selector is null
            ? "__item"
            : BuildAccessExpression("__item", mapping.Selector.Value);

        return ApplyAggregateResultMapping(
            mapping,
            $"global::System.Linq.Enumerable.All({sourceExpression}, static __item => {predicateBody})");
    }

    private static string EmitScalarAggregate(
    string methodName,
    AggregateMapping mapping,
    string sourceExpression)
    {
        if (mapping.Selector is null)
        {
            return
                $"global::System.Linq.Enumerable.{methodName}({sourceExpression})";
        }

        var selectorBody = BuildAccessExpression("__item", mapping.Selector.Value);

        return
            $"global::System.Linq.Enumerable.{methodName}({sourceExpression}, static __item => {selectorBody})";
    }

    private static string EmitFirstOrLast(
    string methodName,
    AggregateMapping mapping,
    string sourceExpression,
    EmitContext context)
    {
        // Вариант без селектора:
        // ItemsFirst -> source.Items.First()
        // Но если элемент нужно маппить, делаем Select(...).First(),
        // чтобы не вычислять First несколько раз.
        if (mapping.Selector is null)
        {
            if (mapping.ElementMapping is null)
            {
                return $"global::System.Linq.Enumerable.{methodName}({sourceExpression})";
            }

            var elementExpression = EmitValue(
                mapping.ElementMapping,
                "__item",
                context);

            if (elementExpression == "__item")
            {
                return $"global::System.Linq.Enumerable.{methodName}({sourceExpression})";
            }

            return
                $"global::System.Linq.Enumerable.{methodName}(" +
                $"global::System.Linq.Enumerable.Select({sourceExpression}, static __item => {elementExpression}))";
        }

        // Вариант с селектором:
        // ItemsNameFirstOrDefault -> source.Items.Select(x => x.Name).FirstOrDefault()
        var selectorExpression = BuildAccessExpression("__item", mapping.Selector.Value);

        var projected =
            $"global::System.Linq.Enumerable.Select({sourceExpression}, static __item => {selectorExpression})";

        var result =
            $"global::System.Linq.Enumerable.{methodName}({projected})";

        return ApplyAggregateResultMapping(mapping, result);
    }

    private static string ApplyAggregateResultMapping(
    AggregateMapping mapping,
    string expression)
    {
        if (mapping.ResultMapping is null)
        {
            return expression;
        }

        // Важно: используем EmitCore, а не EmitValue,
        // чтобы не дублировать агрегатное выражение в null-check.
        return EmitCore(
            mapping.ResultMapping,
            expression,
            EmitContext.ExpressionTree);
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
        return mapping.Capabilities.IsArray
            || mapping.Capabilities.IsKnownCollectionInterface
            || mapping.Capabilities.IsGenericList
            || mapping.Capabilities.HasAddMethod;
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