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
        if (mapping is CollectionMapping
            {
                Plan.MethodBodyCreation: CollectionCreationKind.Unsupported
            } unsupportedCollection)
        {
            throw new MappingGenerationException(
                $"Cannot materialize collection '{unsupportedCollection.TargetType.FullName}'. " +
                "The target collection does not have a suitable constructor, Add or AddRange.");
        }

        if (mapping.SourceType.IsNullableByNullability &&
            !IsNullableValueToNonNullableValue(mapping))
        {
            if (mapping.TargetType.IsReference || mapping.TargetType.IsNullableByNullability)
            {
                yield return $"if (source == null) return {DefaultLiteral(mapping.TargetType)};";
            }
            else
            {
                yield return "if (source == null) throw new global::System.InvalidOperationException(\"Cannot map null source to a non-nullable value type.\");";
            }
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
                    if (collection.Plan.IsArray)
                    {
                        yield return
                            $"throw new global::FusionMapper.MappingException(\"Mapping into an existing array of '{collection.TargetType.FullName}' is not supported.\");";
                        yield break;
                    }

                    if (collection.Plan.Mutation == CollectionMutationKind.None)
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
            switch (member.MutationKind)
            {
                case MemberMutationKind.Skip:
                    continue;

                case MemberMutationKind.Assign:
                    {
                        var sourceAccess = BuildAccessExpression(sourceExpression, member.Source);
                        var mappedValue = EmitValue(member.Value, sourceAccess, EmitContext.MethodBody);

                        if (member.Value.SourceType.CanBeNullRuntime &&
                            member.Value.TargetType.IsValueType &&
                            !member.Value.TargetType.IsNullableValue)
                        {
                            var guard = $"{sourceAccess} != null";
                            guard = WrapIntermediateNullChecks(
                                sourceExpression,
                                member.Source,
                                "false",
                                guard);

                            yield return $"if ({guard})";
                            yield return "{";
                            yield return $"    {targetExpression}.{member.TargetMemberName} = {mappedValue};";
                            yield return "}";
                        }
                        else
                        {
                            yield return $"{targetExpression}.{member.TargetMemberName} = {mappedValue};";
                        }

                        continue;
                    }

                case MemberMutationKind.MutateObject:
                    {
                        foreach (var statement in EmitNestedObjectMutation(member, sourceExpression, targetExpression))
                        {
                            yield return statement;
                        }

                        continue;
                    }

                case MemberMutationKind.MutateCollection:
                    {
                        foreach (var statement in EmitNestedCollectionMutation(member, sourceExpression, targetExpression))
                        {
                            yield return statement;
                        }

                        continue;
                    }
            }
        }
    }

    private static IEnumerable<string> EmitNestedObjectMutation(
        MemberBinding member,
        string sourceExpression,
        string targetExpression)
    {
        var sourceAccess = BuildAccessExpression(sourceExpression, member.Source);
        var mappedValue = EmitValue(member.Value, sourceAccess, EmitContext.MethodBody);

        yield return "{";
        yield return $"    var __current = {targetExpression}.{member.TargetMemberName};";
        yield return "    if (__current == null)";
        yield return "    {";

        if (member.CanWrite)
        {
            if (member.Value.SourceType.CanBeNullRuntime)
            {
                var guard = $"{sourceAccess} != null";
                guard = WrapIntermediateNullChecks(
                    sourceExpression,
                    member.Source,
                    "false",
                    guard);

                yield return $"        if ({guard})";
                yield return "        {";
                yield return $"            {targetExpression}.{member.TargetMemberName} = {mappedValue};";
                yield return "        }";
            }
            else
            {
                yield return $"        {targetExpression}.{member.TargetMemberName} = {mappedValue};";
            }
        }

        yield return "    }";
        yield return "    else";
        yield return "    {";

        var nestedMapping = (ObjectMapping)member.Value;

        if (nestedMapping.SourceType.IsNullableByNullability)
        {
            yield return $"        if ({sourceAccess} != null)";
            yield return "        {";

            foreach (var nestedLine in EmitObjectMutationStatements(nestedMapping, sourceAccess, "__current"))
            {
                yield return $"            {nestedLine}";
            }

            yield return "        }";
        }
        else
        {
            foreach (var nestedLine in EmitObjectMutationStatements(nestedMapping, sourceAccess, "__current"))
            {
                yield return $"        {nestedLine}";
            }
        }

        yield return "    }";
        yield return "}";
    }


    private static IEnumerable<string> EmitNestedCollectionMutation(
        MemberBinding member,
        string sourceExpression,
        string targetExpression)
    {
        var sourceAccess = BuildAccessExpression(sourceExpression, member.Source);
        var mappedValue = EmitValue(member.Value, sourceAccess, EmitContext.MethodBody);

        var collectionMapping = (CollectionMapping)member.Value;

        yield return "{";
        yield return $"    var __current = {targetExpression}.{member.TargetMemberName};";
        yield return "    if (__current == null)";
        yield return "    {";

        if (member.CanWrite)
        {
            if (member.Value.SourceType.CanBeNullRuntime)
            {
                var guard = $"{sourceAccess} != null";
                guard = WrapIntermediateNullChecks(
                    sourceExpression,
                    member.Source,
                    "false",
                    guard);

                yield return $"        if ({guard})";
                yield return "        {";
                yield return $"            {targetExpression}.{member.TargetMemberName} = {mappedValue};";
                yield return "        }";
            }
            else
            {
                yield return $"        {targetExpression}.{member.TargetMemberName} = {mappedValue};";
            }
        }

        yield return "    }";
        yield return "    else";
        yield return "    {";

        if (collectionMapping.SourceType.IsNullableByNullability)
        {
            yield return $"        if ({sourceAccess} != null)";
            yield return "        {";

            foreach (var collectionLine in EmitCollectionMutationStatements(collectionMapping, sourceAccess, "__current"))
            {
                yield return $"            {collectionLine}";
            }

            yield return "        }";
        }
        else
        {
            foreach (var collectionLine in EmitCollectionMutationStatements(collectionMapping, sourceAccess, "__current"))
            {
                yield return $"        {collectionLine}";
            }
        }

        yield return "    }";
        yield return "}";
    }

    private static IEnumerable<string> EmitCollectionMutationStatements(
        CollectionMapping mapping,
        string sourceExpression,
        string targetExpression)
    {
        if (mapping.Plan.Mutation == CollectionMutationKind.None)
        {
            yield break;
        }

        if (IsIdentityCollectionMapping(mapping) && mapping.TargetType.IsReference)
        {
            yield return $"var __sourceItems = {sourceExpression};";
            yield return $"if (!global::System.Object.ReferenceEquals({targetExpression}, __sourceItems))";
            yield return "{";
            yield return $"    {targetExpression}.Clear();";

            if (mapping.Plan.Mutation == CollectionMutationKind.ClearAddRange)
            {
                yield return $"    {targetExpression}.AddRange(__sourceItems);";
            }
            else
            {
                yield return "    foreach (var __item in __sourceItems)";
                yield return "    {";
                yield return $"        {targetExpression}.Add(__item);";
                yield return "    }";
            }

            yield return "}";
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

        if (mapping.Plan.Mutation == CollectionMutationKind.ClearAddRange)
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
        var arguments = mapping.Constructor.Arguments
            .Select(parameter => EmitConstructorArgument(parameter, sourceExpression, context));

        var bindings = mapping.CreationMembers
            .Select(member =>
                $"{member.TargetMemberName} = {EmitMemberBinding(member, sourceExpression, context)}");

        var argumentList = string.Join(", ", arguments);

        var initializer = bindings.Any()
            ? $" {{ {string.Join(", ", bindings)} }}"
            : string.Empty;

        return $"new {mapping.TargetType.Runtime}({argumentList}){initializer}";
    }



    private static string EmitConstructorArgument(
        ConstructorArgument parameter,
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

        if (!mapping.SourceType.IsNullableByNullability ||
            IsNullableValueToNonNullableValue(mapping))
        {
            return core;
        }

        if (mapping is AssignMapping
            {
                Kind: AssignmentKind.SameType or AssignmentKind.ImplicitConversion
            } &&
            mapping.TargetType.IsNullableByNullability)
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
        var kind = context == EmitContext.ExpressionTree
            ? mapping.Plan.ExpressionTreeCreation
            : mapping.Plan.MethodBodyCreation;

        var itemsExpression = EmitProjectedItems(mapping, sourceExpression, context);

        return kind switch
        {
            CollectionCreationKind.Array =>
                $"global::System.Linq.Enumerable.ToArray<{mapping.ElementTypeName.Runtime}>({itemsExpression})",

            CollectionCreationKind.List =>
                $"global::System.Linq.Enumerable.ToList<{mapping.ElementTypeName.Runtime}>({itemsExpression})",

            CollectionCreationKind.CollectionExpression =>
                $"[.. {itemsExpression}]",

            CollectionCreationKind.EnumerableConstructor =>
                $"new {mapping.TargetType.Runtime}({itemsExpression})",

            CollectionCreationKind.AddRangeClosure =>
                EmitAddRangeClosure(mapping, sourceExpression, context),

            CollectionCreationKind.AddLoopClosure =>
                EmitAddLoopClosure(mapping, sourceExpression, context),

            CollectionCreationKind.Unsupported =>
                throw new MappingGenerationException(
                    $"Cannot materialize collection '{mapping.TargetType.FullName}'."),

            _ => throw new InvalidOperationException()
        };
    }

    private static string EmitProjectedItems(
    CollectionMapping mapping,
    string sourceExpression,
    EmitContext context)
    {
        var itemMapping = EmitValue(
            mapping.ElementMapping,
            "__item",
            context);

        return itemMapping == "__item"
            ? sourceExpression
            : $"global::System.Linq.Enumerable.Select({sourceExpression}, static __item => {itemMapping})";
    }

    private static string EmitAddRangeClosure(
    CollectionMapping mapping,
    string sourceExpression,
    EmitContext context)
    {
        var itemsExpression = EmitProjectedItems(mapping, "__source", context);

        return
            $"((global::System.Func<{mapping.SourceType.Runtime}, {mapping.TargetType.Runtime}>)" +
            $"(static (__source) => {{ " +
            $"var __mappedItems = global::System.Linq.Enumerable.ToList<{mapping.ElementTypeName.Runtime}>({itemsExpression}); " +
            $"var __result = new {mapping.TargetType.Runtime}(); " +
            $"__result.AddRange(__mappedItems); " +
            $"return __result; " +
            $"}}))({sourceExpression})";
    }


    private static string EmitAddLoopClosure(
    CollectionMapping mapping,
    string sourceExpression,
    EmitContext context)
    {
        var itemMapping = EmitValue(
            mapping.ElementMapping,
            "__item",
            context);

        var addStatement = itemMapping == "__item"
            ? "__result.Add(__item);"
            : $"__result.Add({itemMapping});";

        return
            $"((global::System.Func<{mapping.SourceType.Runtime}, {mapping.TargetType.Runtime}>)" +
            $"(static (__source) => {{ " +
            $"var __result = new {mapping.TargetType.Runtime}(); " +
            $"foreach (var __item in __source) {{ {addStatement} }} " +
            $"return __result; " +
            $"}}))({sourceExpression})";
    }


    private static string EmitAggregate(
        AggregateMapping mapping,
        string sourceExpression,
        EmitContext context)
    {
        var sequence = sourceExpression;

        if (mapping.Kind is AggregateKind.First
                or AggregateKind.Last
                or AggregateKind.FirstOrDefault
                or AggregateKind.LastOrDefault &&
            mapping.Predicate is { } predicate1)
        {
            sequence = EmitWhere(sequence, predicate1);
        }

        if (mapping.Kind is not (AggregateKind.Any or AggregateKind.All) &&
            mapping.Projection is { } projection)
        {
            sequence = EmitSelect(sequence, projection, context);
        }

        var result = mapping.Kind switch
        {
            AggregateKind.Count when mapping.UseCountProperty &&
                                     mapping.Projection is null &&
                                     mapping.Predicate is null =>
                $"{sequence}.Count",

            AggregateKind.Count =>
                $"global::System.Linq.Enumerable.Count({sequence})",

            AggregateKind.Any when mapping.Predicate is { } predicate2 =>
                $"global::System.Linq.Enumerable.Any({sequence}, static __item => {EmitPredicateBody(predicate2)})",

            AggregateKind.Any =>
                $"global::System.Linq.Enumerable.Any({sequence})",

            AggregateKind.All when mapping.Predicate is { } predicate3 =>
                $"global::System.Linq.Enumerable.All({sequence}, static __item => {EmitPredicateBody(predicate3)})",

            AggregateKind.All =>
                $"global::System.Linq.Enumerable.All({sequence}, static __item => __item)",

            AggregateKind.Sum =>
                $"global::System.Linq.Enumerable.Sum({sequence})",

            AggregateKind.Average =>
                $"global::System.Linq.Enumerable.Average({sequence})",

            AggregateKind.Max =>
                $"global::System.Linq.Enumerable.Max({sequence})",

            AggregateKind.Min =>
                $"global::System.Linq.Enumerable.Min({sequence})",

            AggregateKind.First =>
                $"global::System.Linq.Enumerable.First({sequence})",

            AggregateKind.Last =>
                $"global::System.Linq.Enumerable.Last({sequence})",

            AggregateKind.FirstOrDefault =>
                $"global::System.Linq.Enumerable.FirstOrDefault({sequence})",

            AggregateKind.LastOrDefault =>
                $"global::System.Linq.Enumerable.LastOrDefault({sequence})",

            _ => throw new InvalidOperationException(
                $"Unsupported aggregate kind '{mapping.Kind}'.")
        };

        if (mapping.RequiresNullForgiving)
        {
            result = $"({result})!";
        }

        if (mapping.ResultMapping is null)
        {
            return result;
        }

        return EmitCore(mapping.ResultMapping, result, context);
    }

    private static string EmitPredicateBody(AggregatePredicate predicate)
    {
        var body = BuildAccessExpression("__item", predicate.Path);

        return WrapIntermediateNullChecks(
            "__item",
            predicate.Path,
            "false",
            body);
    }

    private static string EmitWhere(string sequence, AggregatePredicate predicate)
    {
        var body = EmitPredicateBody(predicate);

        return $"global::System.Linq.Enumerable.Where({sequence}, static __item => {body})";
    }

    private static string EmitSelect(
    string sequence,
    AggregateProjection projection,
    EmitContext context)
    {
        var accessExpression = projection.Path is null
            ? "__item"
            : BuildAccessExpression("__item", projection.Path.Value);

        var body = EmitValue(
            projection.Mapping,
            accessExpression,
            context);

        if (projection.Path is { } path)
        {
            body = WrapIntermediateNullChecks(
                "__item",
                path,
                projection.Mapping.TargetType,
                body);
        }

        if (body == "__item")
        {
            return sequence;
        }

        return $"global::System.Linq.Enumerable.Select({sequence}, static __item => {body})";
    }

    private static bool IsIdentityCollectionMapping(CollectionMapping mapping)
    {
        return mapping.ElementMapping is AssignMapping
        {
            Kind: AssignmentKind.SameType
        };
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
        return WrapIntermediateNullChecks(
            rootExpression,
            path,
            DefaultLiteral(targetType),
            mappedExpression);
    }

    private static string WrapIntermediateNullChecks(
    string rootExpression,
    SourcePath path,
    string defaultLiteral,
    string mappedExpression)
    {
        var result = mappedExpression;
        var segments = path.Segments;

        for (var i = segments.Length - 2; i >= 0; i--)
        {
            var segment = segments[i];

            if (!segment.Type.IsNullableByNullability)
            {
                continue;
            }

            var prefix = BuildPrefix(rootExpression, path, i);

            result = $"({prefix} == null ? {defaultLiteral} : {result})";
        }

        return result;
    }

    // ------------------------------------------------------------------
    // Nullability / type helpers
    // ------------------------------------------------------------------
    private static string DefaultLiteral(TypeModel target)
    {
        if (target.IsNullableValue)
        {
            if (target.NullableUnderlyingRuntime is null)
            {
                return $"default({target.Signature})";
            }

            return $"new global::System.Nullable<{target.NullableUnderlyingRuntime}>()";
        }

        return target.IsReference && !target.IsNullableByNullability
            ? "default!"
            : "default";
    }


    private static bool IsNullableValueToNonNullableValue(Mapping mapping)
    {
        return mapping.SourceType.IsNullableValue
            && mapping.TargetType.IsValueType
            && !mapping.TargetType.IsNullableValue;
    }
}