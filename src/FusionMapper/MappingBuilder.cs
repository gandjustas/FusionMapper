using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FusionMapper;

static class MappingBuilder
{
    public static LambdaExpression BuildCreationLambda(Type sourceType, Type targetType)
    {
        var sourceParam = Expression.Parameter(sourceType, "source");

        MappingPath path = new();
        using var guard = path.Push(targetType, sourceType);


        if (BuildCreationBody(
            sourceParam, sourceNullability: NullabilityState.Unknown,
            targetType, targetNullability: NullabilityState.Unknown,
            path) is { } body)
        {
            return Expression.Lambda(body, sourceParam);
        }
        else
        {
            throw new MappingException($"Can't map {sourceParam.Type} to {targetType}.");
        }
    }
    public static LambdaExpression BuildAssignmentLambda(Type sourceType, Type targetType)
    {
        var sourceParam = Expression.Parameter(sourceType, "source");
        var targetParam = Expression.Parameter(targetType, "target");

        MappingPath path = new();
        using var rootGuard = path.Push(targetType, sourceType);

        var body = BuildNonNullAssignmentBody(
            sourceParam,
            targetParam,
            path);

        if (body.Expressions.Count == 0)
        {
            throw new MappingException(
                $"Nothing were mapped from '{sourceType.FullName}' to '{targetType.FullName}'.");
        }

        // Явно создаём Action<TSource, TTarget>, чтобы избежать проблем с кастом LambdaExpression.
        var delegateType = typeof(Action<,>).MakeGenericType(sourceType, targetType);
        return Expression.Lambda(delegateType, body, sourceParam, targetParam);

    }

    public static IEnumerable<Expression> BuildAssignmentBody(
        Expression source,
        NullabilityState sourceNullability,
        Expression target,
        NullabilityState targetNullability,
        MappingPath path)
    {
        var sourceType = source.Type;
        var targetType = target.Type;

        var returnLabel = Expression.Label(targetType, "MapToExistingReturn");

        // source == null -> вернуть target как есть
        if (sourceNullability != NullabilityState.NotNull || CanBeNull(sourceType))
        {
            yield return Expression.IfThen(
                    Expression.Equal(source, Expression.Constant(null, sourceType)),
                    Expression.Return(returnLabel));
        }

        // target == null -> создать новый через creation-маппинг
        if (targetNullability != NullabilityState.NotNull || CanBeNull(targetType))
        {
            var createNew = BuildNonNullMappingBody(source, NullabilityState.NotNull, targetType, path);

            yield return Expression.IfThen(
                    Expression.Equal(target, Expression.Constant(null, targetType)),
                    Expression.Return(returnLabel));
        }

        var body = BuildNonNullAssignmentBody(source, target, path);
        if (body.Expressions.Count == 0)
        {
            throw new MappingException(
                $"Nothing were mapped from '{sourceType.FullName}' to '{targetType.FullName}'.");
        }
        yield return Expression.Label(returnLabel);
    }

    public static BlockExpression BuildNonNullAssignmentBody(
        Expression source,
        Expression target,
        MappingPath path)
    {
        var sourceType = source.Type;
        var targetType = target.Type;

        // Корневая пара нужна, чтобы recursion detection видел полный путь.

        var writableProperties = targetType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Where(p => !IsInitOnly(p))
            .Cast<MemberInfo>();

        var writableFields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !f.IsInitOnly && !f.IsLiteral)
            .Cast<MemberInfo>();

        var assignExpressions = writableProperties
            .Concat(writableFields)
            .Select(member =>
            {
                var memberType = GetMemberType(member);
                var targetNullability = GetMemberNullability(member).WriteState;

                foreach (var (accessExpr, sourceNullability)
                    in GetSourceMemberAccess(source, NullabilityState.NotNull, member.Name))
                {
                    using var guard = path.Push(memberType, accessExpr.Type);

                    if (BuildCreationBody(
                            accessExpr,
                            sourceNullability,
                            memberType,
                            targetNullability,
                            path) is not { } mappedExpr)
                    {
                        continue;
                    }

                    // BuildMappingBody может вернуть expression с типом, который assignable target'у,
                    // но не совпадает с ним. Для стабильных expression tree лучше приводить явно.
                    if (mappedExpr.Type != memberType)
                    {
                        if (!memberType.IsAssignableFrom(mappedExpr.Type))
                            continue;

                        mappedExpr = Expression.Convert(mappedExpr, memberType);
                    }

                    return (Expression)Expression.Assign(
                        Expression.MakeMemberAccess(target, member),
                        mappedExpr);
                }

                return null;
            });

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

