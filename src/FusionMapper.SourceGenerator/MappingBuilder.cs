using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FusionMapper.SourceGenerator;

class MappingBuilder(Compilation compilation)
{
    private readonly ConcurrentStack<(ITypeSymbol Source, ITypeSymbol Target)> path = new();
    private readonly ConcurrentDictionary<ITypeSymbol, TypeModel> typeModelCache = new (SymbolEqualityComparer.IncludeNullability);

    private readonly ConcurrentDictionary<ITypeSymbol, ImmutableArray<ReadableMember>> readableMembersCache =
    new(SymbolEqualityComparer.IncludeNullability);

    private readonly ConcurrentDictionary<ITypeSymbol, ImmutableArray<ReadableMember>> readableMembersByLengthCache =
        new(SymbolEqualityComparer.IncludeNullability);

    private readonly ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<TargetMemberInfo>> targetMembersCache =
        new(SymbolEqualityComparer.IncludeNullability);

    private readonly ConcurrentDictionary<INamedTypeSymbol, ImmutableArray<string>> requiredMembersCache =
        new(SymbolEqualityComparer.IncludeNullability);

    private readonly ConcurrentDictionary<ITypeSymbol, ITypeSymbol?> collectionElementCache =
        new(SymbolEqualityComparer.IncludeNullability);

    private readonly ConcurrentDictionary<(ITypeSymbol Target, ITypeSymbol Element), CollectionPlan> collectionPlanCache =
        new(SymbolPairComparer.Instance);

    private readonly ConcurrentDictionary<ITypeSymbol, bool> hasCountPropertyCache =
        new(SymbolEqualityComparer.IncludeNullability);

    private readonly ConcurrentDictionary<(ITypeSymbol Source, ITypeSymbol Target), Mapping> mappingsCache =
        new(SymbolPairComparer.Instance);

    private readonly ConcurrentDictionary<(ITypeSymbol Source, ITypeSymbol Target), byte> failedMappingsCache =
        new(SymbolPairComparer.Instance);

