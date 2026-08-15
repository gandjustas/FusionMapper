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

    public static LambdaExpression BuildAssignmentFuncLambda(Type sourceType, Type targetType)
    {
        var sourceParam = Expression.Parameter(sourceType, "source");
        var targetParam = Expression.Parameter(targetType, "target");

        MappingPath path = new();
        using var rootGuard = path.Push(targetType, sourceType);

        if (TryBuildMapExpression(
                sourceParam,
                NullabilityState.Unknown,
                targetParam,
                NullabilityState.Unknown,
                path,
                assignTarget: true,
                requireExistingMapping: true) is not { } mapped)
        {
            throw new MappingException(
                $"Can't map '{sourceType.FullName}' to '{targetType.FullName}'.");
        }

        var delegateType = typeof(Func<,,>).MakeGenericType(sourceType, targetType, targetType);

        return Expression.Lambda(
            delegateType,
            mapped,
            sourceParam,
            targetParam);
    }

    private static BlockExpression? TryBuildMapExpression(
        Expression source,
        NullabilityState sourceNullability,
        Expression target,
        NullabilityState targetNullability,
        MappingPath path,
        bool assignTarget,
        bool requireExistingMapping)
    {
        var sourceVar = Expression.Variable(source.Type, "sourceValue");
        var targetVar = Expression.Variable(target.Type, "targetValue");
        var resultVar = Expression.Variable(target.Type, "result");

        var mappedNonNullSource = TryBuildMapFromNonNullSource(
            sourceVar,
            targetVar,
            targetNullability,
            path,
            assignTarget,
            requireExistingMapping);

        if (mappedNonNullSource is null)
        {
            return null;
        }

        List<Expression> body =
        [
            Expression.Assign(sourceVar, source),
        Expression.Assign(targetVar, target),
        Expression.Assign(resultVar, targetVar)
        ];

        var needSourceNullCheck =
            sourceNullability != NullabilityState.NotNull ||
            CanBeNull(source.Type);

        if (needSourceNullCheck)
        {
            body.Add(Expression.IfThen(
                Expression.NotEqual(sourceVar, Expression.Constant(null, source.Type)),
                Expression.Assign(resultVar, mappedNonNullSource)));
        }
        else
        {
            body.Add(Expression.Assign(resultVar, mappedNonNullSource));
        }

        body.Add(resultVar);

        return Expression.Block(
            target.Type,
            [sourceVar, targetVar, resultVar],
            body);
    }


    private static Expression? TryBuildMapFromNonNullSource(
        Expression source,
        Expression target,
        NullabilityState targetNullability,
        MappingPath path,
        bool assignTarget,
        bool requireExistingMapping)
    {
        var targetType = target.Type;

        var existingMapping = TryBuildExistingNonNullMapping(
            source,
            NullabilityState.NotNull,
            target,
            targetNullability,
            path: path,
            assignTarget: assignTarget);

        Expression? createNew = assignTarget
            ? TryBuildCreationExpression(
                source,
                targetType,
                targetNullability,
                path)
            : null;

        if (existingMapping is null && createNew is null)
        {
            return requireExistingMapping
                ? ThrowNothingMapped(source.Type, targetType)
                : null;
        }

        if (CanBeNull(targetType) && targetNullability != NullabilityState.NotNull)
        {
            Expression nullBranch = createNew is not null
                ? EnsureType(createNew, targetType)!
                : requireExistingMapping
                    ? ThrowNothingMapped(source.Type, targetType)
                    : Expression.Default(targetType);

            Expression nonNullBranch = existingMapping is not null
                ? EnsureType(existingMapping, targetType)!
                : requireExistingMapping
                    ? ThrowNothingMapped(source.Type, targetType)
                    : target;

            return Expression.Condition(
                Expression.Equal(target, Expression.Constant(null, targetType)),
                nullBranch,
                nonNullBranch,
                targetType);
        }

        if (existingMapping is not null)
        {
            return EnsureType(existingMapping, targetType);
        }

        return requireExistingMapping
            ? ThrowNothingMapped(source.Type, targetType)
            : null;
    }


    private static BlockExpression? TryBuildMutationExpression(
        Expression source,
        NullabilityState sourceNullability,
        Expression target,
        NullabilityState targetNullability,
        MappingPath path)
    {
        var sourceVar = Expression.Variable(source.Type, "sourceValue");
        var targetVar = Expression.Variable(target.Type, "targetValue");

        var mutation = TryBuildExistingNonNullMapping(
            sourceVar,
            sourceNullability,
            targetVar,
            targetNullability,
            path,
            assignTarget: false);

        if (mutation is null)
        {
            return null;
        }

        var voidMutation = ToVoid(mutation);

        List<Expression> body =
        [
            Expression.Assign(sourceVar, source),
        Expression.Assign(targetVar, target)
        ];

        var needSourceNullCheck = CanBeNull(source.Type);
        var needTargetNullCheck = CanBeNull(target.Type);

        if (needSourceNullCheck && needTargetNullCheck)
        {
            body.Add(Expression.IfThen(
                Expression.AndAlso(
                    Expression.NotEqual(sourceVar, Expression.Constant(null, source.Type)),
                    Expression.NotEqual(targetVar, Expression.Constant(null, target.Type))),
                voidMutation));
        }
        else if (needSourceNullCheck)
        {
            body.Add(Expression.IfThen(
                Expression.NotEqual(sourceVar, Expression.Constant(null, source.Type)),
                voidMutation));
        }
        else if (needTargetNullCheck)
        {
            body.Add(Expression.IfThen(
                Expression.NotEqual(targetVar, Expression.Constant(null, target.Type)),
                voidMutation));
        }
        else
        {
            body.Add(voidMutation);
        }

        return Expression.Block(
            typeof(void),
            [sourceVar, targetVar],
            body);
    }

    private static Expression? TryBuildExistingNonNullMapping(
        Expression source,
        NullabilityState sourceNullability,
        Expression target,
        NullabilityState targetNullability,
        MappingPath path,
        bool assignTarget)
    {
        var targetType = target.Type;

        if (targetType.IsPointer || targetType.IsFunctionPointer)
        {
            return null;
        }

        // Коллекции: пытаемся очистить и заполнить существующую.
        if (IsCollectionType(targetType, out _) && IsCollectionType(source.Type, out _))
        {
            if (TryBuildCollectionMutation(source, target, targetType, path) is { } mutation)
            {
                return Expression.Block(
                    targetType,
                    mutation,
                    target);
            }

            // Массивы и коллекции без Clear/Add/AddRange пропускаем.
            return null;
        }

        // Обычные объекты: пытаемся рекурсивно заполнить существующий объект.
        var statements = TryBuildObjectMutationStatements(source, sourceNullability, target, targetNullability, path);

        if (statements.Count > 0)
        {
            return Expression.Block(
                targetType,
                statements.Append(target));
        }

        if (!assignTarget)
        {
            return null;
        }

        return TryBuildAssignmentConversion(source, targetType);
    }

    private static Expression? TryBuildAssignmentConversion(Expression source, Type targetType)
    {
        var sourceType = source.Type;

        // Прямое присвоение.
        if (targetType.IsAssignableFrom(sourceType))
        {
            return EnsureType(source, targetType);
        }

        // Nullable unwrap / Nullable wrap.
        var sourceUnderlying = Nullable.GetUnderlyingType(sourceType);
        var targetUnderlying = Nullable.GetUnderlyingType(targetType);

        if (sourceUnderlying is not null || targetUnderlying is not null)
        {
            var nonNullSource = sourceUnderlying is null
                ? source
                : Expression.Property(source, sourceType.GetProperty("Value")!);

            var nonNullTarget = targetUnderlying ?? targetType;

            if (TryBuildAssignmentConversion(nonNullSource, nonNullTarget) is { } converted)
            {
                return EnsureType(converted, targetType);
            }
        }

        // enum -> string
        if (sourceType.IsEnum && targetType == typeof(string))
        {
            return Expression.Call(
                Expression.Convert(source, typeof(object)),
                ObjectToStringMethod);
        }

        // string -> enum
        if (sourceType == typeof(string) && targetType.IsEnum)
        {
            return BuildStringToEnum(source, targetType);
        }

        // Остальные стандартные конверсии: int -> long, double -> decimal и т.п.
        return TryConvert(source, targetType);
    }

    private static IReadOnlyList<Expression> TryBuildObjectMutationStatements(
    Expression source,
    NullabilityState sourceNullability,
    Expression target,
    NullabilityState targetNullability,
    MappingPath path)
    {
        var targetType = target.Type;

        if (targetType.IsPointer ||
            targetType.IsFunctionPointer ||
            targetType.IsPrimitive ||
            targetType.IsEnum ||
            targetType == typeof(string))
        {
            return [];
        }

        List<Expression> expressions = [];

        foreach (var member in GetWritableMembers(targetType))
        {
            if (TryBuildWritableMemberStatement(member, source, sourceNullability, target, targetNullability, path) is { } statement)
            {
                expressions.Add(statement);
            }
        }

        foreach (var member in GetReadableNonWritableMembers(targetType))
        {
            if (TryBuildReadOnlyMemberMutation(member, source, sourceNullability, target, targetNullability, path) is { } statement)
            {
                expressions.Add(statement);
            }
        }

        return expressions;
    }

    private static BinaryExpression? TryBuildWritableMemberStatement(
    MemberInfo member,
    Expression sourceOwner,
    NullabilityState sourceNullability,
    Expression targetOwner,
    NullabilityState targetNullability,
    MappingPath path)
    {
        var memberType = GetMemberType(member);
        var targetAccess = Expression.MakeMemberAccess(targetOwner, member);

        foreach (var (sourceAccess, _)
            in GetSourceMemberAccess(sourceOwner, NullabilityState.NotNull, member.Name))
        {
            using var guard = path.Push(memberType, sourceAccess.Type);

            if (TryBuildMapExpression(
                    sourceAccess,
                    sourceNullability,
                    targetAccess,
                    targetNullability,
                    path,
                    assignTarget: true,
                    requireExistingMapping: false) is not { } mapped)
            {
                continue;
            }

            return Expression.Assign(targetAccess, mapped);
        }

        return null;
    }

    private static BlockExpression? TryBuildReadOnlyMemberMutation(
        MemberInfo member,
        Expression sourceOwner,
        NullabilityState sourceNullability,
        Expression targetOwner,
        NullabilityState targetNullability,
        MappingPath path)
    {
        var memberType = GetMemberType(member);

        if (memberType.IsValueType)
        {
            return null;
        }

        var targetAccess = Expression.MakeMemberAccess(targetOwner, member);

        foreach (var (sourceAccess, _)
            in GetSourceMemberAccess(sourceOwner, NullabilityState.NotNull, member.Name))
        {
            using var guard = path.Push(memberType, sourceAccess.Type);

            if (TryBuildMutationExpression(
                    sourceAccess,
                    sourceNullability,
                    targetAccess,
                    targetNullability,
                    path: path) is { } mutation)
            {
                return mutation;
            }
        }

        return null;
    }

    private static BlockExpression? TryBuildCollectionMutation(
    Expression source,
    Expression target,
    Type targetCollectionType,
    MappingPath path)
    {
        if (!IsCollectionType(targetCollectionType, out var targetElementType) ||
            targetElementType is null)
        {
            return null;
        }

        if (!IsCollectionType(source.Type, out var sourceElementType) ||
            sourceElementType is null)
        {
            return null;
        }

        // Массивы не поддерживаем: нельзя безопасно очистить и заполнить существующий массив
        // без информации о длине и без замены ссылки.
        if (targetCollectionType.IsArray)
        {
            return null;
        }

        var clearMethod = FindCollectionClearMethod(targetCollectionType);
        var addMethod = FindCollectionAddMethod(targetCollectionType, targetElementType);
        var addRangeMethod = FindCollectionAddRangeMethod(targetCollectionType, targetElementType);

        if (clearMethod is null || (addMethod is null && addRangeMethod is null))
        {
            return null;
        }

        var itemParam = Expression.Parameter(sourceElementType, "item");

        Expression mappedItem;

        using (path.Push(targetElementType, sourceElementType))
        {
            if (TryBuildCreationExpression(
                    itemParam,
                    targetElementType,
                    NullabilityState.Unknown,
                    path) is not { } itemBody)
            {
                return null;
            }

            mappedItem = itemBody;
        }

        if (EnsureType(mappedItem, targetElementType) is not { } typedMappedItem)
        {
            return null;
        }

        var lambdaType = typeof(Func<,>).MakeGenericType(sourceElementType, targetElementType);

        var lambda = Expression.Lambda(
            lambdaType,
            typedMappedItem,
            itemParam);

        var selectCall = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Select),
            [sourceElementType, targetElementType],
            source,
            lambda);

        // Обязательно материализуем результат до Clear,
        // чтобы не потерять данные, если source и target — одна и та же коллекция.
        var toListCall = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.ToList),
            [targetElementType],
            selectCall);

        var mappedListType = typeof(List<>).MakeGenericType(targetElementType);
        var mappedListVar = Expression.Variable(mappedListType, "mappedItems");

        List<Expression> body =
        [
            Expression.Assign(mappedListVar, toListCall),
        Expression.Call(target, clearMethod)
        ];

        if (addRangeMethod is not null)
        {
            body.Add(Expression.Call(target, addRangeMethod, mappedListVar));
        }
        else
        {
            body.Add(BuildAddFromEnumerableLoop(
                target,
                addMethod!,
                mappedListVar,
                targetElementType));
        }

        return Expression.Block(
            typeof(void),
            [mappedListVar],
            body);
    }
    private static Expression? TryBuildCreationExpression(
    Expression source,
    Type targetType,
    NullabilityState targetNullability,
    MappingPath path)
    {
        try
        {
            var created = BuildCreationBody(
                source,
                NullabilityState.NotNull,
                targetType,
                targetNullability,
                path);

            if (created is null)
            {
                return null;
            }

            return EnsureType(created, targetType);
        }
        catch (Exception ex) when (ex is MappingException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
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
    private static IEnumerable<MemberInfo> GetWritableMembers(Type type)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => p.SetMethod is { IsPublic: true })
            .Where(p => !IsInitOnly(p));

        var fields = type
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !f.IsInitOnly && !f.IsLiteral);

        return properties.Cast<MemberInfo>().Concat(fields);
    }

    private static IEnumerable<MemberInfo> GetReadableNonWritableMembers(Type type)
    {
        var properties = type
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p =>
                p.CanRead &&
                p.GetGetMethod() is not null &&
                p.GetIndexParameters().Length == 0)
            .Where(p =>
                p.SetMethod is null ||
                !p.SetMethod.IsPublic ||
                IsInitOnly(p));

        var fields = type
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.IsInitOnly && !f.IsLiteral);

        return properties.Cast<MemberInfo>().Concat(fields);
    }

    private static Expression? EnsureType(Expression expression, Type type)
    {
        if (expression.Type == type)
        {
            return expression;
        }

        if (type.IsAssignableFrom(expression.Type))
        {
            return Expression.Convert(expression, type);
        }

        try
        {
            return Expression.Convert(expression, type);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static Expression ToVoid(Expression expression)
    {
        if (expression.Type == typeof(void))
        {
            return expression;
        }

        return Expression.Block(typeof(void), expression);
    }

    private static UnaryExpression ThrowNothingMapped(Type sourceType, Type targetType)
    {
        var message =
            $"Nothing were mapped from '{sourceType.FullName}' to '{targetType.FullName}'.";

        return Expression.Throw(
            Expression.New(
                typeof(MappingException).GetConstructor([typeof(string)])!,
                Expression.Constant(message)),
            targetType);
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