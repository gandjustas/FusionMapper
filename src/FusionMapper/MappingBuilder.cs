using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FusionMapper;

static class MappingBuilder
{
    private static readonly string[] CollectionOperations = [
        "FirstOrDefault",
        "LastOrDefault",
        "First",
        "Last",
        "Count",
        "Average",
        "Sum",
        "Max",
        "Min",
        "Any",
        "All"
    ];

    public static Expression<Func<TSource, TTarget>> BuildCreationLambda<TSource, TTarget>()
    {
        var sourceType = typeof(TSource);
        var targetType = typeof(TTarget);
        var sourceParam = Expression.Parameter(sourceType, "source");

        MappingPath path = new();
        using var guard = path.Push(targetType, sourceType);


        if (BuildMappingBody(
            sourceParam, sourceNullability: NullabilityState.Unknown,
            targetType, targetNullability: NullabilityState.Unknown,
            path) is { } body)
        {
            return Expression.Lambda<Func<TSource, TTarget>>(body, sourceParam);
        }
        else
        {
            throw new MappingException($"Can't map {sourceParam.Type} to {targetType}.");
        }
    }

    public static Expression<Action<TSource, TTarget>> BuildAssignmentExpression<TSource, TTarget>()
    {

        var sourceParam = Expression.Parameter(typeof(TSource), "source");
        var targetParam = Expression.Parameter(typeof(TTarget), "target");

        var targetType = typeof(TTarget);
        MappingPath path = new();


        var writableProperties = targetType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !IsInitOnly(p))
            .Cast<MemberInfo>();