    private readonly INamedTypeSymbol enumerableOfT =
        compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T);

    private readonly INamedTypeSymbol? listOfT =
        compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");

    private readonly INamedTypeSymbol int32Type =
        compilation.GetSpecialType(SpecialType.System_Int32);

    private readonly INamedTypeSymbol boolType =
        compilation.GetSpecialType(SpecialType.System_Boolean);

    public Mapping Build(ITypeSymbol sourceSymbol, ITypeSymbol targetSymbol)
    {
        var key = (sourceSymbol, targetSymbol);

        if (mappingsCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (failedMappingsCache.ContainsKey(key))
        {
            throw new MappingGenerationException(
                $"Cannot map '{sourceSymbol.ToDisplayString()}' to '{targetSymbol.ToDisplayString()}'.");
        }

        try
        {
            var mapping = ResolveMapping(sourceSymbol, targetSymbol);
            mappingsCache[key] = mapping;
            return mapping;
        }
        catch (MappingGenerationException ex) when (ex is not RecursiveMappingGenerationException)
        {
            failedMappingsCache.TryAdd(key, 0);
            throw;
        }
    }

    private Mapping ResolveMapping(ITypeSymbol source, ITypeSymbol target)
    {
        using (Push(source, target)) return ResolveCore(source, target);
    }

    private Mapping ResolveCore(ITypeSymbol source, ITypeSymbol target)
    {
        if (source.IsAnonymousType || target.IsAnonymousType)
        {
            throw new MappingGenerationException(
                "Anonymous types are not supported by generated compile-time mapping.");
        }

        if (source is IArrayTypeSymbol sourceArray &&
            target is IArrayTypeSymbol targetArray)
        {
            return ResolveCollectionMapping(
                source,
                target,
                sourceArray.ElementType,
                targetArray.ElementType);
        }

        if (SymbolEqualityComparer.Default.Equals(source, target))
        {
            return CreateAssignMapping(source, target, AssignmentKind.SameType);
        }

        var conversion = compilation.ClassifyConversion(source, target);

        if (conversion.Exists && conversion.IsImplicit)
        {
            return CreateAssignMapping(source, target, AssignmentKind.ImplicitConversion);
        }

        var sourceCore = UnwrapNullable(source);
        var targetCore = UnwrapNullable(target);

        if (sourceCore.TypeKind == TypeKind.Enum && targetCore.SpecialType == SpecialType.System_String)
        {
            return CreateAssignMapping(source, target, AssignmentKind.EnumToString);
        }

        if (sourceCore.SpecialType == SpecialType.System_String && targetCore.TypeKind == TypeKind.Enum)
        {
            return CreateAssignMapping(source, target, AssignmentKind.StringToEnum);
        }

        if (IsCollection(source, out var sourceElement) && IsCollection(target, out var targetElement))
        {
            return ResolveCollectionMapping(source, target, sourceElement!, targetElement!);
        }

        if (conversion.Exists && conversion.IsExplicit)
        {
            return CreateAssignMapping(source, target, AssignmentKind.ExplicitCast);
        }

        if (target is INamedTypeSymbol namedTarget && !IsSimpleType(namedTarget))
        {
            return ResolveObjectMapping(source, namedTarget);
        }

        throw new MappingGenerationException(
            $"Cannot map '{source.ToDisplayString()}' to '{target.ToDisplayString()}'.");
    }

    private AssignMapping CreateAssignMapping(
    ITypeSymbol source,
    ITypeSymbol target,
    AssignmentKind kind)
    {
        return new AssignMapping
        {
            SourceType = typeModelCache.GetOrAdd(source, TypeModel.Create),
            TargetType = typeModelCache.GetOrAdd(target, TypeModel.Create),
            Kind = kind
        };
    }

    private static bool IsKnownCollectionInterfaceSymbol(ITypeSymbol type)
    {
        if (type is not INamedTypeSymbol named || !named.IsGenericType)
        {
            return false;
        }

        var constructedFrom = named.ConstructedFrom.ToDisplayString();

        return constructedFrom is
            "System.Collections.Generic.IEnumerable<T>" or
            "System.Collections.Generic.ICollection<T>" or
            "System.Collections.Generic.IList<T>" or
            "System.Collections.Generic.IReadOnlyCollection<T>" or
            "System.Collections.Generic.IReadOnlyList<T>";
    }

    private ObjectMapping ResolveObjectMapping(
        ITypeSymbol source,
        INamedTypeSymbol target)
    {
        var bindings = ImmutableArray.CreateBuilder<MemberBinding>();
        var assignableMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in GetTargetMembers(target))
        {
            if (!TryResolveMemberValue(
                    source,
                    member.Name,
                    member.Type,
                    out var sourcePath,
                    out var valueMapping))
            {
                continue;
            }

            // Если same-type коллекция поддерживает Clear/Add/AddRange,
            // превращаем AssignMapping в CollectionMapping, чтобы existing mutation работал.
            if (valueMapping is AssignMapping &&
                IsCollection(member.Type, out var targetElement) &&
                IsCollection(sourcePath.FinalType, out var sourceElement))
            {
                var plan = GetCollectionPlan(member.Type, targetElement!);

                if (!plan.IsArray && plan.Mutation != CollectionMutationKind.None)
                {
                    valueMapping = ResolveCollectionMapping(
                        sourcePath.FinalType,
                        member.Type,
                        sourceElement!,
                        targetElement!);
                }
            }

            var mutationKind = DetermineMutationKind(member, valueMapping);

            bindings.Add(new MemberBinding
            {
                TargetMemberName = member.Name,
                Source = MaterializePath(sourcePath),
                Value = valueMapping,
                CanWrite = member.CanWrite,
                MutationKind = mutationKind
            });

            if (member.CanWrite)
            {
                assignableMembers.Add(member.Name);
            }
        }

        var requiredMembers = GetRequiredMemberNames(target).ToImmutableArray();

        var constructor = SelectConstructor(
            source,
            target,
            assignableMembers,
            requiredMembers);

        var creationMembers = bindings
            .Where(member => member.CanWrite)
            .Where(member => !constructor.AssignedMemberNames.Contains(member.TargetMemberName))
            .ToImmutableArray();

        return new ObjectMapping
        {
            SourceType = typeModelCache.GetOrAdd(source, TypeModel.Create),
            TargetType = typeModelCache.GetOrAdd(target, TypeModel.Create),
            Constructor = constructor,
            Members = bindings.ToImmutable(),
            CreationMembers = creationMembers
        };
    }

    private MemberMutationKind DetermineMutationKind(
        TargetMemberInfo member,
        Mapping value)
    {
        if (member.IsInitOnly)
        {
            return MemberMutationKind.Skip;
        }

        if (value is CollectionMapping collection)
        {
            // Существующие массивы не мутируем и не заменяем.
            if (collection.Plan.IsArray)
            {
                return MemberMutationKind.Skip;
            }

            // Если коллекцию можно очистить и заполнить — мутируем её.
            if (member.CanRead && collection.Plan.Mutation != CollectionMutationKind.None)
            {
                return MemberMutationKind.MutateCollection;
            }

            // Если читать нельзя, но писать можно, можно только назначить заново.
            if (!member.CanRead && member.CanWrite)
            {
                return MemberMutationKind.Assign;
            }

            // Остальные существующие коллекции пропускаем.
            return MemberMutationKind.Skip;
        }

        // Если это AssignMapping (например, SameType для IEnumerable<int> -> IEnumerable<int>),
        // но целевой член — коллекция, то при existing mapping мы не перезаписываем ссылку.
        // Это защищает от потери ссылки на существующую коллекцию,
        // особенно для интерфейсов вроде IEnumerable<T>, которые нельзя безопасно мутировать.
        if (value is AssignMapping && IsCollection(member.Type, out _))
        {
            return MemberMutationKind.Skip;
        }

        if (member.CanRead &&
            value is ObjectMapping nestedObject &&
            !member.IsValueType &&
            nestedObject.TargetType.IsReference)
        {
            return MemberMutationKind.MutateObject;
        }

        if (member.CanWrite)
        {
            return MemberMutationKind.Assign;
        }

        return MemberMutationKind.Skip;
    }


    private SelectedConstructor SelectConstructor(
    ITypeSymbol source,
    INamedTypeSymbol target,
    ISet<string> assignableMembers,
    ImmutableArray<string> requiredMembers)
    {
        var candidates = ImmutableArray.CreateBuilder<SelectedConstructor>();

        foreach (var constructor in target.InstanceConstructors
                     .Where(c => c.DeclaredAccessibility == Accessibility.Public))
        {
            if (TryBuildConstructor(
                    source,
                    constructor,
                    assignableMembers,
                    requiredMembers,
                    out var selected))
            {
                candidates.Add(selected);
            }
        }

        if (target.IsValueType && requiredMembers.All(assignableMembers.Contains))
        {
            candidates.Add(new SelectedConstructor
            {
                Arguments = [],
                AssignedMemberNames = []
            });
        }

        if (candidates
            .OrderByDescending(c => c.Arguments.Count(a => a.IsMapped))
            .ThenByDescending(c => c.Arguments.Length)
            .Take(1)
            .ToArray()  
            is [{ } best ])
        {
            return best;

        }

        throw new NoSuitableConstructorException(
            $"No suitable constructor or required members are not mapped for type '{target.ToDisplayString()}'.");
    }

    private bool TryBuildConstructor(
    ITypeSymbol source,
    IMethodSymbol constructor,
    ISet<string> assignableMembers,
    ImmutableArray<string> requiredMembers,
    out SelectedConstructor result)
    {
        result = default!;

        var arguments = ImmutableArray.CreateBuilder<ConstructorArgument>(constructor.Parameters.Length);
        var assignedNames = ImmutableHashSet.CreateBuilder<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in constructor.Parameters)
        {
            if (parameter.Name is { Length: > 0 } parameterName &&
                TryResolveMemberValue(
                    source,
                    parameterName,
                    parameter.Type,
                    out var sourcePath,
                    out var valueMapping))
            {
                arguments.Add(new ConstructorArgument
                {
                    ParameterType = typeModelCache.GetOrAdd(parameter.Type, TypeModel.Create),
                    IsMapped = true,
                    Source = MaterializePath(sourcePath),
                    Value = valueMapping
                });

                assignedNames.Add(parameterName);
                continue;
            }

            var parameterModel = typeModelCache.GetOrAdd(parameter.Type, TypeModel.Create);

            if (CanUseNullForUnmappedConstructorParameter(parameterModel))
            {
                arguments.Add(new ConstructorArgument
                {
                    ParameterType = parameterModel,
                    IsMapped = false,
                    Source = null,
                    Value = null
                });

                if (parameter.Name is { Length: > 0 } fallbackName)
                {
                    assignedNames.Add(fallbackName);
                }

                continue;
            }

            return false;
        }

        var setsRequiredMembers = constructor
            .GetAttributes()
            .Any(a => a.AttributeClass?.ToDisplayString()
                == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute");

        if (!setsRequiredMembers)
        {
            var unassignedRequired = requiredMembers
                .Where(required =>
                    !assignableMembers.Contains(required) &&
                    !assignedNames.Contains(required))
                .ToList();

            if (unassignedRequired.Count > 0)
            {
                return false;
            }
        }

        result = new SelectedConstructor
        {
            Arguments = arguments.ToImmutable(),
            AssignedMemberNames = assignedNames.ToImmutable()
        };

        return true;
    }

    private CollectionMapping ResolveCollectionMapping(
    ITypeSymbol source,
    ITypeSymbol target,
    ITypeSymbol sourceElement,
    ITypeSymbol targetElement)
    {
        var elementMapping = ResolveMapping(sourceElement, targetElement);

        return new CollectionMapping
        {
            SourceType = typeModelCache.GetOrAdd(source, TypeModel.Create),
            TargetType = typeModelCache.GetOrAdd(target, TypeModel.Create),
            ElementTypeName = typeModelCache.GetOrAdd(targetElement, TypeModel.Create),
            ElementMapping = elementMapping,
            Plan = GetCollectionPlan(target, targetElement)
        };
    }

    private CollectionPlan GetCollectionPlan(ITypeSymbol target, ITypeSymbol elementType)
    {
        return collectionPlanCache.GetOrAdd(
            (target, elementType),
            key => this.BuildCollectionPlan(key.Target, key.Element));    
    }

    private CollectionPlan BuildCollectionPlan(
    ITypeSymbol target,
    ITypeSymbol elementType)
    {
        var isArray = target is IArrayTypeSymbol;

        var isGenericList = target is INamedTypeSymbol
        {
            IsGenericType: true
        } genericList && SymbolEqualityComparer.Default.Equals(
            genericList.ConstructedFrom,listOfT);

        var isKnownCollectionInterface = IsKnownCollectionInterfaceSymbol(target);

        var hasClear = HasPublicInstanceMethod(target, "Clear", parameterCount: 0);
        var hasAdd = HasPublicInstanceMethod(target, "Add", parameterCount: 1);
        var hasAddRange = HasPublicInstanceMethod(target, "AddRange", parameterCount: 1);

        var hasParameterlessConstructor = target is INamedTypeSymbol namedTarget &&
            namedTarget.InstanceConstructors.Any(c =>
                c.DeclaredAccessibility == Accessibility.Public &&
                c.Parameters.Length == 0);

        var enumerableOfElement = compilation
            .GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T)
            .Construct(elementType);

        var hasEnumerableConstructor = target is INamedTypeSymbol named &&
            named.InstanceConstructors.Any(c =>
                c.DeclaredAccessibility == Accessibility.Public &&
                c.Parameters.Length == 1 &&
                compilation.ClassifyConversion(enumerableOfElement, c.Parameters[0].Type).IsImplicit);

        var mutation = CollectionMutationKind.None;

        if (!isArray)
        {
            if (hasClear && hasAddRange)
            {
                mutation = CollectionMutationKind.ClearAddRange;
            }
            else if (hasClear && hasAdd)
            {
                mutation = CollectionMutationKind.ClearAdd;
            }
        }

        var methodBodyCreation = ChooseMethodBodyCollectionCreation(
            isArray,
            isGenericList,
            isKnownCollectionInterface,
            hasAdd,
            hasAddRange,
            hasEnumerableConstructor);

        var expressionTreeCreation = ChooseExpressionTreeCollectionCreation(
            isArray,
            isGenericList,
            isKnownCollectionInterface,
            hasEnumerableConstructor);

        return new CollectionPlan
        {
            IsArray = isArray,
            Mutation = mutation,
            MethodBodyCreation = methodBodyCreation,
            ExpressionTreeCreation = expressionTreeCreation
        };
    }

    private static CollectionCreationKind ChooseMethodBodyCollectionCreation(
    bool isArray,
    bool isGenericList,
    bool isKnownCollectionInterface,
    bool hasAdd,
    bool hasAddRange,
    bool hasEnumerableConstructor)
    {
        if (isArray || isGenericList || isKnownCollectionInterface || hasAdd)
        {
            return CollectionCreationKind.CollectionExpression;
        }

        if (hasAddRange)
        {
            return CollectionCreationKind.AddRangeClosure;
        }

        if (hasEnumerableConstructor)
        {
            return CollectionCreationKind.EnumerableConstructor;
        }

        return CollectionCreationKind.Unsupported;
    }

    private static CollectionCreationKind ChooseExpressionTreeCollectionCreation(
    bool isArray,
    bool isGenericList,
    bool isKnownCollectionInterface,
    bool hasEnumerableConstructor)
    {
        if (isArray)
        {
            return CollectionCreationKind.Array;
        }

        if (isGenericList || isKnownCollectionInterface)
        {
            return CollectionCreationKind.List;
        }

        if (hasEnumerableConstructor)
        {
            return CollectionCreationKind.EnumerableConstructor;
        }

        // Раньше здесь мог возвращаться ParameterlessEmpty.
        // Но для projection это тихо теряет данные.
        // Лучше запретить unsupported-коллекции в expression tree.
        return CollectionCreationKind.Unsupported;
    }

    private static bool HasPublicInstanceMethod(
    ITypeSymbol type,
    string name,
    int parameterCount)
    {
        return type
            .GetMembers(name)
            .OfType<IMethodSymbol>()
            .Any(method =>
                !method.IsStatic &&
                method.DeclaredAccessibility == Accessibility.Public &&
                method.Parameters.Length == parameterCount);
    }

    private bool TryResolveMemberValue(
    ITypeSymbol sourceType,
    string targetMemberName,
    ITypeSymbol targetMemberType,
    out ResolvedPath sourcePath,
    out Mapping valueMapping)
    {
        sourcePath = default!;
        valueMapping = default!;

        // 1. Если есть точное совпадение имени source-члена,
        // оно должно победить.
        if (TryResolveExactOrCaseInsensitiveSourcePath(
                sourceType,
                targetMemberName,
                out var exactPath) &&
            TryResolveMapping(exactPath.FinalType, targetMemberType, out var exactMapping))
        {
            sourcePath = exactPath;
            valueMapping = exactMapping;
            return true;
        }

        // 2. Агрегаты: ItemsCount, ItemsAny, ItemsValueSum,
        // ItemsNameFirstOrDefault и т.д.
        if (TryResolveAggregate(
                sourceType,
                targetMemberName,
                targetMemberType,
                out var aggregatePath,
                out var aggregateMapping))
        {
            sourcePath = aggregatePath;
            valueMapping = aggregateMapping;
            return true;
        }

        // 3. Обычный flattening.
        if (TryResolveSourcePath(sourceType, targetMemberName, out var flattenedPath) &&
            TryResolveMapping(flattenedPath.FinalType, targetMemberType, out var flattenedMapping))
        {
            sourcePath = flattenedPath;
            valueMapping = flattenedMapping;
            return true;
        }

        return false;
    }

    private bool TryResolveExactOrCaseInsensitiveSourcePath(
    ITypeSymbol sourceType,
    string suffix,
    out ResolvedPath path)
    {
        path = default!;

        if (string.IsNullOrEmpty(suffix))
        {
            return false;
        }

        var cleanSuffix = suffix.TrimStart('_');
        var members = GetReadableMembers(sourceType);

        var exact = members.FirstOrDefault(m => m.Name == cleanSuffix || m.Name == suffix);

        if (exact is { Name: { }, Type: { } exactType })
        {
            path = new ResolvedPath(
                [new ResolvedSegment(exact.Name, exactType)],
                exactType);

            return true;
        }

        var caseInsensitive = members.FirstOrDefault(m =>
            string.Equals(m.Name, cleanSuffix, StringComparison.OrdinalIgnoreCase));

        if (caseInsensitive is { Name: { }, Type: { } caseInsensitiveType })
        {
            path = new ResolvedPath(
                [new ResolvedSegment(caseInsensitive.Name, caseInsensitiveType)],
                caseInsensitiveType);

            return true;
        }

        return false;
    }

    private bool TryResolveAggregate(
        ITypeSymbol sourceRoot,
        string targetMemberName,
        ITypeSymbol targetType,
        out ResolvedPath collectionPath,
        out AggregateMapping mapping)
    {
        collectionPath = default!;
        mapping = default!;

        var cleanName = targetMemberName.TrimStart('_');

        foreach (var (kind, suffix) in AggregateSuffixes)
        {
            // Старый вариант: ItemsCount, ItemsValueSum, ItemsNameFirstOrDefault
            if (cleanName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                var prefix = cleanName[..^suffix.Length].TrimEnd('_');

                if (prefix.Length > 0 &&
                    TryResolveAggregateCore(
                        kind,
                        sourceRoot,
                        prefix,
                        trailingSelectorSuffix: string.Empty,
                        targetType,
                        out collectionPath,
                        out mapping))
                {
                    return true;
                }
            }

            // Новый вариант: CustomerAddressesFirstOrDefaultCity
            var searchStart = 0;

            while (searchStart < cleanName.Length)
            {
                var index = cleanName.IndexOf(
                    suffix,
                    searchStart,
                    StringComparison.OrdinalIgnoreCase);

                if (index < 0)
                {
                    break;
                }

                searchStart = index + suffix.Length;

                // Суффикс не должен быть в самом начале или в самом конце.
                // Конец уже обработан выше.
                if (index == 0 || index + suffix.Length >= cleanName.Length)
                {
                    continue;
                }

                var prefix = cleanName[..index].TrimEnd('_');
                var trailingSelector = cleanName[(index + suffix.Length)..].TrimStart('_');

                if (prefix.Length == 0 || trailingSelector.Length == 0)
                {
                    continue;
                }

                if (TryResolveAggregateCore(
                        kind,
                        sourceRoot,
                        prefix,
                        trailingSelector,
                        targetType,
                        out collectionPath,
                        out mapping))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private bool TryResolveAggregateCore(
        AggregateKind kind,
        ITypeSymbol sourceRoot,
        string prefix,
        string trailingSelectorSuffix,
        ITypeSymbol targetType,
        out ResolvedPath collectionPath,
        out AggregateMapping mapping)
    {
        collectionPath = default!;
        mapping = default!;

        if (!TryResolveCollectionPrefix(
                sourceRoot,
                prefix,
                out var resolvedCollectionPath,
                out var selectorSuffix))
        {
            return false;
        }

        if (!IsCollection(resolvedCollectionPath.FinalType, out var elementType))
        {
            return false;
        }

        if (!TryBuildLoweredAggregate(
                kind,
                resolvedCollectionPath,
                elementType!,
                selectorSuffix,
                trailingSelectorSuffix,
                targetType,
                out mapping))
        {
            return false;
        }

        collectionPath = resolvedCollectionPath;
        return true;
    }


    private bool TryResolveCollectionPrefix(
    ITypeSymbol type,
    string suffix,
    out ResolvedPath path,
    out string remaining)
    {
        var membersByLength = GetReadableMembersByLength(type);

        path = default!;
        remaining = string.Empty;

        if (string.IsNullOrEmpty(suffix))
        {
            return false;
        }

        // Сначала пробуем весь suffix как путь к коллекции.
        if (TryResolveSourcePath(type, suffix, out var fullPath) &&
            IsCollection(fullPath.FinalType, out _))
        {
            path = fullPath;
            return true;
        }

        foreach (var member in membersByLength)
        {
            if (!TryCutPrefix(suffix, member.Name, out var rest))
            {
                continue;
            }

            if (IsCollection(member.Type, out _))
            {
                path = new ResolvedPath(
                    [new ResolvedSegment(member.Name, member.Type)],
                    member.Type);

                remaining = rest;
                return true;
            }

            if (TryResolveCollectionPrefix(member.Type, rest, out var nestedPath, out remaining))
            {
                path = new ResolvedPath(
                    [
                        new ResolvedSegment(member.Name, member.Type),
                    .. nestedPath.Segments
                    ],
                    nestedPath.FinalType);

                return true;
            }
        }

        return false;
    }

    private static bool TryCutPrefix(string text, string prefix, out string rest)
    {
        rest = string.Empty;

        if (text.Length < prefix.Length)
        {
            return false;
        }

        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        rest = text[prefix.Length..];
        return true;
    }

    private bool TryBuildLoweredAggregate(
        AggregateKind kind,
        ResolvedPath collectionPath,
        ITypeSymbol elementType,
        string leftSelectorSuffix,
        string trailingSelectorSuffix,
        ITypeSymbol targetType,
        out AggregateMapping mapping)
    {
        mapping = default!;

        var sourceModel = typeModelCache.GetOrAdd(collectionPath.FinalType, TypeModel.Create);
        var targetModel = typeModelCache.GetOrAdd(targetType, TypeModel.Create);
        var elementModel = typeModelCache.GetOrAdd(elementType, TypeModel.Create);

        var useCountProperty = HasCountProperty(collectionPath.FinalType);

        switch (kind)
        {
            case AggregateKind.Count:
                {
                    if (leftSelectorSuffix.Length > 0 || trailingSelectorSuffix.Length > 0)
                    {
                        return false;
                    }

                    if (!TryResolveMapping(
                            int32Type,
                            targetType,
                            out var resultMapping))
                    {
                        return false;
                    }

                    mapping = new AggregateMapping
                    {
                        Kind = kind,
                        SourceType = sourceModel,
                        TargetType = targetModel,
                        ElementType = elementModel,
                        Predicate = null,
                        Projection = null,
                        ResultMapping = resultMapping,
                        UseCountProperty = useCountProperty,
                        RequiresNullForgiving = false
                    };

                    return true;
                }

            case AggregateKind.Any:
            case AggregateKind.All:
                {
                    var hasSelector = leftSelectorSuffix.Length > 0 || trailingSelectorSuffix.Length > 0;

                    ResolvedPath? predicatePath = null;

                    if (hasSelector &&
                        !TryResolveAggregateSelectorPath(
                            elementType,
                            leftSelectorSuffix,
                            trailingSelectorSuffix,
                            out predicatePath))
                    {
                        return false;
                    }

                    if (predicatePath is { } resolvedPredicate &&
                        resolvedPredicate.FinalType.SpecialType != SpecialType.System_Boolean)
                    {
                        return false;
                    }

                    if (!TryResolveMapping(
                            boolType,
                            targetType,
                            out var resultMapping))
                    {
                        return false;
                    }

                    mapping = new AggregateMapping
                    {
                        Kind = kind,
                        SourceType = sourceModel,
                        TargetType = targetModel,
                        ElementType = elementModel,
                        Predicate = predicatePath is null
                            ? null
                            : new AggregatePredicate(MaterializePath(predicatePath)),
                        Projection = null,
                        ResultMapping = resultMapping,
                        UseCountProperty = useCountProperty,
                        RequiresNullForgiving = false
                    };

                    return true;
                }

            case AggregateKind.Sum:
            case AggregateKind.Average:
            case AggregateKind.Max:
            case AggregateKind.Min:
                {
                    var hasSelector = leftSelectorSuffix.Length > 0 || trailingSelectorSuffix.Length > 0;

                    AggregateProjection? projection = null;
                    var resultType = elementType;

                    if (hasSelector)
                    {
                        if (!TryResolveAggregateSelectorPath(
                                elementType,
                                leftSelectorSuffix,
                                trailingSelectorSuffix,
                                out var selectorPath))
                        {
                            return false;
                        }

                        var selectorModel = typeModelCache.GetOrAdd(selectorPath.FinalType, TypeModel.Create);

                        var selectorMapping = new AssignMapping
                        {
                            SourceType = selectorModel,
                            TargetType = selectorModel,
                            Kind = AssignmentKind.SameType
                        };

                        projection = new AggregateProjection(MaterializePath(selectorPath), selectorMapping);

                        resultType = selectorPath.FinalType;
                    }

                    if (!TryResolveMapping(resultType, targetType, out var resultMapping))
                    {
                        return false;
                    }

                    mapping = new AggregateMapping
                    {
                        Kind = kind,
                        SourceType = sourceModel,
                        TargetType = targetModel,
                        ElementType = elementModel,
                        Predicate = null,
                        Projection = projection,
                        ResultMapping = resultMapping,
                        UseCountProperty = useCountProperty,
                        RequiresNullForgiving = false
                    };

                    return true;
                }

            case AggregateKind.First:
            case AggregateKind.Last:
            case AggregateKind.FirstOrDefault:
            case AggregateKind.LastOrDefault:
                {
                    AggregatePredicate? predicate = null;

                    // Новый сценарий:
                    // CustomerAddressesIsPrimaryFirstOrDefaultCity
                    //
                    // leftSelectorSuffix = IsPrimary
                    // trailingSelectorSuffix = City
                    if (trailingSelectorSuffix.Length > 0)
                    {
                        if (leftSelectorSuffix.Length > 0)
                        {
                            if (!TryResolveSourcePath(
                                    elementType,
                                    leftSelectorSuffix,
                                    out var predicatePath))
                            {
                                return false;
                            }

                            if (predicatePath.FinalType.SpecialType != SpecialType.System_Boolean)
                            {
                                return false;
                            }

                            predicate = new AggregatePredicate(MaterializePath(predicatePath));
                        }

                        if (!TryResolveMemberValue(
                                elementType,
                                trailingSelectorSuffix,
                                targetType,
                                out var postPath,
                                out var postMapping))
                        {
                            return false;
                        }

                        mapping = new AggregateMapping
                        {
                            Kind = kind,
                            SourceType = sourceModel,
                            TargetType = targetModel,
                            ElementType = elementModel,
                            Predicate = predicate,
                            Projection = new AggregateProjection(MaterializePath(postPath), postMapping),
                            ResultMapping = null,
                            UseCountProperty = useCountProperty,
                            RequiresNullForgiving = RequiresNullForgiving(kind, targetModel)
                        };

                        return true;
                    }

                    // Старый сценарий:
                    // ItemsFirst
                    // ItemsNameFirstOrDefault
                    if (leftSelectorSuffix.Length == 0)
                    {
                        if (!TryResolveMapping(elementType, targetType, out var elementMapping))
                        {
                            return false;
                        }

                        mapping = new AggregateMapping
                        {
                            Kind = kind,
                            SourceType = sourceModel,
                            TargetType = targetModel,
                            ElementType = elementModel,
                            Predicate = null,
                            Projection = new AggregateProjection(null, elementMapping),
                            ResultMapping = null,
                            UseCountProperty = useCountProperty,
                            RequiresNullForgiving = RequiresNullForgiving(kind, targetModel)
                        };

                        return true;
                    }

                    if (!TryResolveSourcePath(
                            elementType,
                            leftSelectorSuffix,
                            out var simpleSelectorPath))
                    {
                        return false;
                    }

                    if (!TryResolveMapping(
                            simpleSelectorPath.FinalType,
                            targetType,
                            out var selectorMapping))
                    {
                        return false;
                    }

                    mapping = new AggregateMapping
                    {
                        Kind = kind,
                        SourceType = sourceModel,
                        TargetType = targetModel,
                        ElementType = elementModel,
                        Predicate = null,
                        Projection = new AggregateProjection(MaterializePath(simpleSelectorPath),selectorMapping),
                        ResultMapping = null,
                        UseCountProperty = useCountProperty,
                        RequiresNullForgiving = RequiresNullForgiving(kind, targetModel)
                    };

                    return true;
                }

            default:
                return false;
        }
    }

    private bool TryResolveAggregateSelectorPath(
    ITypeSymbol elementType,
    string leftSelectorSuffix,
    string trailingSelectorSuffix,
    out ResolvedPath path)
    {
        path = default!;

        var candidates = (leftSelectorSuffix.Length, trailingSelectorSuffix.Length) switch
        {
            (0, _) => new[] { trailingSelectorSuffix },
            (_, 0) => [leftSelectorSuffix],
            _ =>
            [
            leftSelectorSuffix + trailingSelectorSuffix,
            trailingSelectorSuffix,
            leftSelectorSuffix
        ]
        };

        foreach (var candidate in candidates)
        {
            if (candidate.Length == 0)
            {
                continue;
            }

            if (TryResolveSourcePath(elementType, candidate, out path))
            {
                return true;
            }
        }

        return false;
    }

    private bool HasCountProperty(ITypeSymbol type)
    {
        return hasCountPropertyCache.GetOrAdd(type, static t => HasCountPropertyCore(t));
    }

    private static bool HasCountPropertyCore(ITypeSymbol type)
    {
        return type
            .GetMembers("Count")
            .OfType<IPropertySymbol>()
            .Any(p =>
                !p.IsStatic &&
                p.GetMethod is not null &&
                IsAccessibleFromGeneratedCode(p) &&
                IsAccessibleFromGeneratedCode(p.GetMethod));
    }


    private static bool RequiresNullForgiving(AggregateKind kind, TypeModel targetType)
    {
        return kind is AggregateKind.FirstOrDefault or AggregateKind.LastOrDefault &&
               targetType.IsReference &&
               !targetType.IsNullableByNullability;
    }

    private bool TryResolveMapping(
        ITypeSymbol source,
        ITypeSymbol target,
        [NotNullWhen(true)] out Mapping? mapping)
    {
        var key = (source, target);

        if (mappingsCache.TryGetValue(key, out mapping))
        {
            return mapping is not null;
        }

        if (failedMappingsCache.ContainsKey(key))
        {
            mapping = null;
            return false;
        }

        try
        {
            mapping = ResolveMapping(source, target);
            mappingsCache[key] = mapping;
            return true;
        }
        catch (MappingGenerationException ex) when (ex is not RecursiveMappingGenerationException)
        {
            failedMappingsCache.TryAdd(key, 0);
            mapping = null;
            return false;
        }
    }

    private bool TryResolveSourcePath(
    ITypeSymbol sourceType,
    string suffix,
    out ResolvedPath path)
    {
        path = default!;

        if (string.IsNullOrEmpty(suffix))
        {
            return false;
        }

        var cleanSuffix = suffix.TrimStart('_');

        var members = GetReadableMembers(sourceType);

        var exact = members.FirstOrDefault(m => m.Name == cleanSuffix || m.Name == suffix);

        if (exact is { Name: { }, Type: { } exactType })
        {
            path = new ResolvedPath(
                [new ResolvedSegment(exact.Name, exactType)],
                exactType);

            return true;
        }

        var caseInsensitive = members.FirstOrDefault(m =>
            string.Equals(m.Name, cleanSuffix, StringComparison.OrdinalIgnoreCase));

        if (caseInsensitive is { Name: { }, Type: { } caseInsensitiveType })
        {
            path = new ResolvedPath(
                [new ResolvedSegment(caseInsensitive.Name, caseInsensitiveType)],
                caseInsensitiveType);

            return true;
        }

        foreach (var member in members.OrderByDescending(m => m.Name.Length))
        {
            if (!cleanSuffix.StartsWith(member.Name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (cleanSuffix.Length <= member.Name.Length)
            {
                continue;
            }

            var remainingSuffix = cleanSuffix[member.Name.Length..];

            if (!TryResolveSourcePath(member.Type, remainingSuffix, out var nestedPath))
            {
                continue;
            }

            path = new ResolvedPath(
                [new ResolvedSegment(member.Name, member.Type), .. nestedPath.Segments],
                nestedPath.FinalType);

            return true;
        }

        return false;
    }

    private SourcePath MaterializePath(ResolvedPath path)
    {
        return new SourcePath
        {
            Segments = [.. path.Segments
                .Select(segment => new SourcePathSegment
                {
                    MemberName = segment.Name,
                    Type = typeModelCache.GetOrAdd(segment.Type, TypeModel.Create)
                })]
        };
    }

    private static bool CanUseNullForUnmappedConstructorParameter(TypeModel parameterType)
    {
        return parameterType.IsNullableValue ||
               (parameterType.IsReference && parameterType.Annotation == NullableAnnotation.Annotated);
    }

    private ImmutableArray<ReadableMember> GetReadableMembers(ITypeSymbol type)
    {
        return readableMembersCache.GetOrAdd(type, static t => [.. GetReadableMembersCore(t)]);
    }

    private ImmutableArray<ReadableMember> GetReadableMembersByLength(ITypeSymbol type)
    {
        return readableMembersByLengthCache.GetOrAdd(
            type,
            t => [.. GetReadableMembers(t).OrderByDescending(m => m.Name.Length)]);
    }

    private ImmutableArray<TargetMemberInfo> GetTargetMembers(INamedTypeSymbol type)
    {
        return targetMembersCache.GetOrAdd(type, static t => GetTargetMembersCore(t).ToImmutableArray());
    }

    private ImmutableArray<string> GetRequiredMemberNames(INamedTypeSymbol type)
    {
        return requiredMembersCache.GetOrAdd(type, static t => GetRequiredMemberNamesCore(t).ToImmutableArray());
    }

    private static IEnumerable<ReadableMember> GetReadableMembersCore(ITypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var currentType in GetTypeAndBaseTypes(type))
        {
            foreach (var property in currentType.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.Parameters.Length > 0 || property.GetMethod is null)
                {
                    continue;
                }

                if (!IsAccessibleFromGeneratedCode(property) ||
                    !IsAccessibleFromGeneratedCode(property.GetMethod))
                {
                    continue;
                }

                if (!seen.Add(property.Name))
                {
                    continue;
                }

                yield return new ReadableMember(property.Name, property.Type);
            }

            foreach (var field in currentType.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic)
                {
                    continue;
                }

                if (!IsAccessibleFromGeneratedCode(field))
                {
                    continue;
                }

                if (!seen.Add(field.Name))
                {
                    continue;
                }

                yield return new ReadableMember(field.Name, field.Type);
            }
        }
    }
    private static IEnumerable<TargetMemberInfo> GetTargetMembersCore(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var currentType in GetTypeAndBaseTypes(type))
        {
            foreach (var property in currentType.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsStatic || property.Parameters.Length > 0)
                {
                    continue;
                }

                var canRead = property.GetMethod is not null &&
                    IsAccessibleFromGeneratedCode(property) &&
                    IsAccessibleFromGeneratedCode(property.GetMethod);

                var canWrite = property.SetMethod is not null &&
                    IsAccessibleFromGeneratedCode(property) &&
                    IsAccessibleFromGeneratedCode(property.SetMethod);

                if (!canRead && !canWrite)
                {
                    continue;
                }

                if (!seen.Add(property.Name))
                {
                    continue;
                }

                yield return new TargetMemberInfo(
                    property.Name,
                    property.Type,
                    property.IsRequired,
                    IsInitOnlyProperty(property),
                    canRead,
                    canWrite,
                    property.Type.IsValueType);
            }

            foreach (var field in currentType.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsStatic || field.IsConst)
                {
                    continue;
                }

                if (!IsAccessibleFromGeneratedCode(field))
                {
                    continue;
                }

                if (!seen.Add(field.Name))
                {
                    continue;
                }

                yield return new TargetMemberInfo(
                    field.Name,
                    field.Type,
                    field.IsRequired,
                    IsInitOnly: false,
                    CanRead: true,
                    CanWrite: !field.IsReadOnly,
                    field.Type.IsValueType);
            }
        }
    }

    private static IEnumerable<string> GetRequiredMemberNamesCore(INamedTypeSymbol type)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var currentType in GetTypeAndBaseTypes(type))
        {
            foreach (var property in currentType.GetMembers().OfType<IPropertySymbol>())
            {
                if (property.IsRequired && seen.Add(property.Name))
                {
                    yield return property.Name;
                }
            }

            foreach (var field in currentType.GetMembers().OfType<IFieldSymbol>())
            {
                if (field.IsRequired && seen.Add(field.Name))
                {
                    yield return field.Name;
                }
            }
        }
    }

    private static bool IsInitOnlyProperty(IPropertySymbol property)
    {
        var setter = property.SetMethod;

        if (setter is null)
        {
            return false;
        }

        foreach (var modifier in setter.ReturnTypeCustomModifiers)
        {
            if (modifier.IsOptional)
            {
                continue;
            }

            var modifierType = modifier.Modifier;

            if (modifierType.Name == "IsExternalInit" &&
                modifierType.ContainingNamespace.ToDisplayString() == "System.Runtime.CompilerServices")
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAccessibleFromGeneratedCode(ISymbol symbol) => symbol.DeclaredAccessibility == Accessibility.Public;

    private bool IsCollection(
        ITypeSymbol type,
        [NotNullWhen(true)] out ITypeSymbol? elementType)
    {
        if (collectionElementCache.TryGetValue(type, out var cached))
        {
            elementType = cached;
            return cached is not null;
        }

        elementType = ResolveCollectionElement(type);
        collectionElementCache[type] = elementType;

        return elementType is not null;
    }

    private ITypeSymbol? ResolveCollectionElement(ITypeSymbol type)
    {
        if (type.SpecialType == SpecialType.System_String)
        {
            return null;
        }

        if (type is IArrayTypeSymbol array)
        {
            return array.ElementType;
        }

        if (type is INamedTypeSymbol named &&
            named.IsGenericType &&
            SymbolEqualityComparer.Default.Equals(named.ConstructedFrom, enumerableOfT))
        {
            return named.TypeArguments[0];
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.IsGenericType &&
                SymbolEqualityComparer.Default.Equals(iface.ConstructedFrom, enumerableOfT))
            {
                return iface.TypeArguments[0];
            }
        }

        return null;
    }


    private PopScope Push(ITypeSymbol source, ITypeSymbol target)
    {
        foreach (var (Source, Target) in path)
        {
            if (SymbolEqualityComparer.Default.Equals(Source, source)
                && SymbolEqualityComparer.Default.Equals(Target, target))
            {
                throw new RecursiveMappingGenerationException(
                    $"Recursive mapping detected between '{source.ToDisplayString()}' and '{target.ToDisplayString()}'.");
            }
        }

        path.Push((source, target));
        return new PopScope(this, source, target);
    }

    private readonly struct PopScope(MappingBuilder owner, ITypeSymbol source, ITypeSymbol target) : IDisposable
    {
        public void Dispose()
        {
            while (true)
            {
                if(owner.path.TryPop(out var e))
                {
                    if(ReferenceEquals(e.Source, source) && ReferenceEquals(e.Target, target))
                    {
                        break;
                    }
#pragma warning disable S3877
                    throw new InvalidOperationException();
#pragma warning restore S3877
                }
            }
            
                
        }
    }


    private static bool IsSimpleType(INamedTypeSymbol type)
    {
        return type.SpecialType switch
        {
            SpecialType.System_Boolean => true,
            SpecialType.System_Char => true,
            SpecialType.System_SByte => true,
            SpecialType.System_Byte => true,
            SpecialType.System_Int16 => true,
            SpecialType.System_UInt16 => true,
            SpecialType.System_Int32 => true,
            SpecialType.System_UInt32 => true,
            SpecialType.System_Int64 => true,
            SpecialType.System_UInt64 => true,
            SpecialType.System_Decimal => true,
            SpecialType.System_Single => true,
            SpecialType.System_Double => true,
            SpecialType.System_String => true,
            SpecialType.System_DateTime => true,
            _ => type.TypeKind == TypeKind.Enum,
        };
    }

    private static bool CanBeNullRuntime(ITypeSymbol type)
    {
        return type.IsReferenceType
            || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type)
    {
        return type is INamedTypeSymbol
        {
            IsValueType: true,
            ConstructedFrom.SpecialType: SpecialType.System_Nullable_T
        } nullable
            ? nullable.TypeArguments[0]
            : type;
    }

    private static IEnumerable<ITypeSymbol> GetTypeAndBaseTypes(ITypeSymbol type)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            yield return current;
        }
    }

    private static readonly (AggregateKind Kind, string Suffix)[] AggregateSuffixes =