        var fillCollectionExpressions = readOnlyProperties
            .Concat(readOnlyFields)
            .Select(member =>
            {
                var targetMemberType = GetMemberType(member);

                if (!IsCollectionType(targetMemberType, out _))
                    return null;

                foreach (var (accessExpr, nullability)
                    in GetSourceMemberAccess(source, NullabilityState.NotNull, member.Name))
                {
                    // Если кандидат не является коллекцией, пробуем следующего кандидата.
                    if (!IsCollectionType(accessExpr.Type, out _))
                        continue;

                    using var guard = path.Push(targetMemberType, accessExpr.Type);

                    if (BuildReadOnlyCollectionMutation(
                            accessExpr,
                            nullability,
                            target,
                            member,
                            targetMemberType,
                            path) is { } mutation)
                    {
                        return (Expression)mutation;
                    }
                }

                return null;
            });

        var bodyExpressions = assignExpressions
            .Concat(fillCollectionExpressions)
            .OfType<Expression>();

        return Expression.Block(typeof(void), bodyExpressions);
    }


    private static BlockExpression? BuildReadOnlyCollectionMutation(
        Expression sourceAccess,
        NullabilityState sourceNullability,
        Expression target,
        MemberInfo member,
        Type targetCollectionType,
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
        var addMethod = FindCollectionAddMethod(targetCollectionType, targetElementType);
        var addRangeMethod = FindCollectionAddRangeMethod(targetCollectionType, targetElementType);


        if (clearMethod is null || (addRangeMethod is null && addMethod is null))
        {
            throw new MappingException(
                $"Read-only collection member '{member.Name}' cannot be mutated. " +
                $"Target member type: '{targetCollectionType.FullName}'. " +
                "The collection must expose a public Clear method and either AddRange or Add.");
        }

        var mappedEnumerableType = typeof(IEnumerable<>).MakeGenericType(targetElementType);

        var memberAccess = Expression.MakeMemberAccess(target, member);

        var existingVar = Expression.Variable(targetCollectionType, $"{member.Name}_existing");
        var sourceVar = Expression.Variable(sourceAccess.Type, $"{member.Name}_source");
        var mappedListVar = Expression.Variable(mappedEnumerableType, $"{member.Name}_mapped");

        List<Expression> body =
        [
            Expression.Assign(existingVar, memberAccess)
        ];

        if (CanBeNull(targetCollectionType))
        {
            body.Add(Expression.IfThen(
                Expression.Equal(existingVar, Expression.Constant(null, targetCollectionType)),
                Expression.Throw(
                    Expression.New(
                        typeof(InvalidOperationException).GetConstructor([typeof(string)])!,
                        Expression.Constant(
                            $"Read-only collection member '{member.Name}' is null and cannot be mutated. " +
                            $"Target member type: '{targetCollectionType.FullName}'.")),
                    typeof(void))));
        }

        body.Add(Expression.Assign(sourceVar, sourceAccess));

        var itemParam = Expression.Parameter(sourceElementType, "item");

        Expression mappedItem;

        // Пушим пару элементов, чтобы корректно детектить рекурсию на уровне element mapping.
        using (path.Push(targetElementType, sourceElementType))
        {
            if (BuildCreationBody(
                    itemParam,
                    sourceNullability: NullabilityState.NotNull,
                    targetType: targetElementType,
                    targetNullability: NullabilityState.NotNull,
                    path: path) is not { } itemBody)
            {
                return null;
            }

            mappedItem = itemBody;
        }

        // Select<TSource, TResult> требует Func<TSource, TResult>.
        // Если body имеет assignable, но другой тип, приводим к targetElementType.
        if (mappedItem.Type != targetElementType)
        {
            if (!targetElementType.IsAssignableFrom(mappedItem.Type))
                return null;

            mappedItem = Expression.Convert(mappedItem, targetElementType);
        }

        var lambdaType = typeof(Func<,>).MakeGenericType(sourceElementType, targetElementType);
        var lambda = Expression.Lambda(lambdaType, mappedItem, itemParam);

        var selectCall = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Select),
            [sourceElementType, targetElementType],
            sourceVar,
            lambda);



        Expression mappedListValue;

        if (CanBeNull(sourceVar.Type) &&
            sourceNullability != NullabilityState.NotNull)
        {
            mappedListValue = Expression.Condition(
                Expression.Equal(sourceVar, Expression.Constant(null, sourceVar.Type)),
                Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Empty),
                [targetElementType]),
                selectCall,
                mappedEnumerableType);
        }
        else
        {
            mappedListValue = selectCall;
        }

        body.Add(Expression.Assign(mappedListVar, mappedListValue));
        body.Add(Expression.Call(existingVar, clearMethod));

        if (addRangeMethod is not null)
        {
            body.Add(Expression.Call(existingVar, addRangeMethod, mappedListVar));
        }
        else
        {
            body.Add(BuildAddFromEnumerableLoop(
                existingVar,
                addMethod!,
                mappedListVar,
                targetElementType));
        }

        return Expression.Block(
            typeof(void),
            [existingVar, sourceVar, mappedListVar],
            body);
    }

    private static BlockExpression BuildAddFromEnumerableLoop(
    Expression collection,
    MethodInfo addMethod,
    Expression source,
    Type elementType)
    {
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);
        var enumeratorType = typeof(IEnumerator<>).MakeGenericType(elementType);

        var enumeratorVar = Expression.Variable(enumeratorType, "enumerator");

        var getEnumeratorMethod = enumerableType.GetMethod(nameof(IEnumerable<>.GetEnumerator))
            ?? typeof(IEnumerable<>).MakeGenericType(elementType).GetMethod(nameof(IEnumerable<>.GetEnumerator));
        var moveNextMethod = typeof(System.Collections.IEnumerator).GetMethod(nameof(IEnumerator<>.MoveNext));
        var currentProperty = enumeratorType.GetProperty(nameof(IEnumerator<>.Current));
        var disposeMethod = typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose));
        var breakLabel = Expression.Label("LoopBreak");


        var loop = Expression.Loop(
            Expression.IfThenElse(
                Expression.Call(enumeratorVar, moveNextMethod!),
                Expression.Call(collection, addMethod, Expression.Property(enumeratorVar, currentProperty!)),
                Expression.Break(breakLabel)),
            breakLabel);

        var tryFinally = Expression.TryFinally(
            loop,
            Expression.Call(enumeratorVar, disposeMethod!));

        return Expression.Block(
            typeof(void),
            [enumeratorVar],
            Expression.Assign(enumeratorVar, Expression.Call(source, getEnumeratorMethod!)),
            tryFinally);
    }

    private static MethodInfo? FindCollectionClearMethod(Type type) =>
    type.GetMethod(
        nameof(ICollection<>.Clear),
        BindingFlags.Public | BindingFlags.Instance,
        null,
        Type.EmptyTypes,
        null);


    private static MethodInfo? FindCollectionAddRangeMethod(Type type, Type elementType)
    {
        var enumerableType = typeof(IEnumerable<>).MakeGenericType(elementType);

        var method = type.GetMethod(
            nameof(List<>.AddRange),
            BindingFlags.Public | BindingFlags.Instance,
            null,
            [enumerableType],
            null);

        if (method is not null)
            return method;

        return type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == nameof(List<>.AddRange) &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.IsAssignableFrom(enumerableType));
    }

    private static MethodInfo? FindCollectionAddMethod(Type type, Type elementType)
    {
        var method = type.GetMethod(
            nameof(ICollection<>.Add),
            BindingFlags.Public | BindingFlags.Instance,
            null,
            [elementType],
            null);

        if (method is not null)
            return method;

        return type
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(m =>
                m.Name == nameof(ICollection<>.Add) &&
                m.GetParameters().Length == 1 &&
                m.GetParameters()[0].ParameterType.IsAssignableFrom(elementType));
    }

    private static Expression? BuildCreationBody(
        Expression sourceExpr,
        NullabilityState sourceNullability,
        Type targetType,
        NullabilityState targetNullability,
        MappingPath path)
    {
        if (targetType.IsPointer || targetType.IsFunctionPointer)
            return null;

        var sourceType = sourceExpr.Type;

        if (targetType.IsAssignableFrom(sourceType))
        {
            return targetType == sourceType
                ? sourceExpr
                : Expression.Convert(sourceExpr, targetType);
        }

        var sourceUnderlyingType = Nullable.GetUnderlyingType(sourceType);
        var targetUnderlyingType = Nullable.GetUnderlyingType(targetType);

        var sourceCanBeNull =
            CanBeNull(sourceType) &&
            sourceNullability != NullabilityState.NotNull;

        var targetAcceptsNull =
            CanBeNull(targetType) &&
            targetNullability != NullabilityState.NotNull;

        var nonNullSource = sourceUnderlyingType is null
            ? sourceExpr
            : Expression.Property(sourceExpr, sourceType.GetProperty("Value")!);

        var nonNullTarget = targetUnderlyingType ?? targetType;

        var nonNullBody = BuildNonNullMappingBody(
            nonNullSource,
            NullabilityState.NotNull,
            nonNullTarget,
            path);

        if (nonNullBody is null)
            return null;

        if (nonNullBody.Type != targetType)
        {
            if (targetType.IsAssignableFrom(nonNullBody.Type))
            {
                nonNullBody = Expression.Convert(nonNullBody, targetType);
            }
            else if (targetUnderlyingType is not null &&
                     targetUnderlyingType.IsAssignableFrom(nonNullBody.Type))
            {
                nonNullBody = Expression.Convert(nonNullBody, targetType);
            }
            else
            {
                return null;
            }
        }

        if (sourceCanBeNull)
        {
            Expression nullBranch = targetAcceptsNull || targetUnderlyingType is not null
                ? Expression.Default(targetType)
                : Expression.Throw(
                    Expression.New(
                        typeof(InvalidOperationException).GetConstructor([typeof(string)])!,
                        Expression.Constant(
                            $"Cannot map null source to non-nullable target type '{targetType.FullName}'.")),
                    targetType);

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

        if (IsCollectionType(targetType, out var targetElementType) &&
            IsCollectionType(sourceType, out var sourceElementType))
        {
            return BuildCollectionMapping(
                sourceExpr,
                sourceElementType!,
                targetElementType!,
                targetType,
                path);
        }

        if (targetType.IsAssignableFrom(sourceType))
        {
            return targetType == sourceType
                ? sourceExpr
                : Expression.Convert(sourceExpr, targetType);
        }

        // enum -> string
        if (sourceType.IsEnum && targetType == typeof(string))
        {
            return Expression.Call(
                Expression.Convert(sourceExpr, typeof(object)),
                ObjectToStringMethod);
        }

        // string -> enum
        if (sourceType == typeof(string) && targetType.IsEnum)
        {
            return BuildStringToEnum(sourceExpr, targetType);
        }

        if (TryConvert(sourceExpr, targetType) is { } e)
            return e;

        if (targetType.IsPrimitive)
            return null;

        return BuildObjectMapping(sourceExpr, sourceNullability, targetType, path);
    }

    private static MemberInitExpression BuildObjectMapping(
    Expression sourceExpr,
    NullabilityState sourceNullability,
    Type targetType,
    MappingPath path)
    {

        var bindings = BuildMemberAssignments(sourceExpr, sourceNullability, targetType, path).ToArray();
        var assignedMembers = bindings.Select(m => m.Member);
        var requiredMembers = GetRequiredMemberNames(targetType);
        var needToAssign = requiredMembers.Except(assignedMembers).ToArray();


        var constructors = targetType
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Select(c => BuildConstructorCall(c, sourceExpr, sourceNullability, path))
            .OfType<NewExpression>()
            .OrderByDescending(p => p.Arguments.Count)
            .ToArray();

        if (constructors.Length == 0) throw new MappingException($"No suitable constructor found for type '{targetType.FullName}'.");

        string[] unassigned = [];
        foreach (var ex in constructors)
        {
            var args = ex.Constructor!
                    .GetParameters()
                    .Select(p => (p.Name!, p.ParameterType));
            unassigned = [.. needToAssign.ExceptBy(args, m => (m.Name, GetMemberType(m)), MemberComparer.Instance).Select(p => p.Name)];
            if (ex.Constructor!.GetCustomAttribute<SetsRequiredMembersAttribute>() is { } || unassigned.Length == 0)
            {
                return Expression.MemberInit(ex, bindings.ExceptBy(args, m => (m.Member.Name, GetMemberType(m.Member)), MemberComparer.Instance));
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
            foreach (var (accessExpr, nullability)
                in GetSourceMemberAccess(sourceExpr, sourceNullability, property.Name!))
            {
                using var guard = path.Push(property.PropertyType, accessExpr.Type);
                if (BuildCreationBody(accessExpr,
                    nullability,
                    targetType: property.PropertyType, SafeNullability(property).WriteState,
                    path) is { } mappedExpr)
                {
                    initializedNames.Add(property.Name);
                    yield return Expression.Bind(property, mappedExpr);
                    break;
                }

            }
        }


        var publicFields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !initializedNames.Contains(f.Name))
            .Where(f => !f.IsLiteral && !f.IsInitOnly);

        foreach (var field in publicFields)
        {
            foreach (var (accessExpr, nullability)
                in GetSourceMemberAccess(sourceExpr, sourceNullability, field.Name!))
            {
                using var guard = path.Push(field.FieldType, accessExpr.Type);
                if (BuildCreationBody(accessExpr, nullability,
                    field.FieldType, SafeNullability(field).WriteState,
                    path) is { } mappedExpr)
                {
                    yield return Expression.Bind(field, mappedExpr);
                    break;
                }
            }
        }
    }

    private static NewExpression? BuildConstructorCall(
    ConstructorInfo constructor,
    Expression sourceExpr,
    NullabilityState sourceNullability,
    MappingPath path)
    {

        HashSet<ParameterInfo> initialized = [];
        List<Expression> args = [];

        // 1. Маппим все аргументы конструктора.
        foreach (var parameter in constructor.GetParameters())
        {
            var paramNullability = SafeNullability(parameter).WriteState;
            foreach (var (accessExpr, nullability)
                    in GetSourceMemberAccess(sourceExpr, sourceNullability, parameter.Name!))
            {
                using var guard = path.Push(parameter.ParameterType, accessExpr.Type);
                if (BuildCreationBody(accessExpr, nullability,
                    parameter.ParameterType, paramNullability,
                    path: path) is { } mappedExpr)
                {
                    args.Add(mappedExpr);
                    initialized.Add(parameter);
                    break;
                }
            }

            if (!initialized.Contains(parameter))
            {
                var canBeNull =
                    paramNullability == NullabilityState.Nullable ||
                    paramNullability == NullabilityState.Unknown && CanBeNull(parameter.ParameterType);

                if (!canBeNull)
                    return null;

                args.Add(Expression.Constant(null, parameter.ParameterType));
                initialized.Add(parameter);
            }
        }

        return args.Count > 0
            ? Expression.New(constructor, args)
            : Expression.New(constructor);

    }

    private static IEnumerable<MemberInfo> GetRequiredMemberNames(Type targetType)
    {
        var properties = targetType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            .OfType<MemberInfo>();

        var fields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            .OfType<MemberInfo>();

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
        if (BuildCreationBody(itemParam, NullabilityState.NotNull,
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
        if (defaultCtor is not null)
        {
            var collectionVar = Expression.Variable(targetCollectionType, "collection");

            List<Expression> body = [Expression.Assign(collectionVar, Expression.New(defaultCtor))];

            var addRangeMethod = FindCollectionAddRangeMethod(targetCollectionType, targetElementType);
            var addMethod = FindCollectionAddMethod(targetCollectionType, targetElementType);

            if (addRangeMethod is not null)
            {
                body.Add(Expression.Call(collectionVar, addRangeMethod, selectCall));
            }
            else if (addMethod is not null)
            {
                body.Add(BuildAddFromEnumerableLoop(
                    collectionVar,
                    addMethod,
                    selectCall,
                    targetElementType));
            }
            else
            {
                throw new MappingException(
                    $"Cannot map collection to type '{targetCollectionType.FullName}'. " +
                    "The collection must expose a public AddRange or Add method.");
            }

            body.Add(collectionVar);

            return Expression.Block(
                targetCollectionType,
                [collectionVar],
                body);
        }

        throw new MappingException($"Cannot map collection to type '{targetCollectionType.FullName}'.");
    }


    private static IEnumerable<(Expression Expression, NullabilityState Nullability)> GetSourceMemberAccess(
        Expression sourceExpr,
        NullabilityState nullability,
        string suffix)
    {
        if (string.IsNullOrEmpty(suffix))
        {
            yield return (sourceExpr, nullability);
            yield break;
        }

        if (suffix.StartsWith('_')) suffix = suffix[1..];

        var sourceType = sourceExpr.Type;
        var candidates = GetSourceMembers(sourceType)
            .OrderByDescending(m => m.Name.Length)
            .ToList();

        var exactMatches = candidates.Where(m => suffix.StartsWith(m.Name, StringComparison.Ordinal)).ToArray();
        var caseInsensitiveMatches = candidates.Except(exactMatches).Where(m => suffix.StartsWith(m.Name, StringComparison.OrdinalIgnoreCase));

        foreach (var match in exactMatches.Concat(caseInsensitiveMatches))
        {
            var rec = GetSourceMemberAccess(
                Expression.MakeMemberAccess(sourceExpr, match),
                nullability == NullabilityState.NotNull ? GetMemberNullability(match).ReadState : nullability,
                suffix[match.Name.Length..]);

            foreach (var (ex, n) in rec)
            {
                yield return (WrapNullCheck(sourceExpr, ex, n), n);
            }
        }

        if (IsCollectionType(sourceType, out var elementType))
        {
            foreach (var p in GetSourceMemberCollection(sourceExpr, nullability, suffix, elementType))
            {
                yield return p;
            }

        }
    }

    private static IEnumerable<(Expression Expression, NullabilityState Nullability)> GetSourceMemberCollection(
        Expression sourceExpr,
        NullabilityState nullability,
        string suffix,
        Type elementType)
    {
        var candidates = GetSourceMembers(elementType).ToArray();

        foreach (var op in CollectionOperations)
        {
            var aggregateNullability = op is "FirstOrDefault" or "LastOrDefault" ? NullabilityState.Nullable : NullabilityState.NotNull;
            var resultNullability = nullability == NullabilityState.NotNull ? aggregateNullability : nullability;

            if (suffix.StartsWith(op, StringComparison.Ordinal)
                && GetSourceMemberCollectionAggregates(sourceExpr, op, elementType) is { } x)
            {
                var rec = GetSourceMemberAccess(x, resultNullability, suffix[op.Length..]);

                foreach (var (ex, n) in rec)
                {
                    yield return (WrapNullCheck(sourceExpr, ex, n), n);
                }
            }
            else
            {
                if (candidates
                    .Where(m => suffix.StartsWith(m.Name + op, StringComparison.Ordinal))
                    .Select(m => (E: GetSourceMemberCollectionAggregates(sourceExpr, op, elementType, m), M: m))
                    .FirstOrDefault(x => x.E != null) is ({ } x1, { } m))
                {
                    var rec = GetSourceMemberAccess(x1, resultNullability, suffix[(m.Name + op).Length..]);

                    foreach (var (ex, n) in rec)
                    {
                        yield return (WrapNullCheck(sourceExpr, ex, n), n);
                    }
                }
            }
        }

    }

    private static MethodCallExpression? GetSourceMemberCollectionAggregates(Expression source, string op, Type elementType)
    {
        foreach (var method in GetEnumerableMethods(source, op, elementType, 1))
        {
            var parameters = method.GetParameters();
            if (parameters.Length == 1)
            {
                return Expression.Call(null, method, source);
            }
        }
        return null;
    }

    private static MethodCallExpression? GetSourceMemberCollectionAggregates(Expression source, string op, Type elementType, MemberInfo member)
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
                return ps.Length == parameterCount
                && ps is [{ } p, ..]
                && p.ParameterType.IsAssignableFrom(source.Type);
            });

    private static Expression WrapNullCheck(
        Expression source,
        Expression target,
        NullabilityState nullability)
    {
        if (nullability == NullabilityState.NotNull || !CanBeNull(source.Type))
        {
            return target;
        }

        var resultType = target.Type;

        if (resultType.IsValueType && Nullable.GetUnderlyingType(resultType) is null)
        {
            resultType = typeof(Nullable<>).MakeGenericType(resultType);
        }

        return Expression.Condition(
            Expression.Equal(source, Expression.Constant(null, source.Type)),
            Expression.Default(resultType),
            target.Type == resultType ? target : Expression.Convert(target, resultType),
            resultType);
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
        if (TryConvertCache.TryGetValue(key, out var canConvert) && !canConvert) return null;

        try
        {
            return Expression.Convert(expr, targetType);
        }
        catch (InvalidOperationException)
        {
            TryConvertCache[key] = false;
            return null;
        }
    }

    private static UnaryExpression BuildStringToEnum(Expression source, Type enumType)
    {
        var parsed = Expression.Call(
            EnumParseMethod,
            Expression.Constant(enumType, typeof(Type)),
            source);

        return Expression.Convert(parsed, enumType);
    }

    private static bool IsCollectionType(Type type, [NotNullWhen(true)] out Type? elementType)
    {
        elementType = null;

        if (type == typeof(string))
            return false;

        if (type.IsArray)
        {
            elementType = type.GetElementType()!;
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
        if (setMethod == null) return true;
        return setMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit));
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

    private static readonly string[] CollectionOperations = [
    nameof(Enumerable.FirstOrDefault),
    nameof(Enumerable.LastOrDefault),
    nameof(Enumerable.First),
    nameof(Enumerable.Last),
    nameof(Enumerable.Count),
    nameof(Enumerable.Average),
    nameof(Enumerable.Sum),
    nameof(Enumerable.Max),
    nameof(Enumerable.Min),
    nameof(Enumerable.Any),
    nameof(Enumerable.All)
    ];

    private static readonly MethodInfo ObjectToStringMethod =
        typeof(object).GetMethod(nameof(object.ToString), Type.EmptyTypes)!;

    private static readonly MethodInfo EnumParseMethod =
        typeof(Enum).GetMethod(nameof(Enum.Parse), [typeof(Type), typeof(string)])!;


    private static readonly NullabilityInfoContext NullabilityContext = new();
    private static readonly ConcurrentDictionary<(Type Target, Type Source), bool> TryConvertCache = [];

    private static NullabilityInfo SafeNullability(PropertyInfo info)
    {
        lock (NullabilityContext)
        {
            return NullabilityContext.Create(info);
        }
    }
    private static NullabilityInfo SafeNullability(FieldInfo info)
    {
        lock (NullabilityContext)
        {
            return NullabilityContext.Create(info);
        }
    }
    private static NullabilityInfo SafeNullability(ParameterInfo info)
    {
        lock (NullabilityContext)
        {
            return NullabilityContext.Create(info);
        }
    }

    private sealed class MemberComparer : IEqualityComparer<(string, Type)>
    {
        public static readonly MemberComparer Instance = new();

        public bool Equals((string, Type) x, (string, Type) y) =>
            x.Item2 == y.Item2 && Normalize(x.Item1) == Normalize(y.Item1);

        public int GetHashCode([DisallowNull] (string, Type) obj) =>
            HashCode.Combine(obj.Item2, Normalize(obj.Item1));

        private static string Normalize(string value) =>
            value.TrimStart('_').ToLowerInvariant() ?? string.Empty;
    }
}