using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

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
        LastOrDefault
    }

    private readonly record struct TargetToken(string Text, CollectionOperation Operation)
    {
        public bool IsOperator => Operation != CollectionOperation.None;
    }

    private sealed record ConventionCandidate(
        CollectionOperation Operation,
        Expression CollectionAccess,
        Type ElementType,
        ParameterExpression? ItemParameter,
        Expression? SelectorBody,
        int Score,
        string Description);

    public static Expression BuildCreationExpression<TSource, TTarget>(ParameterExpression sourceParam)
    {
        var sourceType = typeof(TSource);
        var targetType = typeof(TTarget);
        Stack<(Type Source, Type Target)> path = new();

        var body = BuildMappingBody(sourceParam, targetType, sourceType, path);
        return EnsureType(body, targetType);
    }

    public static Expression BuildAssignmentExpression<TSource, TTarget>(
        ParameterExpression sourceParam,
        ParameterExpression targetParam)
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
        Stack<(Type Source, Type Target)> path = new();

        foreach (var member in members)
        {
            var targetMemberType = GetMemberType(member);

            if (TryGetSourceMemberAccess(
                    sourceType,
                    sourceParam,
                    member.Name,
                    targetMemberType,
                    path,
                    out var accessExpr))
            {
                var mappedExpr = BuildMappingBody(accessExpr, targetMemberType, accessExpr.Type, path);
                var assign = Expression.Assign(
                    Expression.MakeMemberAccess(targetParam, member),
                    EnsureType(mappedExpr, targetMemberType));

                assignments.Add(assign);
            }
        }

        return assignments.Count > 0
            ? Expression.Block(assignments)
            : Expression.Empty();
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
                var allFound = true;

                foreach (var param in parameters)
                {
                    if (TryGetSourceMemberAccess(
                            sourceType,
                            sourceExpr,
                            param.Name!,
                            param.ParameterType,
                            path,
                            out var accessExpr))
                    {
                        var mappedExpr = BuildMappingBody(
                            accessExpr,
                            param.ParameterType,
                            accessExpr.Type,
                            path);

                        dict[param.Name!] = EnsureType(mappedExpr, param.ParameterType);
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
                var args = selectedCtor.GetParameters()
                    .Select(p => paramMap![p.Name!])
                    .ToArray();

                newExpr = Expression.New(selectedCtor, args);
            }

            HashSet<string> initializedMembers =
            [
                .. selectedCtor.GetParameters().Select(p => p.Name!)
            ];

            var targetMembers = targetType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanWrite)
                .Cast<MemberInfo>()
                .Concat(targetType.GetFields(BindingFlags.Public | BindingFlags.Instance)
                    .Where(f => !f.IsInitOnly && !f.IsLiteral))
                .ToArray();

            var requiredMembers = targetMembers
                .Where(m => m.GetCustomAttribute<RequiredMemberAttribute>() != null)
                .ToArray();

            List<MemberBinding> bindings = [];

            foreach (var member in targetMembers)
            {
                if (initializedMembers.Contains(member.Name))
                    continue;

                if (TryGetSourceMemberAccess(
                        sourceType,
                        sourceExpr,
                        member.Name,
                        GetMemberType(member),
                        path,
                        out var accessExpr))
                {
                    var targetMemberType = GetMemberType(member);
                    var mappedExpr = BuildMappingBody(accessExpr, targetMemberType, accessExpr.Type, path);
                    var converted = EnsureType(mappedExpr, targetMemberType);
                    var binding = Expression.Bind(member, converted);

                    bindings.Add(binding);
                    initializedMembers.Add(member.Name);
                }
                else
                {
                    if (requiredMembers.Contains(member))
                    {
                        throw new MappingException(
                            $"Required member '{member.Name}' cannot be mapped from source type '{sourceType.FullName}'.");
                    }
                }
            }

            if (selectedCtor.GetCustomAttribute<SetsRequiredMembersAttribute>() == null)
            {
                foreach (var req in requiredMembers.Where(r => !initializedMembers.Contains(r.Name)))
                {
                    throw new MappingException($"Required member '{req.Name}' was not initialized.");
                }
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
        Type targetType,
        Stack<(Type Source, Type Target)> path,
        out Expression accessExpr)
    {
        if (TryGetBasicSourceMemberAccess(sourceType, sourceExpr, targetMemberName, out accessExpr))
            return true;

        if (TryGetCollectionConventionAccess(
                sourceType,
                sourceExpr,
                targetMemberName,
                targetType,
                path,
                out accessExpr))
        {
            return true;
        }

        accessExpr = null!;
        return false;
    }

    private static bool TryGetBasicSourceMemberAccess(
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
            candidates =
            [
                .. GetSuffixFlatteningCandidates(
                    sourceType,
                    sourceExpr,
                    targetMemberName,
                    exactOnly: false,
                    path: string.Empty,
                    depth: 0)
            ];
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

        suffix = remainingName[memberName.Length..].TrimStart('_');
        return suffix.Length > 0;
    }

    private static Expression MakeNullSafe(
        Expression body,
        IReadOnlyList<Expression> nullChecks)
    {
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

    private static bool TryGetCollectionConventionAccess(
        Type sourceType,
        Expression sourceExpr,
        string targetMemberName,
        Type targetType,
        Stack<(Type Source, Type Target)> path,
        out Expression accessExpr)
    {
        accessExpr = null!;

        var tokens = TokenizeTargetName(targetMemberName);
        if (tokens.All(t => !t.IsOperator))
            return false;

        List<ConventionCandidate> candidates = [];

        for (var opIndex = 0; opIndex < tokens.Count; opIndex++)
        {
            var opToken = tokens[opIndex];
            if (!opToken.IsOperator)
                continue;

            var op = opToken.Operation;

            switch (op)
            {
                case CollectionOperation.Count:
                case CollectionOperation.Any:
                    if (opIndex == tokens.Count - 1)
                    {
                        var before = tokens.GetRange(0, opIndex);
                        if (before.Count > 0 &&
                            TryResolveCollection(sourceType, sourceExpr, before, out var collectionAccess, out var elementType))
                        {
                            if (IsCompatibleAggregate(op, elementType, targetType))
                            {
                                candidates.Add(new ConventionCandidate(
                                    op,
                                    collectionAccess,
                                    elementType,
                                    null,
                                    null,
                                    GetCandidateScore(op, false, before.Count, 0),
                                    CombineTokens(before)));
                            }
                        }
                    }
                    break;

                case CollectionOperation.Sum:
                case CollectionOperation.Average:
                case CollectionOperation.Max:
                case CollectionOperation.Min:
                    if (opIndex == tokens.Count - 1)
                    {
                        var before = tokens.GetRange(0, opIndex);
                        AddAggregateCandidates(sourceType, sourceExpr, targetType, op, before, candidates);
                    }
                    break;

                case CollectionOperation.First:
                case CollectionOperation.FirstOrDefault:
                case CollectionOperation.Last:
                case CollectionOperation.LastOrDefault:
                    var beforeFirstLast = tokens.GetRange(0, opIndex);
                    var after = tokens.GetRange(opIndex + 1, tokens.Count - opIndex - 1);
                    AddFirstLastCandidates(sourceType, sourceExpr, targetType, op, beforeFirstLast, after, candidates);
                    break;
            }
        }

        if (candidates.Count == 0)
            return false;

        var best = candidates
            .OrderByDescending(c => c.Score)
            .First();

        accessExpr = BuildConventionExpression(best, targetType, path);
        return true;
    }

    private static void AddAggregateCandidates(
        Type sourceType,
        Expression sourceExpr,
        Type targetType,
        CollectionOperation op,
        List<TargetToken> before,
        List<ConventionCandidate> candidates)
    {
        for (var split = before.Count; split >= 1; split--)
        {
            var collectionTokens = before.GetRange(0, split);
            var selectorTokens = before.GetRange(split, before.Count - split);

            if (!TryResolveCollection(sourceType, sourceExpr, collectionTokens, out var collectionAccess, out var elementType))
                continue;

            if (selectorTokens.Count == 0)
            {
                if (IsCompatibleAggregate(op, elementType, targetType))
                {
                    candidates.Add(new ConventionCandidate(
                        op,
                        collectionAccess,
                        elementType,
                        null,
                        null,
                        GetCandidateScore(op, false, collectionTokens.Count, 0),
                        CombineTokens(collectionTokens)));
                }
            }
            else
            {
                if (TryResolveSelector(elementType, selectorTokens, out var itemParam, out var selectorBody) &&
                    IsCompatibleAggregate(op, selectorBody.Type, targetType))
                {
                    candidates.Add(new ConventionCandidate(
                        op,
                        collectionAccess,
                        elementType,
                        itemParam,
                        selectorBody,
                        GetCandidateScore(op, true, collectionTokens.Count, selectorTokens.Count),
                        $"{CombineTokens(collectionTokens)} => {CombineTokens(selectorTokens)}"));
                }
            }
        }
    }

    private static void AddFirstLastCandidates(
        Type sourceType,
        Expression sourceExpr,
        Type targetType,
        CollectionOperation op,
        List<TargetToken> before,
        List<TargetToken> after,
        List<ConventionCandidate> candidates)
    {
        if (before.Count == 0)
            return;

        for (var split = 1; split <= before.Count; split++)
        {
            var collectionTokens = before.GetRange(0, split);

            List<TargetToken> selectorTokens = new();
            selectorTokens.AddRange(before.GetRange(split, before.Count - split));
            selectorTokens.AddRange(after);

            if (!TryResolveCollection(sourceType, sourceExpr, collectionTokens, out var collectionAccess, out var elementType))
                continue;

            if (selectorTokens.Count == 0)
            {
                if (IsCompatibleTerminal(elementType, targetType))
                {
                    candidates.Add(new ConventionCandidate(
                        op,
                        collectionAccess,
                        elementType,
                        null,
                        null,
                        GetCandidateScore(op, false, collectionTokens.Count, 0),
                        CombineTokens(collectionTokens)));
                }
            }
            else
            {
                if (TryResolveSelector(elementType, selectorTokens, out var itemParam, out var selectorBody) &&
                    IsCompatibleTerminal(selectorBody.Type, targetType))
                {
                    candidates.Add(new ConventionCandidate(
                        op,
                        collectionAccess,
                        elementType,
                        itemParam,
                        selectorBody,
                        GetCandidateScore(op, true, collectionTokens.Count, selectorTokens.Count),
                        $"{CombineTokens(collectionTokens)} => {CombineTokens(selectorTokens)}"));
                }
            }
        }
    }

    private static bool TryResolveCollection(
        Type sourceType,
        Expression sourceExpr,
        List<TargetToken> segments,
        out Expression collectionAccess,
        out Type elementType)
    {
        collectionAccess = null!;
        elementType = null!;

        var pathName = CombineTokens(segments);
        if (string.IsNullOrEmpty(pathName))
            return false;

        if (!TryGetBasicSourceMemberAccess(sourceType, sourceExpr, pathName, out collectionAccess))
            return false;

        if (!IsCollectionType(collectionAccess.Type, out var element) || element is null)
            return false;

        elementType = element;
        return true;
    }

    private static bool TryResolveSelector(
        Type elementType,
        List<TargetToken> selectorSegments,
        out ParameterExpression itemParam,
        out Expression selectorBody)
    {
        itemParam = Expression.Parameter(elementType, "item");
        selectorBody = null!;

        var selectorName = CombineTokens(selectorSegments);
        if (string.IsNullOrEmpty(selectorName))
            return false;

        if (!TryGetBasicSourceMemberAccess(elementType, itemParam, selectorName, out selectorBody))
            return false;

        return true;
    }

    private static Expression BuildConventionExpression(
        ConventionCandidate candidate,
        Type targetType,
        Stack<(Type Source, Type Target)> path)
    {
        Expression sequence;
        Type projectedType;

        if (candidate.SelectorBody is null)
        {
            sequence = candidate.CollectionAccess;
            projectedType = candidate.ElementType;
        }
        else
        {
            Expression body = candidate.SelectorBody;

            if (CanBeNull(candidate.ElementType))
            {
                body = Expression.Condition(
                    Expression.Equal(candidate.ItemParameter!, Expression.Constant(null, candidate.ElementType)),
                    Expression.Default(body.Type),
                    body,
                    body.Type);
            }

            var lambda = Expression.Lambda(body, candidate.ItemParameter!);

            sequence = Expression.Call(
                typeof(Enumerable),
                nameof(Enumerable.Select),
                [candidate.ElementType, body.Type],
                candidate.CollectionAccess,
                lambda);

            projectedType = body.Type;
        }

        Expression rawCall = candidate.Operation switch
        {
            CollectionOperation.Count =>
                CallEnumerable(nameof(Enumerable.Count), candidate.ElementType, candidate.CollectionAccess),

            CollectionOperation.Any =>
                CallEnumerable(nameof(Enumerable.Any), candidate.ElementType, candidate.CollectionAccess),

            CollectionOperation.Sum =>
                CallAggregate(candidate.Operation, projectedType, sequence),

            CollectionOperation.Average =>
                CallAggregate(candidate.Operation, projectedType, sequence),

            CollectionOperation.Max =>
                CallAggregate(candidate.Operation, projectedType, sequence),

            CollectionOperation.Min =>
                CallAggregate(candidate.Operation, projectedType, sequence),

            CollectionOperation.First =>
                CallEnumerable(nameof(Enumerable.First), projectedType, sequence),

            CollectionOperation.FirstOrDefault =>
                CallEnumerable(nameof(Enumerable.FirstOrDefault), projectedType, sequence),

            CollectionOperation.Last =>
                CallEnumerable(nameof(Enumerable.Last), projectedType, sequence),

            CollectionOperation.LastOrDefault =>
                CallEnumerable(nameof(Enumerable.LastOrDefault), projectedType, sequence),

            _ => throw new MappingException($"Unsupported collection operation '{candidate.Operation}'.")
        };

        var mapped = BuildMappingBody(rawCall, targetType, rawCall.Type, path);
        mapped = EnsureType(mapped, targetType);

        var defaultTarget = Expression.Default(targetType);
        Expression result = mapped;

        var protectFromEmpty = candidate.Operation != CollectionOperation.Count &&
                               candidate.Operation != CollectionOperation.Any;

        if (protectFromEmpty)
        {
            var any = CallEnumerable(nameof(Enumerable.Any), candidate.ElementType, candidate.CollectionAccess);

            result = Expression.Condition(
                any,
                result,
                defaultTarget,
                targetType);
        }

        if (CanBeNull(candidate.CollectionAccess.Type))
        {
            var nullCheck = Expression.Equal(
                candidate.CollectionAccess,
                Expression.Constant(null, candidate.CollectionAccess.Type));

            result = Expression.Condition(
                nullCheck,
                defaultTarget,
                result,
                targetType);
        }

        return result;
    }

    private static MethodCallExpression CallEnumerable(string methodName, Type elementType, Expression source)
    {
        var method = FindEnumerableMethod(methodName, elementType, source);
        return Expression.Call(method, source);
    }

    private static MethodCallExpression CallAggregate(CollectionOperation operation, Type elementType, Expression sequence)
    {
        var methodName = operation switch
        {
            CollectionOperation.Sum => nameof(Enumerable.Sum),
            CollectionOperation.Average => nameof(Enumerable.Average),
            CollectionOperation.Max => nameof(Enumerable.Max),
            CollectionOperation.Min => nameof(Enumerable.Min),
            _ => throw new MappingException($"Unsupported aggregate operation '{operation}'.")
        };

        var method = FindEnumerableMethod(methodName, elementType, sequence);
        return Expression.Call(method, sequence);
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

    private static List<TargetToken> TokenizeTargetName(string name)
    {
        var words = SplitPascalWords(name);
        List<TargetToken> tokens = new();

        var i = 0;
        while (i < words.Count)
        {
            var matched = false;

            for (var len = Math.Min(4, words.Count - i); len >= 1; len--)
            {
                var combined = string.Concat(words.Skip(i).Take(len));
                
                if (Enum.TryParse<CollectionOperation>(combined, ignoreCase: true, out var Operation))
                {
                    tokens.Add(new TargetToken(Operation.ToString(), Operation));
                    i += len;
                    matched = true;
                    break;
                }
            }

            if (!matched)
            {
                tokens.Add(new TargetToken(words[i], CollectionOperation.None));
                i++;
            }
        }

        return tokens;
    }

    private static List<string> SplitPascalWords(string name)
    {
        List<string> words = new();
        StringBuilder current = new();
        char? previous = null;

        foreach (var c in name)
        {
            if (c == '_')
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }

                previous = null;
                continue;
            }

            if (char.IsUpper(c) && current.Length > 0 && previous.HasValue && !char.IsUpper(previous.Value))
            {
                words.Add(current.ToString());
                current.Clear();
            }

            current.Append(c);
            previous = c;
        }

        if (current.Length > 0)
            words.Add(current.ToString());

        return words;
    }

    private static string CombineTokens(IEnumerable<TargetToken> tokens)
    {
        return string.Concat(tokens.Where(t => !t.IsOperator).Select(t => t.Text));
    }

    private static int GetCandidateScore(
        CollectionOperation operation,
        bool hasSelector,
        int collectionSegmentCount,
        int selectorSegmentCount)
    {
        var score = operation switch
        {
            CollectionOperation.Count or CollectionOperation.Any => 900,
            CollectionOperation.Sum or CollectionOperation.Average or CollectionOperation.Max or CollectionOperation.Min => 800,
            CollectionOperation.First or CollectionOperation.FirstOrDefault or CollectionOperation.Last or CollectionOperation.LastOrDefault => 700,
            _ => 500
        };

        if (!hasSelector)
            score += 20;

        score += collectionSegmentCount * 5;
        score -= selectorSegmentCount;

        return score;
    }

    private static bool IsCompatibleAggregate(CollectionOperation operation, Type sourceType, Type targetType)
    {
        var sourceUnder = Nullable.GetUnderlyingType(sourceType) ?? sourceType;
        var targetUnder = Nullable.GetUnderlyingType(targetType) ?? targetType;

        switch (operation)
        {
            case CollectionOperation.Count:
                return IsNumericType(targetUnder);

            case CollectionOperation.Any:
                return targetUnder == typeof(bool);

            case CollectionOperation.Sum:
                return IsNumericType(sourceUnder) && IsNumericType(targetUnder);

            case CollectionOperation.Average:
                return IsNumericType(sourceUnder) && IsNumericType(targetUnder);

            case CollectionOperation.Max:
            case CollectionOperation.Min:
                if (targetUnder.IsAssignableFrom(sourceUnder))
                    return true;

                if (sourceUnder == targetUnder)
                    return true;

                if (IsNumericType(sourceUnder) && IsNumericType(targetUnder))
                    return true;

                return false;

            default:
                return false;
        }
    }

    private static bool IsCompatibleTerminal(Type sourceType, Type targetType)
    {
        if (targetType.IsAssignableFrom(sourceType))
            return true;

        var sourceUnder = Nullable.GetUnderlyingType(sourceType) ?? sourceType;
        var targetUnder = Nullable.GetUnderlyingType(targetType) ?? targetType;

        if (targetUnder.IsAssignableFrom(sourceUnder))
            return true;

        if (IsSimpleType(targetUnder) && IsSimpleType(sourceUnder))
            return true;

        if (!IsSimpleType(targetUnder) && !IsSimpleType(sourceUnder))
            return true;

        return false;
    }

    private static bool IsNumericType(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;

        return type == typeof(byte) ||
               type == typeof(sbyte) ||
               type == typeof(short) ||
               type == typeof(ushort) ||
               type == typeof(int) ||
               type == typeof(uint) ||
               type == typeof(long) ||
               type == typeof(ulong) ||
               type == typeof(float) ||
               type == typeof(double) ||
               type == typeof(decimal);
    }

    private static Expression EnsureType(Expression expression, Type type)
    {
        if (expression.Type == type)
            return expression;

        return Expression.Convert(expression, type);
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

    private sealed record SuffixFlatteningCandidate(
        Expression Access,
        IReadOnlyList<Expression> NullChecks,
        int Depth,
        string Path);
}