[
        (AggregateKind.FirstOrDefault, nameof(AggregateKind.FirstOrDefault)),
        (AggregateKind.LastOrDefault, nameof(AggregateKind.LastOrDefault)),
        (AggregateKind.First, nameof(AggregateKind.First)),
        (AggregateKind.Last, nameof(AggregateKind.Last)),
        (AggregateKind.Count, nameof(AggregateKind.Count)),
        (AggregateKind.Any, nameof(AggregateKind.Any)),
        (AggregateKind.All, nameof(AggregateKind.All)),
        (AggregateKind.Sum, nameof(AggregateKind.Sum)),
        (AggregateKind.Average, nameof(AggregateKind.Average)),
        (AggregateKind.Max, nameof(AggregateKind.Max)),
        (AggregateKind.Min, nameof(AggregateKind.Min))
    ];

    private sealed record ReadableMember(string Name, ITypeSymbol Type);

    private sealed record ResolvedPath(ImmutableArray<ResolvedSegment> Segments, ITypeSymbol FinalType);

    private sealed record ResolvedSegment(string Name, ITypeSymbol Type);

    private sealed record TargetMemberInfo(
        string Name,
        ITypeSymbol Type,
        bool IsRequired,
        bool IsInitOnly,
        bool CanRead,
        bool CanWrite,
        bool IsValueType);

    private sealed class SymbolPairComparer : IEqualityComparer<(ITypeSymbol Source, ITypeSymbol Target)>
    {
        public static SymbolPairComparer Instance { get; } = new();

        private static readonly SymbolEqualityComparer Comparer = SymbolEqualityComparer.IncludeNullability;

        public bool Equals(
            (ITypeSymbol Source, ITypeSymbol Target) x,
            (ITypeSymbol Source, ITypeSymbol Target) y)
        {
            return Comparer.Equals(x.Source, y.Source) &&
                   Comparer.Equals(x.Target, y.Target);
        }

        public int GetHashCode((ITypeSymbol Source, ITypeSymbol Target) obj)
        {
            return HashCode.Combine(
                Comparer.GetHashCode(obj.Source),
                Comparer.GetHashCode(obj.Target));
        }
    }
}

internal sealed class RecursiveMappingGenerationException(string message) : MappingGenerationException(message) { }

internal class MappingGenerationException(string message) : Exception(message) { }
internal sealed class NoSuitableConstructorException(string message): MappingGenerationException(message) { }

