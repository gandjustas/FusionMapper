using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace FusionMapper;

static class MappingBuilder
{
    public static Expression BuildCreationExpression<TSource, TTarget>(ParameterExpression sourceParam)
    {
        var sourceType = typeof(TSource);
        var targetType = typeof(TTarget);
        var path = new Stack<(Type Source, Type Target)>();
        var body = BuildMappingBody(sourceParam, targetType, sourceType, path);
        return Expression.Convert(body, targetType);
    }

    public static Expression BuildAssignmentExpression<TSource, TTarget>(ParameterExpression sourceParam, ParameterExpression targetParam)
    {
        var sourceType = typeof(TSource);
        var targetType = typeof(TTarget);

        var members = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && !IsInitOnly(p))
            .Cast<MemberInfo>()
            .Concat(targetType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                .Where(f => !f.IsInitOnly && !f.IsLiteral))
            .ToArray();

        List<Expression> assignments = [];
        var path = new Stack<(Type, Type)>();
        foreach (var member in members)
        {
            if (TryGetSourceMemberAccess(sourceType, sourceParam, member.Name, out var accessExpr))
            {
                var targetMemberType = GetMemberType(member);
                var mappedExpr = BuildMappingBody(accessExpr, targetMemberType, accessExpr.Type, path);
                var assign = Expression.Assign(
                    Expression.MakeMemberAccess(targetParam, member),
                    Expression.Convert(mappedExpr, targetMemberType)
                );
                assignments.Add(assign);
            }
        }

