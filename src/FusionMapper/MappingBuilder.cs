using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FusionMapper;

static class MappingBuilder
{
    private enum CollectionOperation
    {
        None,
        Count,
        Any,
        Sum,
        Average,
        Max,
        Min,
        First,
        FirstOrDefault,
        Last,
        LastOrDefault,
        All
    }

    private static readonly (string Name, CollectionOperation Operation)[] CollectionOperations = [
        ("FirstOrDefault", CollectionOperation.FirstOrDefault),
        ("LastOrDefault", CollectionOperation.LastOrDefault),
        ("First", CollectionOperation.First),
        ("Last", CollectionOperation.Last),
        ("Count", CollectionOperation.Count),
        ("Average", CollectionOperation.Average),
        ("Sum", CollectionOperation.Sum),
        ("Max", CollectionOperation.Max),
        ("Min", CollectionOperation.Min),
        ("Any", CollectionOperation.Any),
        ("All", CollectionOperation.All)
    ];

    private sealed record ObjectMappingPlan(
        NewExpression NewExpression,
        IReadOnlyList<MemberBinding> Bindings,
        int ConstructorParameterCount);

    public static Expression<Func<TSource, TTarget>> BuildCreationLambda<TSource, TTarget>()
    {
        var sourceParam = Expression.Parameter(typeof(TSource), "source");

        var sourceType = typeof(TSource);
        var targetType = typeof(TTarget);
        Stack<(Type Source, Type Target)> path = new();

        var body = BuildMappingBody(sourceParam, targetType, sourceType, path);
        return Expression.Lambda<Func<TSource, TTarget>>(EnsureType(body, targetType), sourceParam);
    }

    public static Expression<Action<TSource, TTarget>> BuildAssignmentExpression<TSource, TTarget>()
    {

        var sourceParam = Expression.Parameter(typeof(TSource), "source");
        var targetParam = Expression.Parameter(typeof(TTarget), "target");

        var sourceType = typeof(TSource);
        var targetType = typeof(TTarget);
        Stack<(Type Source, Type Target)> path = new();

        List<Expression> bodyExpressions = [];

        var writableProperties = targetType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !IsInitOnly(p))
            .Cast<MemberInfo>();