        var writableFields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !f.IsInitOnly && !f.IsLiteral)
            .Cast<MemberInfo>();


        var assignExpressions = writableProperties
            .Concat(writableFields)
            .Select(member =>
            {
                var targetType = GetMemberType(member);
                if (GetSourceMemberAccess(sourceParam, member.Name, NullabilityState.NotNull).FirstOrDefault() is
                    not (Expression accessExpr, NullabilityState nullability)) return null;

                using var guard = path.Push(GetMemberType(member), accessExpr.Type);
                if (BuildMappingBody(accessExpr, nullability,
                    targetType, GetMemberNullability(member).WriteState,
                    path) is not { } mappedExpr) return null;

                return (Expression)Expression.Assign(Expression.MakeMemberAccess(targetParam, member), mappedExpr);
            })
            .Where(x => x is not null);



        var readOnlyProperties = targetType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p =>
                p.CanRead &&
                !p.CanWrite &&
                p.GetIndexParameters().Length == 0)
            .Cast<MemberInfo>();

        var readOnlyFields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.IsInitOnly && !f.IsLiteral)
            .Cast<MemberInfo>();

        var fillCollecitonExpressions = readOnlyProperties
            .Concat(readOnlyFields)
            .Select(member =>
            {
                var targetMemberType = GetMemberType(member);

                if (!IsCollectionType(targetMemberType, out _)) return null;

                if (GetSourceMemberAccess(sourceParam, member.Name, NullabilityState.NotNull).FirstOrDefault() is
                not (Expression accessExpr, NullabilityState _)) return null;
                // TODO: handle nullable
                return BuildReadOnlyCollectionMutation(
                        targetParam,
                        member,
                        targetMemberType,
                        accessExpr,
                        path);
            })
            .Where(x => x is not null);

        var body = Expression.Block(typeof(void), assignExpressions.Concat(fillCollecitonExpressions)!);
        if (body.Expressions.Count == 0) throw new MappingException($"No properties were mapped");

        return Expression.Lambda<Action<TSource, TTarget>>(body, sourceParam, targetParam);
    }

    private static BlockExpression? BuildReadOnlyCollectionMutation(
        ParameterExpression targetParam,
        MemberInfo member,
        Type targetCollectionType,
        Expression sourceAccess,
        MappingPath path)
    {
        if (!IsCollectionType(targetCollectionType, out var targetElementType) ||
            targetElementType is null)
        {
            return null;
        }

        if (!IsCollectionType(sourceAccess.Type, out var sourceElementType) ||
            sourceElementType is null)
        {
            return null;
        }

        if (targetCollectionType.IsArray)
        {
            throw new MappingException(
                $"Cannot update read-only array member '{member.Name}'. " +
                $"Source type: '{sourceAccess.Type.FullName}'. " +
                $"Target member type: '{targetCollectionType.FullName}'.");
        }

        var clearMethod = FindCollectionClearMethod(targetCollectionType);
        var addRangeMethod = FindCollectionAddRangeMethod(targetCollectionType, targetElementType);
        var addMethod = FindCollectionAddMethod(targetCollectionType, targetElementType);

        if (clearMethod is null || (addRangeMethod is null && addMethod is null))
        {
            throw new MappingException(
                $"Read-only collection member '{member.Name}' cannot be mutated. " +
                $"Target member type: '{targetCollectionType.FullName}'. " +
                "The collection must expose a public Clear method and either AddRange or Add.");
        }

        var memberAccess = Expression.MakeMemberAccess(targetParam, member);

        var existingVar = Expression.Variable(targetCollectionType, $"{member.Name}_existing");
        var sourceVar = Expression.Variable(sourceAccess.Type, $"{member.Name}_source");

        var listType = typeof(List<>).MakeGenericType(targetElementType);
        var mappedListVar = Expression.Variable(listType, $"{member.Name}_mapped");
        var iVar = Expression.Variable(typeof(int), $"{member.Name}_index");

        List<Expression> body = [];

        body.Add(Expression.Assign(existingVar, memberAccess));

        if (CanBeNull(targetCollectionType))
        {
            body.Add(Expression.IfThen(
                Expression.Equal(existingVar, Expression.Constant(null, targetCollectionType)),
                Expression.Throw(
                    Expression.New(
                        typeof(MappingException).GetConstructor([typeof(string)])!,
                        Expression.Constant(
                            $"Read-only collection member '{member.Name}' is null and cannot be mutated.")),
                    typeof(void))));
        }

        body.Add(Expression.Assign(sourceVar, sourceAccess));

        var itemParam = Expression.Parameter(sourceElementType, "item");
        var mappedItem = BuildMappingBody(itemParam, sourceNullability: NullabilityState.Unknown, targetType: targetElementType, targetNullability: NullabilityState.Unknown, path: path);
        var lambda = Expression.Lambda(mappedItem, itemParam);

        var selectCall = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Select),
            [sourceElementType, targetElementType],
            sourceVar,
            lambda);

        var toListCall = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.ToList),
            [targetElementType],
            selectCall);

        Expression mappedListValue;

        if (CanBeNull(sourceVar.Type))
        {
            mappedListValue = Expression.Condition(
                Expression.Equal(sourceVar, Expression.Constant(null, sourceVar.Type)),
                Expression.New(listType.GetConstructor(Type.EmptyTypes)!),
                toListCall,
                listType);
        }
        else
        {
            mappedListValue = toListCall;
        }

        body.Add(Expression.Assign(mappedListVar, mappedListValue));
        body.Add(Expression.Call(existingVar, clearMethod));

        if (addRangeMethod is not null)
        {
            body.Add(Expression.Call(existingVar, addRangeMethod, mappedListVar));
        }
        else
        {
            body.Add(Expression.Assign(iVar, Expression.Constant(0)));

            var breakLabel = Expression.Label("break");

            var addCall = Expression.Call(
                existingVar,
                addMethod!,
                Expression.Property(mappedListVar, "Item", iVar));

            var increment = Expression.Assign(
                iVar,
                Expression.Add(iVar, Expression.Constant(1)));

            var loopBody = Expression.IfThenElse(
                Expression.LessThan(
                    iVar,
                    Expression.Property(mappedListVar, "Count")),
                Expression.Block(typeof(void), addCall, increment),
                Expression.Break(breakLabel));

            var loop = Expression.Loop(loopBody, breakLabel);
            body.Add(loop);
        }

        return Expression.Block(
            typeof(void),
            [existingVar, sourceVar, mappedListVar, iVar],
            body);

    }

    private static MethodInfo? FindCollectionClearMethod(Type type)
    {
        var method = type.GetMethod(
            "Clear",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            Type.EmptyTypes,
            null);

        if (method is not null)
            return method;

        return type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == "Clear" &&
                m.GetParameters().Length == 0);
    }

    private static MethodInfo? FindCollectionAddRangeMethod(Type type, Type elementType)
    {
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);

        var method = type.GetMethod(
            "AddRange",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            [enumerableType],
            null);

        if (method is not null)
            return method;

        return type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == "AddRange" &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.IsAssignableFrom(enumerableType));
    }

    private static MethodInfo? FindCollectionAddMethod(Type type, Type elementType)
    {
        var method = type.GetMethod(
            "Add",
            BindingFlags.Public | BindingFlags.Instance,
            null,
            [elementType],
            null);

        if (method is not null)
            return method;

        return type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == "Add" &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.IsAssignableFrom(elementType));
    }

    private static Expression? BuildMappingBody(
        Expression sourceExpr,
        NullabilityState sourceNullability,
        Type targetType,
        NullabilityState targetNullability,
        MappingPath path)
    {
        if (targetType.IsPointer || targetType.IsFunctionPointer) return null;

        var sourceType = sourceExpr.Type;

        if (targetType.IsAssignableFrom(sourceType))
        {
            if (targetType != sourceType && targetType.IsValueType && Nullable.GetUnderlyingType(targetType) != null)
                return Expression.Convert(sourceExpr, targetType);
            return sourceExpr;
        }
        if (sourceType.IsValueType 
            && targetType.IsValueType 
            && TryConvert(sourceExpr, targetType) is { } e)
            return e;

        if (targetType.IsPrimitive) return null;

        var sourceCanBeNull =
            CanBeNull(sourceType) &&
            sourceNullability != NullabilityState.NotNull;

        var targetAcceptsNull =
            CanBeNull(targetType) &&
            targetNullability != NullabilityState.NotNull;

        var nonNullBody = BuildNonNullMappingBody(sourceExpr, NullabilityState.NotNull, targetType, path);
        if (sourceCanBeNull && nonNullBody is not null)
        {
            Expression nullBranch;

            if (targetAcceptsNull || Nullable.GetUnderlyingType(targetType) is not null)
            {
                nullBranch = Expression.Default(targetType);
            }
            else
            {
                nullBranch = Expression.Throw(
                    Expression.New(
                        typeof(MappingException).GetConstructor([typeof(string)])!,
                        Expression.Constant(
                            $"Cannot map null source to non-nullable value type '{targetType.FullName}'.")),
                    targetType);
            }

            return Expression.Condition(
                Expression.Equal(sourceExpr, Expression.Constant(null, sourceType)),
                nullBranch,
                nonNullBody,
                targetType);
        }

        return nonNullBody;
    }

    private static Expression? BuildNonNullMappingBody(
        Expression sourceExpr,
        NullabilityState sourceNullability,
        Type targetType,
        MappingPath path)
    {
        var sourceType = sourceExpr.Type;
        if (IsCollectionType(targetType, out var targetElementType)
            && IsCollectionType(sourceType, out var sourceElementType))
        {
            return BuildCollectionMapping(
                sourceExpr,
                sourceElementType!,
                targetElementType!,
                targetType,
                path);
        }

        if (TryConvert(sourceExpr, targetType) is { } e)
            return e;


        return BuildObjectMapping(sourceExpr, sourceNullability, targetType, path);
    }

    private static MemberInitExpression BuildObjectMapping(
    Expression sourceExpr,
    NullabilityState sourceNullability,
    Type targetType,
    MappingPath path)
    {

        var bindings = BuildMemberAssignments(sourceExpr, sourceNullability, targetType, path).ToArray();
        var assignedMembers = bindings.Select(m => m.Member.Name);
        var requredMembers = GetRequiredMemberNames(targetType);
        var needToAssign = requredMembers.Except(assignedMembers, StringComparer.Ordinal).ToArray();


        var constructors = targetType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .OrderByDescending(c => c.GetParameters().Length)
            .Select(c => BuildConstructorCall(c, sourceExpr, sourceNullability, path))
            .Where(p => p != null)
            .Select(p => p.Value)
            .ToArray();

        if (constructors.Length == 0) throw new MappingException($"No suitable constructor found for type '{targetType.FullName}'.");

        string[] unassigned = [];
        foreach (var (ex, args) in constructors)
        {
            unassigned = [.. needToAssign.Except(args, StringComparer.Ordinal)];
            if (ex.Constructor!.GetCustomAttribute<SetsRequiredMembersAttribute>() is { } || unassigned.Length == 0)
            {
                return Expression.MemberInit(ex, bindings);
            }
        }
        throw new MappingException($"Required members of type '{targetType.FullName}' is not mapped: {string.Join(',', unassigned.Select(x => "'" + x + "'"))}.");
    }

    private static IEnumerable<MemberAssignment> BuildMemberAssignments(
    Expression sourceExpr,
    NullabilityState sourceNullability,
    Type targetType,
    MappingPath path)
    {
        List<string> initializedNames = [];

        var settableOrInitOnlyProperties = targetType
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0)
        .Where(p => p.SetMethod is { IsPublic: true });

        foreach (var property in settableOrInitOnlyProperties)
        {
            Expression? mappedExpr = null;

            if (GetSourceMemberAccess(sourceExpr, property.Name!, sourceNullability).FirstOrDefault()
                    is (Expression accessExpr, NullabilityState nullability))
            {
                using var guard = path.Push(property.PropertyType, accessExpr.Type);
                mappedExpr = BuildMappingBody(accessExpr,
                    nullability,
                    targetType: property.PropertyType, SafeNullability(property).WriteState,
                    path);
            }

            if (mappedExpr != null)
            {
                initializedNames.Add(property.Name);
                yield return Expression.Bind(property, mappedExpr);
            }
        }


        var publicFields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !initializedNames.Contains(f.Name))
            .Where(f => !f.IsLiteral && !f.IsInitOnly);

        foreach (var field in publicFields)
        {
            Expression? mappedExpr = null;

            if (GetSourceMemberAccess(sourceExpr, field.Name!, sourceNullability).FirstOrDefault()
                is (Expression accessExpr, NullabilityState nullability))
            {
                using var guard = path.Push(field.FieldType, accessExpr.Type);
                mappedExpr = BuildMappingBody(accessExpr, nullability,
                    field.FieldType, SafeNullability(field).WriteState,
                    path);
            }

            if (mappedExpr != null)
            {
                yield return Expression.Bind(field, mappedExpr);
            }
        }

    }

    private static (NewExpression Expression, IEnumerable<string> Args)? BuildConstructorCall(
    ConstructorInfo constructor,
    Expression sourceExpr,
    NullabilityState sourceNullability,
    MappingPath path)
    {

        List<string> initializedNames = [];
        List<Expression> args = [];

        // 1. Маппим все аргументы конструктора.
        foreach (var parameter in constructor.GetParameters())
        {
            var paramNullability = SafeNullability(parameter).WriteState;
            Expression? mappedExpr = null;
            if (GetSourceMemberAccess(
                    sourceExpr,
                    parameter.Name!,
                    sourceNullability).FirstOrDefault() is (Expression accessExpr, NullabilityState nullability))
            {
                using var guard = path.Push(parameter.ParameterType, accessExpr.Type);
                mappedExpr = BuildMappingBody(accessExpr, nullability,
                    parameter.ParameterType, paramNullability,
                    path: path);
            }

            if (mappedExpr != null)
            {
                args.Add(mappedExpr);
                initializedNames.Add(parameter.Name!);
            }
            else
            {
                if (paramNullability != NullabilityState.Nullable) return null;
                args.Add(Expression.Constant(null, parameter.ParameterType));
            }

        }

        return (args.Count > 0
            ? Expression.New(constructor, args)
            : Expression.New(constructor), initializedNames);

    }

    private static IEnumerable<string> GetRequiredMemberNames(Type targetType)
    {
        var properties = targetType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            .Select(p => p.Name);

        var fields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            .Select(f => f.Name);

        return properties.Concat(fields);
    }

    private static Expression? BuildCollectionMapping(
        Expression sourceExpr,
        Type sourceElementType,
        Type targetElementType,
        Type targetCollectionType,
        MappingPath path)
    {
        var itemParam = Expression.Parameter(sourceElementType, "item");
        if (BuildMappingBody(itemParam, NullabilityState.NotNull,
            targetElementType, NullabilityState.Unknown,
            path) is not { } mappedItem) return null;
        var lambda = Expression.Lambda(mappedItem, itemParam);

        var selectCall = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Select),
            [sourceElementType, targetElementType],
            sourceExpr,
            lambda);

        if (targetCollectionType.IsArray)
        {
            return Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.ToArray),
                [targetElementType],
                selectCall);
        }

        if (targetCollectionType.IsGenericType &&
            targetCollectionType.GetGenericTypeDefinition() == typeof(List<>))
        {
            return Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.ToList),
                [targetElementType],
                selectCall);
        }

        if (targetCollectionType.IsInterface)
        {
            return Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.ToList),
                [targetElementType],
                selectCall);
        }

        var ctor = targetCollectionType.GetConstructor([typeof(IEnumerable<>).MakeGenericType(targetElementType)]);
        if (ctor != null)
        {
            return Expression.New(ctor, selectCall);
        }

        var defaultCtor = targetCollectionType.GetConstructor(Type.EmptyTypes);
        if (defaultCtor != null)
        {
            var newCollection = Expression.New(defaultCtor);

            var addRangeMethod = targetCollectionType.GetMethod(
                "AddRange",
                [typeof(IEnumerable<>).MakeGenericType(targetElementType)]);

            if (addRangeMethod != null)
            {
                return Expression.Call(newCollection, addRangeMethod, selectCall);
            }

            throw new MappingException(
                $"Cannot map collection to type '{targetCollectionType.FullName}' because it has no AddRange method.");
        }

        throw new MappingException($"Cannot map collection to type '{targetCollectionType.FullName}'.");
    }


    private static IEnumerable<(Expression Expression, NullabilityState Nullability)> GetSourceMemberAccess(
        Expression sourceExpr,
        string suffix,
        NullabilityState nullability)
    {
        if (string.IsNullOrEmpty(suffix))
        {
            yield return (sourceExpr, nullability);
            yield break;
        }

        if (suffix.StartsWith('_')) suffix = suffix[1..];

        var sourceType = sourceExpr.Type;
        var candidates = GetSourceMembers(sourceType).ToList();

        var exectMatches = candidates.Where(m => suffix.StartsWith(m.Name, StringComparison.Ordinal)).ToArray();
        var caseInsesitiveMathes = candidates.Except(exectMatches).Where(m => suffix.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var match in exectMatches.Concat(caseInsesitiveMathes))
        {
            var rec = GetSourceMemberAccess(
                Expression.MakeMemberAccess(sourceExpr, match),
                suffix[match.Name.Length..],
                nullability == NullabilityState.NotNull ? GetMemberNullability(match).ReadState : nullability);

            foreach (var (ex, n) in rec)
            {
                yield return (nullability == NullabilityState.NotNull ? ex : WrapNullCoalescingOperator(sourceExpr, ex), n);
            }
        }

        if (sourceType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            is { } enumerable)
        {
            var elementType = enumerable.GetGenericArguments()[0];
            candidates = [.. GetSourceMembers(elementType)];

            foreach (var op in CollectionOperations)
            {
                var aggregateNullability = op is "FirstOrDefault" or "LastOrDefault" ? NullabilityState.Nullable : NullabilityState.NotNull;
                var resultNullability = nullability == NullabilityState.NotNull ? aggregateNullability : nullability;

                if (suffix.StartsWith(op, StringComparison.Ordinal)
                    && GetSourceMemberCollectionAggregates(sourceExpr, op, elementType) is { } x)
                {
                    var rec = GetSourceMemberAccess(x, suffix[op.Length..], resultNullability);

                    foreach (var (ex, n) in rec)
                    {
                        yield return (nullability == NullabilityState.NotNull ? ex : WrapNullCoalescingOperator(sourceExpr, ex), n);
                    }
                    break;
                }
                else
                {
                    if (candidates
                        .Where(m => suffix.StartsWith(m.Name + op, StringComparison.Ordinal))
                        .Select(m => (Expresstion: GetSourceMemberCollectionAggregates(sourceExpr, op, elementType, m), Member: m))
                        .FirstOrDefault(x => x.Expresstion != null) is ({ } x1, { } m))
                    {
                        var rec = GetSourceMemberAccess(x1, suffix[(m.Name + op).Length..], resultNullability);

                        foreach (var (ex, n) in rec)
                        {
                            yield return (nullability == NullabilityState.NotNull ? ex : WrapNullCoalescingOperator(sourceExpr, ex), n);
                        }
                        break;
                    }
                }
            }
        }
    }

    private static Expression? GetSourceMemberCollectionAggregates(Expression source, string op, Type elementType, MemberInfo? member = null)
    {

        if (member is null)
        {
            foreach (var method in GetEnumerableMethods(source, op, elementType, 1))
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 1)
                {
                    return Expression.Call(null, method, source);
                }
            }
        }
        else
        {
            var p = Expression.Parameter(elementType);
            var lambda = Expression.Lambda(Expression.MakeMemberAccess(p, member), p);
            foreach (var method in GetEnumerableMethods(source, op, elementType, 2))
            {
                var parameters = method.GetParameters();
                if (parameters[1].ParameterType == lambda.Type)
                {
                    return Expression.Call(null, method, source, lambda);
                }
            }

            var memberType = GetMemberType(member);
            var selectCall = Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Select),
                [elementType, memberType],
                source,
                lambda);
            foreach (var method in GetEnumerableMethods(selectCall, op, memberType, 1))
            {
                var parameters = method.GetParameters();
                if (parameters.Length == 1)
                {
                    return Expression.Call(null, method, selectCall);
                }
            }
        }
        return null;
    }

    private static IEnumerable<MethodInfo> GetEnumerableMethods(Expression source, string op, Type elementType, int parameterCount) =>
        typeof(Enumerable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == op)
            .Where(m => !m.IsGenericMethod || m.GetGenericArguments().Length == 1)
            .Select(m => m.IsGenericMethod ? m.MakeGenericMethod(elementType) : m)
            .Where(m =>
            {
                var ps = m.GetParameters();
                return ps.Length == parameterCount && ps is [{ } p] && p.ParameterType.IsAssignableFrom(source.Type);
            });

    private static ConditionalExpression WrapNullCoalescingOperator(Expression source, Expression target)
    {
        var targetType = target.Type;
        if (targetType.IsValueType && Nullable.GetUnderlyingType(targetType) == null)
        {
            targetType = typeof(Nullable<>).MakeGenericType(targetType);
        }
        return Expression.Condition(
            Expression.Equal(source, Expression.Constant(null, source.Type)),
            Expression.Default(targetType),
            target.Type == targetType ? target : Expression.Convert(target, targetType),
            targetType);
    }

    private static bool CanBeNull(Type type)
    {
        return !type.IsValueType || Nullable.GetUnderlyingType(type) is not null;
    }

    private static IEnumerable<MemberInfo> GetSourceMembers(Type sourceType)
    {
        var properties = sourceType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p =>
                p.CanRead &&
                p.GetGetMethod() is not null &&
                p.GetIndexParameters().Length == 0)
            .Cast<MemberInfo>();

        var fields = sourceType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Cast<MemberInfo>();

        return properties.Concat(fields);
    }


    private static UnaryExpression? TryConvert(Expression expr, Type targetType)
    {
        var sourceType = expr.Type;
        var key = (targetType, sourceType);
        if (tryConvertCache.TryGetValue(key, out var canCovert) && !canCovert) return null;

        try
        {
            return Expression.Convert(expr, targetType);
        }
        catch (InvalidOperationException)
        {
            tryConvertCache[key] = false;
            return null;
        }
    }

    private static bool IsCollectionType(Type type, out Type? elementType)
    {
        elementType = null;

        if (type == typeof(string))
            return false;

        if (type.IsArray)
        {
            elementType = type.GetElementType();
            return true;
        }

        if (type.IsGenericType)
        {
            var genType = type.GetGenericTypeDefinition();

            if (genType == typeof(IEnumerable<>) ||
                genType == typeof(ICollection<>) ||
                genType == typeof(IList<>) ||
                genType == typeof(List<>))
            {
                elementType = type.GetGenericArguments()[0];
                return true;
            }
        }

        var interfaces = type.GetInterfaces();

        foreach (var iface in interfaces)
        {
            if (iface.IsGenericType && iface.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            {
                elementType = iface.GetGenericArguments()[0];
                return true;
            }
        }

        return false;
    }

    private static bool IsInitOnly(PropertyInfo property)
    {
        var setMethod = property.SetMethod;
        if (setMethod == null)
            return true;

        var parameters = setMethod.GetParameters();
        if (parameters.Length == 0)
            return false;

        var lastParam = parameters[^1];
        var modReqs = lastParam.GetRequiredCustomModifiers();

        return modReqs.Any(t =>
            t.Name == "IsExternalInit" &&
            t.Namespace == "System.Runtime.CompilerServices");
    }

    private static Type GetMemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => throw new InvalidOperationException("Unsupported member type")
    };

    private static NullabilityInfo GetMemberNullability(MemberInfo member) => member switch
    {
        PropertyInfo p => SafeNullability(p),
        FieldInfo f => SafeNullability(f),
        _ => throw new InvalidOperationException("Unsupported member type")
    };



    private static readonly NullabilityInfoContext NullabilityContext = new();
    private static readonly Lock NullabilityLock = new();
    private static readonly ConcurrentDictionary<(Type Target, Type Source), bool> tryConvertCache = [];

    private static NullabilityInfo SafeNullability(PropertyInfo info)
    {
        lock (NullabilityLock)
        {
            return NullabilityContext.Create(info);
        }
    }
    private static NullabilityInfo SafeNullability(FieldInfo info)
    {
        lock (NullabilityLock)
        {
            return NullabilityContext.Create(info);
        }
    }
    private static NullabilityInfo SafeNullability(ParameterInfo info)
    {
        lock (NullabilityLock)
        {
            return NullabilityContext.Create(info);
        }
    }

}