        return Expression.Block(assignments);
    }

    private static Expression BuildMappingBody(Expression sourceExpr, Type targetType, Type sourceType, Stack<(Type Source, Type Target)> path)
    {
        // Проверка на рекурсивный цикл
        var pair = (sourceType, targetType);
        if (path.Contains(pair))
        {
            throw new MappingException($"Recursive mapping detected between '{sourceType.FullName}' and '{targetType.FullName}'. Path: {string.Join(" -> ", path.Select(p => p.Source.Name + "->" + p.Target.Name))} -> {sourceType.Name}. Recursive and cyclic type graphs are not supported.");
        }

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
                            $"Cannot map null source to non-nullable value type '{targetType.FullName}'.")
                    ),
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

    private static Expression BuildNonNullMappingBody(Expression sourceExpr, Type targetType, Type sourceType, Stack<(Type Source, Type Target)> path)
    {
        if (targetType.IsAssignableFrom(sourceType))
            return sourceExpr;

        if (IsSimpleType(targetType) || IsSimpleType(sourceType))
        {
            return TryConvert(sourceExpr, targetType);
        }

        if (IsCollectionType(targetType, out var targetElementType) &&
            IsCollectionType(sourceType, out var sourceElementType))
        {
            return BuildCollectionMapping(sourceExpr, sourceElementType!, targetElementType!, targetType, path);
        }

        return BuildObjectMapping(sourceExpr, targetType, sourceType, path);
    }

    private static Expression BuildObjectMapping(Expression sourceExpr, Type targetType, Type sourceType, Stack<(Type Source, Type Target)> path)
    {
        path.Push((sourceType, targetType));

        try
        {
            var constructors = targetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .Where(c => c.GetParameters().Length > 0)
                .OrderByDescending(c => c.GetParameters().Length)
                .ToArray();

            ConstructorInfo? selectedCtor = null;
            Dictionary<string, Expression>? paramMap = null;

            foreach (var ctor in constructors)
            {
                var parameters = ctor.GetParameters();
                Dictionary<string, Expression> dict = [];
                bool allFound = true;
                foreach (var param in parameters)
                {
                    if (TryGetSourceMemberAccess(sourceType, sourceExpr, param.Name!, out var accessExpr))
                    {
                        var mappedExpr = BuildMappingBody(accessExpr, param.ParameterType, accessExpr.Type, path);
                        dict[param.Name!] = Expression.Convert(mappedExpr, param.ParameterType);
                    }
                    else
                    {
                        allFound = false;
                        break;
                    }
                }
                if (allFound)
                {
                    selectedCtor = ctor;
                    paramMap = dict;
                    break;
                }
            }

            if (selectedCtor == null)
            {
                selectedCtor = targetType.GetConstructor(Type.EmptyTypes)
                    ?? throw new MappingException($"No suitable constructor found for type '{targetType.FullName}'.");
                paramMap = [];
            }

            NewExpression newExpr;
            if (selectedCtor.GetParameters().Length == 0)
            {
                newExpr = Expression.New(selectedCtor);
            }
            else
            {
                var args = selectedCtor.GetParameters().Select(p => paramMap![p.Name!]).ToArray();
                newExpr = Expression.New(selectedCtor, args);
            }

            HashSet<string> initializedMembers = [.. selectedCtor.GetParameters().Select(p => p.Name!)];

            var targetMembers = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .Cast<MemberInfo>()
                .Concat(targetType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(f => !f.IsInitOnly && !f.IsLiteral))
                .ToArray();

            var requiredMembers = targetMembers.Where(m => m.GetCustomAttribute<RequiredMemberAttribute>() != null).ToArray();

            List<MemberBinding> bindings = [];
            foreach (var member in targetMembers)
            {
                if (initializedMembers.Contains(member.Name))
                    continue;

                if (TryGetSourceMemberAccess(sourceType, sourceExpr, member.Name, out var accessExpr))
                {
                    var targetMemberType = GetMemberType(member);
                    var mappedExpr = BuildMappingBody(accessExpr, targetMemberType, accessExpr.Type, path);
                    var converted = Expression.Convert(mappedExpr, targetMemberType);
                    var binding = Expression.Bind(member, converted);
                    bindings.Add(binding);
                    initializedMembers.Add(member.Name);
                }
                else
                {
                    if (requiredMembers.Contains(member))
                        throw new MappingException($"Required member '{member.Name}' cannot be mapped from source type '{sourceType.FullName}'.");
                }
            }

            if (selectedCtor.GetCustomAttribute<SetsRequiredMembersAttribute>() == null)
            {
                foreach (var req in requiredMembers.Where(r => !initializedMembers.Contains(r.Name)))
                    throw new MappingException($"Required member '{req.Name}' was not initialized.");
            }

            return bindings.Count > 0
                ? Expression.MemberInit(newExpr, bindings)
                : newExpr;
        }
        finally
        {
            path.Pop();
        }
    }

    private static Expression BuildCollectionMapping(Expression sourceExpr, Type sourceElementType, Type targetElementType, Type targetCollectionType, Stack<(Type, Type)> path)
    {
        var itemParam = Expression.Parameter(sourceElementType, "item");
        // Встраиваем маппинг для элемента, передавая стек
        var mappedItem = BuildMappingBody(itemParam, targetElementType, sourceElementType, path);
        var lambda = Expression.Lambda(mappedItem, itemParam);

        var selectCall = Expression.Call(
            typeof(Enumerable),
            nameof(Enumerable.Select),
            [sourceElementType, targetElementType],
            sourceExpr,
            lambda
        );

        if (targetCollectionType.IsArray)
        {
            return Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.ToArray),
                [targetElementType],
                selectCall
            );
        }

        if (targetCollectionType.IsGenericType && targetCollectionType.GetGenericTypeDefinition() == typeof(List<>))
        {
            return Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.ToList),
                [targetElementType],
                selectCall
            );
        }

        if (targetCollectionType.IsInterface)
        {
            return Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.ToList),
                [targetElementType],
                selectCall
            );
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
            var addRangeMethod = targetCollectionType.GetMethod("AddRange", [typeof(IEnumerable<>).MakeGenericType(targetElementType)]);
            if (addRangeMethod != null)
            {
                return Expression.Call(newCollection, addRangeMethod, selectCall);
            }
            throw new MappingException($"Cannot map collection to type '{targetCollectionType.FullName}' because it has no AddRange method.");
        }

        throw new MappingException($"Cannot map collection to type '{targetCollectionType.FullName}'.");
    }

    private sealed record SuffixFlatteningCandidate(
        Expression Access,
        IReadOnlyList<Expression> NullChecks,
        int Depth,
        string Path);

    private static bool TryGetSourceMemberAccess(
        Type sourceType,
        Expression sourceExpr,
        string targetMemberName,
        out Expression accessExpr)
    {
        // 1. Exact direct match.
        if (TryGetDirectSourceMemberAccess(
                sourceType,
                sourceExpr,
                targetMemberName,
                exactOnly: true,
                out accessExpr))
        {
            return true;
        }

        // 2. Case-insensitive direct match.
        if (TryGetDirectSourceMemberAccess(
                sourceType,
                sourceExpr,
                targetMemberName,
                exactOnly: false,
                out accessExpr))
        {
            return true;
        }

        // 3. Recursive suffix flattening.
        if (TryGetSuffixFlattenedSourceMemberAccess(
                sourceType,
                sourceExpr,
                targetMemberName,
                out accessExpr))
        {
            return true;
        }

        accessExpr = null!;
        return false;
    }

    private static bool TryGetDirectSourceMemberAccess(
        Type sourceType,
        Expression sourceExpr,
        string memberName,
        bool exactOnly,
        out Expression accessExpr)
    {
        accessExpr = null!;

        if (!TryGetDirectSourceMember(sourceType, memberName, exactOnly, out var member))
            return false;

        accessExpr = Expression.MakeMemberAccess(sourceExpr, member);
        return true;
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

    private static bool TryGetSuffixFlattenedSourceMemberAccess(
        Type sourceType,
        Expression sourceExpr,
        string targetMemberName,
        out Expression accessExpr)
    {
        accessExpr = null!;

        var exactCandidates = GetSuffixFlatteningCandidates(
                sourceType,
                sourceExpr,
                targetMemberName,
                exactOnly: true,
                path: string.Empty,
                depth: 0)
            .ToArray();

        var candidates = exactCandidates;

        if (candidates.Length == 0)
        {
            candidates = [.. GetSuffixFlatteningCandidates(
                    sourceType,
                    sourceExpr,
                    targetMemberName,
                    exactOnly: false,
                    path: string.Empty,
                    depth: 0)];
        }

        if (candidates.Length == 0)
            return false;

        var minDepth = candidates.Min(c => c.Depth);

        var bestCandidates = candidates
            .Where(c => c.Depth == minDepth)
            .ToArray();

        if (bestCandidates.Length > 1)
        {
            throw new MappingException(
                $"Ambiguous suffix flattening match for target member '{targetMemberName}'. " +
                $"Candidates: {string.Join("; ", bestCandidates.Select(c => c.Path))}.");
        }

        var best = bestCandidates[0];

        accessExpr = MakeNullSafe(best.Access, best.NullChecks);
        return true;
    }

    private static IEnumerable<SuffixFlatteningCandidate> GetSuffixFlatteningCandidates(
        Type sourceType,
        Expression sourceExpr,
        string remainingName,
        bool exactOnly,
        string path,
        int depth)
    {
        // Если оставшийся суффикс находится напрямую, это самый короткий вариант.
        if (TryGetDirectSourceMember(sourceType, remainingName, exactOnly, out var directMember))
        {
            var directPath = AppendPath(path, directMember.Name);

            yield return new SuffixFlatteningCandidate(
                Expression.MakeMemberAccess(sourceExpr, directMember),
                [],
                depth + 1,
                directPath);

            yield break;
        }

        foreach (var member in GetSourceMembers(sourceType))
        {
            if (!TryGetPrefixSuffix(remainingName, member.Name, exactOnly, out var suffix))
                continue;

            var memberAccess = Expression.MakeMemberAccess(sourceExpr, member);
            var memberType = GetMemberType(member);

            List<Expression> nullChecks = [];

            if (CanBeNull(memberType))
                nullChecks.Add(memberAccess);

            Expression nextExpr = memberAccess;
            Type nextType = memberType;

            var underlyingType = Nullable.GetUnderlyingType(nextType);
            if (underlyingType is not null)
            {
                nextExpr = Expression.Property(nextExpr, "Value");
                nextType = underlyingType;
            }

            var memberPath = AppendPath(path, member.Name);

            foreach (var nested in GetSuffixFlatteningCandidates(
                         nextType,
                         nextExpr,
                         suffix,
                         exactOnly,
                         memberPath,
                         depth + 1))
            {
                List<Expression> combinedNullChecks = [.. nullChecks];
                combinedNullChecks.AddRange(nested.NullChecks);

                yield return nested with
                {
                    NullChecks = combinedNullChecks
                };
            }
        }
    }

    private static bool TryGetPrefixSuffix(
        string remainingName,
        string memberName,
        bool exactOnly,
        out string suffix)
    {
        suffix = string.Empty;

        if (string.IsNullOrEmpty(memberName))
            return false;

        if (remainingName.Length <= memberName.Length)
            return false;

        var comparison = exactOnly
            ? StringComparison.Ordinal
            : StringComparison.OrdinalIgnoreCase;

        if (!remainingName.StartsWith(memberName, comparison))
            return false;

        suffix = remainingName[memberName.Length..]
            .TrimStart('_');

        return suffix.Length > 0;
    }

    private static Expression MakeNullSafe(
        Expression body,
        IReadOnlyList<Expression> nullChecks)
    {
        // Если по пути могут быть null, а конечный член имеет non-nullable value type,
        // то результатом должен быть Nullable<T>, чтобы null был null, а не 0/false/default.
        if (nullChecks.Count > 0 &&
            body.Type.IsValueType &&
            Nullable.GetUnderlyingType(body.Type) is null)
        {
            body = Expression.Convert(
                body,
                typeof(Nullable<>).MakeGenericType(body.Type));
        }

        Expression result = body;

        for (var i = nullChecks.Count - 1; i >= 0; i--)
        {
            var check = nullChecks[i];

            if (!CanBeNull(check.Type))
                continue;

            var isNull = Expression.Equal(
                check,
                Expression.Constant(null, check.Type));

            var defaultValue = Expression.Default(result.Type);

            result = Expression.Condition(
                isNull,
                defaultValue,
                result,
                result.Type);
        }

        return result;
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

        // Свойства предпочтительнее полей, если имя совпадает.
        return properties.Concat(fields)
            .GroupBy(m => m.Name, StringComparer.Ordinal)
            .Select(g => g.OfType<PropertyInfo>().FirstOrDefault() ?? g.First());
    }

    private static PropertyInfo? TryGetPreferredProperty(MemberInfo[] candidates)
    {
        var properties = candidates.OfType<PropertyInfo>().ToArray();
        return properties.Length == 1 ? properties[0] : null;
    }

    private static string AppendPath(string path, string memberName)
    {
        return string.IsNullOrEmpty(path)
            ? memberName
            : $"{path}.{memberName}";
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
        if (setMethod == null) return true;
        var parameters = setMethod.GetParameters();
        if (parameters.Length == 0) return false;
        var lastParam = parameters[^1];
        var modReqs = lastParam.GetRequiredCustomModifiers();
        return modReqs.Any(t => t.Name == "IsExternalInit" && t.Namespace == "System.Runtime.CompilerServices");
    }

    private static Type GetMemberType(MemberInfo member) => member switch
    {
        PropertyInfo p => p.PropertyType,
        FieldInfo f => f.FieldType,
        _ => throw new InvalidOperationException("Unsupported member type")
    };
}