        var writableFields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !f.IsInitOnly && !f.IsLiteral)
            .Cast<MemberInfo>();

        foreach (var member in writableProperties.Concat(writableFields))
        {
            var targetMemberType = GetMemberType(member);

            if (TryGetSourceMemberAccess(
                    sourceType,
                    sourceParam,
                    member.Name,
                    path,
                    out var accessExpr))
            {
                var mappedExpr = BuildMappingBody(accessExpr, targetMemberType, accessExpr.Type, path);

                var assign = Expression.Assign(
                    Expression.MakeMemberAccess(targetParam, member),
                    EnsureType(mappedExpr, targetMemberType));

                bodyExpressions.Add(assign);
            }
        }

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

        foreach (var member in readOnlyProperties.Concat(readOnlyFields))
        {
            var targetMemberType = GetMemberType(member);

            if (!IsCollectionType(targetMemberType, out _))
                continue;

            if (!TryGetSourceMemberAccess(
                    sourceType,
                    sourceParam,
                    member.Name,
                    path,
                    out var accessExpr))
            {
                continue;
            }

            if (TryBuildReadOnlyCollectionMutation(
                    targetParam,
                    member,
                    targetMemberType,
                    accessExpr,
                    path,
                    out var mutationExpression))
            {
                bodyExpressions.Add(mutationExpression);
            }
        }

        var body = bodyExpressions.Count > 0
            ? Expression.Block(typeof(void), bodyExpressions.ToArray())
            : throw new MappingException($"No properties were mapped");

        return Expression.Lambda<Action<TSource, TTarget>>(body, sourceParam, targetParam);
    }

    private static bool TryBuildReadOnlyCollectionMutation(
        ParameterExpression targetParam,
        MemberInfo member,
        Type targetCollectionType,
        Expression sourceAccess,
        Stack<(Type Source, Type Target)> path,
        out Expression mutationExpression)
    {
        mutationExpression = null!;

        if (!IsCollectionType(targetCollectionType, out var targetElementType) ||
            targetElementType is null)
        {
            return false;
        }

        if (!IsCollectionType(sourceAccess.Type, out var sourceElementType) ||
            sourceElementType is null)
        {
            return false;
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
        var mappedItem = BuildMappingBody(itemParam, targetElementType, sourceElementType, path);
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

        mutationExpression = Expression.Block(
            typeof(void),
            [existingVar, sourceVar, mappedListVar, iVar],
            body);

        return true;
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

    private static Expression BuildMappingBody(
        Expression sourceExpr,
        Type targetType,
        Type sourceType,
        Stack<(Type Source, Type Target)> path)
    {
        var pair = (sourceType, targetType);

        if (path.Contains(pair))
        {
            throw new MappingException(
                $"Recursive mapping detected between '{sourceType.FullName}' and '{targetType.FullName}'. " +
                $"Path: {string.Join(" -> ", path.Select(p => p.Source.Name + "->" + p.Target.Name))} -> {sourceType.Name}. " +
                "Recursive and cyclic type graphs are not supported.");
        }

        if (sourceType == targetType)
            return sourceExpr;

        if (!sourceType.IsValueType || Nullable.GetUnderlyingType(sourceType) != null)
        {
            var nullCheck = Expression.Equal(
                sourceExpr,
                Expression.Constant(null, sourceType));

            Expression defaultTarget;

            if (targetType.IsClass || Nullable.GetUnderlyingType(targetType) != null)
            {
                defaultTarget = Expression.Default(targetType);
            }
            else
            {
                defaultTarget = Expression.Throw(
                    Expression.New(
                        typeof(MappingException).GetConstructor([typeof(string)])!,
                        Expression.Constant(
                            $"Cannot map null source to non-nullable value type '{targetType.FullName}'.")),
                    targetType);
            }

            var nonNullBody = BuildNonNullMappingBody(sourceExpr, targetType, sourceType, path);

            return Expression.Condition(
                nullCheck,
                defaultTarget,
                nonNullBody,
                targetType);
        }

        return BuildNonNullMappingBody(sourceExpr, targetType, sourceType, path);
    }

    private static Expression BuildNonNullMappingBody(
        Expression sourceExpr,
        Type targetType,
        Type sourceType,
        Stack<(Type Source, Type Target)> path)
    {
        if (targetType.IsAssignableFrom(sourceType))
            return sourceExpr;

        if (IsSimpleType(targetType) || IsSimpleType(sourceType))
            return TryConvert(sourceExpr, targetType);

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

        return BuildObjectMapping(sourceExpr, targetType, sourceType, path);
    }

    private static Expression BuildObjectMapping(
    Expression sourceExpr,
    Type targetType,
    Type sourceType,
    Stack<(Type Source, Type Target)> path)
    {
        path.Push((sourceType, targetType));

        try
        {
            var constructors = targetType
                .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .ToArray();

            ObjectMappingPlan? bestPlan = null;
            int? bestParameterCount = null;
            string? lastFailure = null;

            foreach (var constructor in constructors)
            {
                var parameterCount = constructor.GetParameters().Length;

                // Если уже есть подходящий конструктор с большим числом параметров,
                // конструкторы с меньшим числом параметров не рассматриваем.
                if (bestPlan is not null && parameterCount < bestParameterCount!.Value)
                    break;

                if (!TryBuildConstructorPlan(
                        constructor,
                        sourceType,
                        sourceExpr,
                        targetType,
                        path,
                        out var plan,
                        out var failureReason))
                {
                    lastFailure = failureReason;
                    continue;
                }

                // Если найдено несколько полностью bindable-конструкторов
                // с одинаковым количеством параметров, это неоднозначность.
                if (bestPlan is not null)
                {
                    throw new MappingException(
                        $"Ambiguous constructor selection for type '{targetType.FullName}'. " +
                        $"Found multiple constructors with {parameterCount} bindable parameter(s).");
                }

                bestPlan = plan;
                bestParameterCount = parameterCount;
            }

            if (bestPlan is null)
            {
                if (lastFailure is not null &&
                    lastFailure.StartsWith("required:", StringComparison.Ordinal))
                {
                    var requiredMember = lastFailure["required:".Length..];

                    throw new MappingException(
                        $"Required member '{requiredMember}' cannot be mapped from source type '{sourceType.FullName}'.");
                }

                if (lastFailure is not null &&
                    lastFailure.StartsWith("ctor:", StringComparison.Ordinal))
                {
                    var parameterName = lastFailure["ctor:".Length..];

                    throw new MappingException(
                        $"Constructor parameter '{parameterName}' of type '{targetType.FullName}' cannot be mapped from source type '{sourceType.FullName}'.");
                }

                throw new MappingException(
                    $"No suitable constructor found for type '{targetType.FullName}'.");
            }

            return bestPlan.Bindings.Count > 0
                ? Expression.MemberInit(bestPlan.NewExpression, bestPlan.Bindings)
                : bestPlan.NewExpression;
        }
        finally
        {
            path.Pop();
        }
    }

    private static bool TryBuildConstructorPlan(
    ConstructorInfo constructor,
    Type sourceType,
    Expression sourceExpr,
    Type targetType,
    Stack<(Type Source, Type Target)> path,
    out ObjectMappingPlan plan,
    out string? failureReason)
    {
        plan = null!;
        failureReason = null;

        var parameters = constructor.GetParameters();

        List<Expression> args = [];
        HashSet<string> initializedNames = new(StringComparer.OrdinalIgnoreCase);

        // 1. Маппим все аргументы конструктора.
        foreach (var parameter in parameters)
        {
            if (!TryGetSourceMemberAccess(
                    sourceType,
                    sourceExpr,
                    parameter.Name!,
                    path,
                    out var accessExpr))
            {
                failureReason = $"ctor:{parameter.Name}";
                return false;
            }

            var mappedExpr = BuildMappingBody(
                accessExpr,
                parameter.ParameterType,
                accessExpr.Type,
                path);

            args.Add(EnsureType(mappedExpr, parameter.ParameterType));
            initializedNames.Add(parameter.Name!);
        }

        var newExpression = args.Count > 0
            ? Expression.New(constructor, args)
            : Expression.New(constructor);

        List<MemberBinding> bindings = [];
        HashSet<string> filledNames = new(StringComparer.OrdinalIgnoreCase);

        // 2. Маппим settable или init-only свойства,
        //    имена которых не совпадают с аргументами конструктора.
        var settableOrInitOnlyProperties = targetType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0)
            .Where(p => p.SetMethod is { IsPublic: true })
            .ToArray();

        foreach (var property in settableOrInitOnlyProperties)
        {
            if (initializedNames.Contains(property.Name))
                continue;

            if (!TryGetSourceMemberAccess(
                    sourceType,
                    sourceExpr,
                    property.Name,
                    path,
                    out var accessExpr))
            {
                continue;
            }

            var mappedExpr = BuildMappingBody(
                accessExpr,
                property.PropertyType,
                accessExpr.Type,
                path);

            bindings.Add(Expression.Bind(property, EnsureType(mappedExpr, property.PropertyType)));
            filledNames.Add(property.Name);
        }

        // 3. Маппим публичные поля.
        //    Под полями здесь понимаются публичные instance-поля,
        //    которые можно использовать в инициализаторе:
        //    не const и не readonly.
        var publicFields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => !f.IsLiteral && !f.IsInitOnly)
            .ToArray();

        foreach (var field in publicFields)
        {
            if (initializedNames.Contains(field.Name))
                continue;

            if (!TryGetSourceMemberAccess(
                    sourceType,
                    sourceExpr,
                    field.Name,
                    path,
                    out var accessExpr))
            {
                continue;
            }

            var mappedExpr = BuildMappingBody(
                accessExpr,
                field.FieldType,
                accessExpr.Type,
                path);

            bindings.Add(Expression.Bind(field, EnsureType(mappedExpr, field.FieldType)));
            filledNames.Add(field.Name);
        }

        // 4. Проверяем required-члены, если конструктор
        //    не помечен SetsRequiredMembersAttribute.
        if (constructor.GetCustomAttribute<SetsRequiredMembersAttribute>() is null)
        {
            foreach (var requiredMember in GetRequiredMemberNames(targetType))
            {
                if (!initializedNames.Contains(requiredMember) &&
                    !filledNames.Contains(requiredMember))
                {
                    failureReason = $"required:{requiredMember}";
                    return false;
                }
            }
        }

        plan = new ObjectMappingPlan(
            NewExpression: newExpression,
            Bindings: bindings,
            ConstructorParameterCount: parameters.Length);

        return true;
    }

    private static string[] GetRequiredMemberNames(Type targetType)
    {
        var properties = targetType
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            .Select(p => p.Name);

        var fields = targetType
            .GetFields(BindingFlags.Public | BindingFlags.Instance)
            .Where(f => f.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            .Select(f => f.Name);

        return [.. properties
            .Concat(fields)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private static Expression BuildCollectionMapping(
        Expression sourceExpr,
        Type sourceElementType,
        Type targetElementType,
        Type targetCollectionType,
        Stack<(Type Source, Type Target)> path)
    {
        var itemParam = Expression.Parameter(sourceElementType, "item");
        var mappedItem = BuildMappingBody(itemParam, targetElementType, sourceElementType, path);
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

    private static bool TryGetSourceMemberAccess(
        Type sourceType,
        Expression sourceExpr,
        string targetMemberName,
        Stack<(Type Source, Type Target)> path,
        out Expression accessExpr)
    {
        if (TryResolveSuffix(sourceType, sourceExpr, targetMemberName, path, out accessExpr))
            return true;

        accessExpr = null!;
        return false;
    }

    private static bool TryResolveSuffix(
        Type sourceType,
        Expression sourceExpr,
        string suffix,
        Stack<(Type Source, Type Target)> path,
        out Expression result)
    {
        if (TryResolveSuffixCore(sourceType, sourceExpr, suffix, path, exactOnly: true, out result))
            return true;

        if (TryResolveSuffixCore(sourceType, sourceExpr, suffix, path, exactOnly: false, out result))
            return true;

        result = null!;
        return false;
    }

    private static bool TryResolveSuffixCore(
        Type sourceType,
        Expression sourceExpr,
        string suffix,
        Stack<(Type Source, Type Target)> path,
        bool exactOnly,
        out Expression result)
    {
        result = null!;

        if (suffix.Length == 0)
        {
            result = sourceExpr;
            return true;
        }

        if (TryGetDirectSourceMember(sourceType, suffix, exactOnly, out var directMember))
        {
            result = Expression.MakeMemberAccess(sourceExpr, directMember);
            return true;
        }

        var nullableUnderlying = Nullable.GetUnderlyingType(sourceType);
        if (nullableUnderlying is not null)
        {
            var valueAccess = Expression.Property(sourceExpr, "Value");

            if (TryResolveSuffixCore(
                    nullableUnderlying,
                    valueAccess,
                    suffix,
                    path,
                    exactOnly,
                    out var nullableNested))
            {
                result = WrapNullSafe(sourceExpr, nullableNested);
                return true;
            }
        }

        var comparison = exactOnly
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var prefixMembers = GetSourceMembers(sourceType)
            .Where(m =>
                suffix.Length > m.Name.Length &&
                suffix.StartsWith(m.Name, comparison))
            .OrderByDescending(m => m.Name.Length)
            .ToArray();

        foreach (var member in prefixMembers)
        {
            var remaining = suffix[member.Name.Length..].TrimStart('_');
            if (remaining.Length == 0)
                continue;

            var memberAccess = Expression.MakeMemberAccess(sourceExpr, member);
            var memberType = GetMemberType(member);

            if (TryResolveSuffixCore(
                    memberType,
                    memberAccess,
                    remaining,
                    path,
                    exactOnly,
                    out var nested))
            {
                result = WrapNullSafe(memberAccess, nested);
                return true;
            }
        }

        if (exactOnly &&
            IsCollectionType(sourceType, out var elementType) &&
            elementType is not null)
        {
            foreach (var (Name, Operation) in CollectionOperations.OrderByDescending(o => o.Name.Length))
            {
                if (!suffix.StartsWith(Name, StringComparison.Ordinal))
                    continue;

                var remaining = suffix[Name.Length..].TrimStart('_');

                if (!TryBuildOperationExpression(
                        sourceExpr,
                        elementType,
                        Operation,
                        sequence: null,
                        sequenceElementType: null,
                        out var operationExpr))
                {
                    continue;
                }

                if (remaining.Length == 0)
                {
                    result = operationExpr;
                    return true;
                }

                if (TryResolveSuffixCore(
                        operationExpr.Type,
                        operationExpr,
                        remaining,
                        path,
                        exactOnly,
                        out var nested))
                {
                    result = WrapNullSafe(operationExpr, nested);
                    return true;
                }
            }

            if (TryResolveCollectionSuffixOperation(
                    sourceExpr,
                    elementType,
                    suffix,
                    path,
                    out result))
            {
                return true;
            }
        }

        if (IsCollectionType(sourceType, out elementType) && elementType is not null)
        {
            // Уже есть операция как префикс, например:
            // CollectionFirstDateYear -> source.Collection.First().Date.Year
            //
            // Уже может быть операция как суффикс, например:
            // CollectionDateFirst -> source.Collection.Select(x => x.Date).First()

            // Дополнительный кандидатный поиск:
            // property(T) + operation
            if (TryResolveCollectionElementPropertyOperation(
                    sourceExpr,
                    elementType,
                    suffix,
                    path,
                    exactOnly,
                    out result))
            {
                return true;
            }
        }
        return false;
    }


    private static bool TryResolveCollectionElementPropertyOperation(
        Expression collectionExpr,
        Type elementType,
        string suffix,
        Stack<(Type Source, Type Target)> path,
        bool exactOnly,
        out Expression result)
    {
        result = null!;

        var memberComparison = exactOnly
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var elementMembers = GetSourceMembers(elementType)
            .OrderByDescending(m => m.Name.Length)
            .ToArray();

        foreach (var member in elementMembers)
        {
            if (suffix.Length <= member.Name.Length)
                continue;

            if (!suffix.StartsWith(member.Name, memberComparison))
                continue;

            var afterMember = suffix[member.Name.Length..].TrimStart('_');

            if (afterMember.Length == 0)
                continue;

            foreach (var (Name, Operation) in GetCollectionOperations())
            {
                // Операторы всегда матчим точно.
                if (!afterMember.StartsWith(Name, StringComparison.Ordinal))
                    continue;

                var remaining = afterMember[Name.Length..].TrimStart('_');

                var itemParam = Expression.Parameter(elementType, "item");
                Expression selectorBody = Expression.MakeMemberAccess(itemParam, member);

                if (CanBeNull(elementType))
                {
                    selectorBody = Expression.Condition(
                        Expression.Equal(itemParam, Expression.Constant(null, elementType)),
                        Expression.Default(selectorBody.Type),
                        selectorBody,
                        selectorBody.Type);
                }

                if (Operation == CollectionOperation.All)
                {
                    var normalizedBoolSelector = NormalizeBooleanExpression(selectorBody);

                    if (normalizedBoolSelector is null)
                        continue;

                    selectorBody = normalizedBoolSelector;
                }

                var lambda = Expression.Lambda(selectorBody, itemParam);

                var sequence = Expression.Call(
                    typeof(Enumerable),
                    nameof(Enumerable.Select),
                    [elementType, selectorBody.Type],
                    collectionExpr,
                    lambda);

                if (!TryBuildOperationExpression(
                        collectionExpr,
                        elementType,
                        Operation,
                        sequence,
                        selectorBody.Type,
                        out var operationExpr))
                {
                    continue;
                }

                if (remaining.Length == 0)
                {
                    result = operationExpr;
                    return true;
                }

                if (TryResolveSuffixCore(
                        operationExpr.Type,
                        operationExpr,
                        remaining,
                        path,
                        exactOnly,
                        out var nested))
                {
                    result = WrapNullSafe(operationExpr, nested);
                    return true;
                }
            }
        }

        return false;
    }

    private static Expression? NormalizeBooleanExpression(Expression expression)
    {
        if (expression.Type == typeof(bool))
            return expression;

        if (expression.Type == typeof(bool?))
        {
            return Expression.Equal(
                expression,
                Expression.Constant(true, typeof(bool?)));
        }

        return null;
    }

    private static bool TryResolveCollectionSuffixOperation(
        Expression collectionExpr,
        Type elementType,
        string suffix,
        Stack<(Type Source, Type Target)> path,
        out Expression result)
    {
        result = null!;

        foreach (var (Name, Operation) in CollectionOperations.OrderByDescending(o => o.Name.Length))
        {
            if (!suffix.EndsWith(Name, StringComparison.Ordinal))
                continue;

            var selectorSuffix = suffix[..^Name.Length].TrimEnd('_');

            Expression sequence = collectionExpr;
            Type projectedType = elementType;

            if (selectorSuffix.Length > 0)
            {
                if (Operation == CollectionOperation.Count ||
                    Operation == CollectionOperation.Any)
                {
                    continue;
                }

                var itemParam = Expression.Parameter(elementType, "item");

                if (!TryResolveSuffix(elementType, itemParam, selectorSuffix, path, out var selectorBody))
                    continue;

                if (CanBeNull(elementType))
                {
                    selectorBody = Expression.Condition(
                        Expression.Equal(itemParam, Expression.Constant(null, elementType)),
                        Expression.Default(selectorBody.Type),
                        selectorBody,
                        selectorBody.Type);
                }

                var lambda = Expression.Lambda(selectorBody, itemParam);

                sequence = Expression.Call(
                    typeof(Enumerable),
                    nameof(Enumerable.Select),
                    [elementType, selectorBody.Type],
                    collectionExpr,
                    lambda);

                projectedType = selectorBody.Type;
            }

            if (TryBuildOperationExpression(
                    collectionExpr,
                    elementType,
                    Operation,
                    sequence,
                    projectedType,
                    out result))
            {
                return true;
            }
        }

        return false;
    }

    private static Expression WrapNullSafe(Expression baseExpression, Expression nested)
    {
        if (!CanBeNull(baseExpression.Type))
            return nested;

        var resultType = nested.Type;

        if (resultType.IsValueType && Nullable.GetUnderlyingType(resultType) is null)
        {
            resultType = typeof(Nullable<>).MakeGenericType(resultType);
            nested = Expression.Convert(nested, resultType);
        }

        return Expression.Condition(
            Expression.Equal(baseExpression, Expression.Constant(null, baseExpression.Type)),
            Expression.Default(resultType),
            nested,
            resultType);
    }

    private static bool TryBuildOperationExpression(
        Expression collectionExpr,
        Type collectionElementType,
        CollectionOperation operation,
        Expression? sequence,
        Type? sequenceElementType,
        out Expression result)
    {
        result = null!;

        var effectiveSequence = sequence ?? collectionExpr;
        var effectiveElementType = sequenceElementType ?? collectionElementType;

        Expression rawCall;

        switch (operation)
        {
            case CollectionOperation.Count:
                if (!TryCallEnumerable(nameof(Enumerable.Count), effectiveElementType, effectiveSequence, out rawCall))
                    return false;
                break;

            case CollectionOperation.Any:
                if (!TryCallEnumerable(nameof(Enumerable.Any), effectiveElementType, effectiveSequence, out rawCall))
                    return false;
                break;

            case CollectionOperation.Sum:
            case CollectionOperation.Average:
            case CollectionOperation.Max:
            case CollectionOperation.Min:
                if (!TryCallAggregate(operation, effectiveElementType, effectiveSequence, out rawCall))
                    return false;
                break;

            case CollectionOperation.First:
                if (!TryCallEnumerable(nameof(Enumerable.First), effectiveElementType, effectiveSequence, out rawCall))
                    return false;
                break;

            case CollectionOperation.FirstOrDefault:
                if (!TryCallEnumerable(nameof(Enumerable.FirstOrDefault), effectiveElementType, effectiveSequence, out rawCall))
                    return false;
                break;

            case CollectionOperation.Last:
                if (!TryCallEnumerable(nameof(Enumerable.Last), effectiveElementType, effectiveSequence, out rawCall))
                    return false;
                break;

            case CollectionOperation.LastOrDefault:
                if (!TryCallEnumerable(nameof(Enumerable.LastOrDefault), effectiveElementType, effectiveSequence, out rawCall))
                    return false;
                break;
            case CollectionOperation.All:
                if (!TryBuildAllExpression(effectiveSequence, effectiveElementType, out rawCall))
                    return false;
                break;
            default:
                return false;
        }

        var defaultResult = Expression.Default(rawCall.Type);
        Expression body = rawCall;

        var protectFromEmpty = operation != CollectionOperation.Count &&
                               operation != CollectionOperation.Any &&
                               operation != CollectionOperation.All;
        if (protectFromEmpty)
        {
            if (!TryCallEnumerable(nameof(Enumerable.Any), collectionElementType, collectionExpr, out var anyCall))
                return false;

            body = Expression.Condition(
                anyCall,
                body,
                defaultResult,
                rawCall.Type);
        }

        if (CanBeNull(collectionExpr.Type))
        {
            var nullCheck = Expression.Equal(
                collectionExpr,
                Expression.Constant(null, collectionExpr.Type));

            body = Expression.Condition(
                nullCheck,
                defaultResult,
                body,
                rawCall.Type);
        }

        result = body;
        return true;
    }


    private static bool TryBuildAllExpression(
    Expression sequence,
    Type elementType,
    out Expression call)
    {
        call = null!;

        if (elementType != typeof(bool))
            return false;

        var allMethod = typeof(Enumerable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m =>
                m.Name == nameof(Enumerable.All) &&
                m.IsGenericMethodDefinition &&
                m.GetParameters().Length == 2);

        if (allMethod is null)
            return false;

        var genericMethod = allMethod.MakeGenericMethod(elementType);

        var itemParam = Expression.Parameter(elementType, "allItem");
        var predicateBody = itemParam; // x => x
        var predicate = Expression.Lambda(predicateBody, itemParam);

        call = Expression.Call(
            genericMethod,
            sequence,
            predicate);

        return true;
    }

    private static bool TryCallAggregate(
        CollectionOperation operation,
        Type elementType,
        Expression sequence,
        out Expression call)
    {
        call = null!;

        var methodName = operation switch
        {
            CollectionOperation.Sum => nameof(Enumerable.Sum),
            CollectionOperation.Average => nameof(Enumerable.Average),
            CollectionOperation.Max => nameof(Enumerable.Max),
            CollectionOperation.Min => nameof(Enumerable.Min),
            _ => throw new MappingException($"Unsupported aggregate operation '{operation}'.")
        };

        return TryCallEnumerable(methodName, elementType, sequence, out call);
    }

    private static bool TryCallEnumerable(string methodName, Type elementType, Expression source, out Expression call)
    {
        call = null!;

        try
        {
            var method = FindEnumerableMethod(methodName, elementType, source);
            call = Expression.Call(method, source);
            return true;
        }
        catch (MappingException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static MethodInfo FindEnumerableMethod(string methodName, Type elementType, Expression source)
    {
        var methods = typeof(Enumerable)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == methodName && m.GetParameters().Length == 1)
            .ToArray();

        foreach (var method in methods)
        {
            if (method.IsGenericMethodDefinition && method.GetGenericArguments().Length == 1)
            {
                MethodInfo generic;

                try
                {
                    generic = method.MakeGenericMethod(elementType);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (generic.GetParameters()[0].ParameterType.IsAssignableFrom(source.Type))
                    return generic;
            }
            else if (!method.IsGenericMethod)
            {
                if (method.GetParameters()[0].ParameterType.IsAssignableFrom(source.Type))
                    return method;
            }
        }

        throw new MappingException(
            $"Cannot find suitable Enumerable.{methodName} overload for element type '{elementType.FullName}'.");
    }

    private static bool TryGetDirectSourceMember(
        Type sourceType,
        string memberName,
        bool exactOnly,
        out MemberInfo member)
    {
        member = null!;

        var comparison = exactOnly
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        var candidates = GetSourceMembers(sourceType)
            .Where(m => string.Equals(m.Name, memberName, comparison))
            .ToArray();

        if (candidates.Length == 1)
        {
            member = candidates[0];
            return true;
        }

        if (candidates.Length > 1)
        {
            var preferred = TryGetPreferredProperty(candidates);
            if (preferred is not null)
            {
                member = preferred;
                return true;
            }

            throw new MappingException(
                $"Ambiguous source member match for member '{memberName}' on source type '{sourceType.FullName}'.");
        }

        return false;
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

        return properties.Concat(fields)
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .Select(g => g.OfType<PropertyInfo>().FirstOrDefault() ?? g.First());
    }

    private static PropertyInfo? TryGetPreferredProperty(MemberInfo[] candidates)
    {
        var properties = candidates.OfType<PropertyInfo>().ToArray();
        return properties.Length == 1 ? properties[0] : null;
    }

    private static Expression EnsureType(Expression expression, Type type)
    {
        if (expression.Type == type)
            return expression;

        return Expression.Convert(expression, type);
    }

    private static Expression TryConvert(Expression expr, Type targetType)
    {
        if (expr.Type == targetType)
            return expr;

        if (targetType.IsAssignableFrom(expr.Type))
            return expr;

        if (expr.Type.IsGenericType && expr.Type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            var underlying = Nullable.GetUnderlyingType(expr.Type);
            if (targetType == underlying)
                return Expression.Convert(expr, targetType);
        }
        else if (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            var underlying = Nullable.GetUnderlyingType(targetType);
            if (expr.Type == underlying)
                return Expression.Convert(expr, targetType);
        }

        return Expression.Convert(expr, targetType);
    }

    private static bool IsSimpleType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return type.IsPrimitive ||
               type.IsEnum ||
               type == typeof(string) ||
               type == typeof(decimal) ||
               type == typeof(DateTime) ||
               type == typeof(Guid) ||
               type == typeof(TimeSpan) ||
               type == typeof(DateTimeOffset);
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

    private static IEnumerable<(string Name, CollectionOperation Operation)> GetCollectionOperations()
    {
        return CollectionOperations.OrderByDescending(o => o.Name.Length);
    }
}