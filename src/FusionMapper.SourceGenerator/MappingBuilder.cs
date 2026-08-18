using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FusionMapper.SourceGenerator;

class MappingBuilder(Compilation compilation)
{
    private readonly Stack<(ITypeSymbol Source, ITypeSymbol Target)> path = new();
    private readonly ConcurrentDictionary<ITypeSymbol, TypeModel> typeModelCache = new (SymbolEqualityComparer.IncludeNullability);
    private readonly ConcurrentDictionary<(TypeModel Source, TypeModel Target), Mapping> mappingsCache = [];


    public Mapping Build(ITypeSymbol sourceSymbol, ITypeSymbol targetSymbol)
    {
        var source = typeModelCache.GetOrAdd(sourceSymbol, TypeModel.Create);
        var target = typeModelCache.GetOrAdd(targetSymbol, TypeModel.Create);

        var key = (Source: source, Target: target);

        if (mappingsCache.TryGetValue(key, out var cachedMapping))
        {
            return cachedMapping;
        }

        var mapping = ResolveMapping(sourceSymbol, targetSymbol);
        mappingsCache[key] = mapping;
        return mapping;
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

        if (target is INamedTypeSymbol namedTarget && !IsSimpleType(namedTarget))
        {
            return ResolveObjectMapping(source, namedTarget);
        }

        if (conversion.Exists && conversion.IsExplicit)
        {
            return CreateAssignMapping(source, target, AssignmentKind.ExplicitCast);
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
            Capabilities = BuildCollectionCapabilities(target, targetElement)
        };
    }

    private CollectionCapabilities BuildCollectionCapabilities(
    ITypeSymbol target,
    ITypeSymbol elementType)
    {
        var enumerableOfElement = compilation
            .GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T)
            .Construct(elementType);

        return new CollectionCapabilities
        {
            IsArray = target is IArrayTypeSymbol,

            IsGenericList = target is INamedTypeSymbol
            {
                IsGenericType: true
            } genericList && SymbolEqualityComparer.Default.Equals(
                genericList.ConstructedFrom, 
                compilation.GetTypeByMetadataName("System.Collections.Generic.List`1")),

            IsKnownCollectionInterface = IsKnownCollectionInterfaceSymbol(target),

            HasClearMethod = HasInstanceMethod(target, "Clear", parameterCount: 0),
            HasAddMethod = HasInstanceMethod(target, "Add", parameterCount: 1),
            HasAddRangeMethod = HasInstanceMethod(target, "AddRange", parameterCount: 1),

            HasParameterlessConstructor = target is INamedTypeSymbol namedTarget &&
                namedTarget.InstanceConstructors.Any(c =>
                    c.DeclaredAccessibility == Accessibility.Public &&
                    c.Parameters.Length == 0),

            HasEnumerableConstructor = target is INamedTypeSymbol named &&
                named.InstanceConstructors.Any(c =>
                    c.DeclaredAccessibility == Accessibility.Public &&
                    c.Parameters.Length == 1 &&
                    compilation.ClassifyConversion(enumerableOfElement, c.Parameters[0].Type).IsImplicit),

            HasCountProperty = target
                .GetMembers("Count")
                .OfType<IPropertySymbol>()
                .Any(p =>
                    !p.IsStatic &&
                    p.GetMethod is not null &&
                    IsAccessibleFromGeneratedCode(p) &&
                    IsAccessibleFromGeneratedCode(p.GetMethod))
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

    private static bool HasInstanceMethod(ITypeSymbol type, string name, int parameterCount)
    {
        return type
            .GetMembers(name)
            .OfType<IMethodSymbol>()
            .Any(method =>
                !method.IsStatic
                && method.Parameters.Length == parameterCount);
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

            bindings.Add(new MemberBinding
            {
                TargetMemberName = member.Name,
                Source = MaterializePath(sourcePath),
                Value = valueMapping,
                IsRequired = member.IsRequired,
                IsInitOnly = member.IsInitOnly,
                CanRead = member.CanRead,
                CanWrite = member.CanWrite,
                IsTargetMemberValueType = member.IsValueType
            });

            if (member.CanWrite)
            {
                assignableMembers.Add(member.Name);
            }
        }

        var requiredMembers = GetRequiredMemberNames(target).ToImmutableArray();

        var constructors = BuildConstructorCandidates(
            source,
            target,
            assignableMembers,
            requiredMembers);

        if (constructors.Length == 0)
        {
            throw new MappingGenerationException(
                $"No suitable constructor or required members are not mapped for type '{target.ToDisplayString()}'.");
        }

        return new ObjectMapping
        {
            SourceType = typeModelCache.GetOrAdd(source, TypeModel.Create),
            TargetType = typeModelCache.GetOrAdd(target, TypeModel.Create),
            Constructors = constructors,
            Members = bindings.ToImmutable(),
            RequiredMemberNames = requiredMembers
        };
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
        var members = GetReadableMembers(sourceType).ToList();

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

        ResolvedPath? selectorPath = null;

        if (selectorSuffix.Length > 0 || trailingSelectorSuffix.Length > 0)
        {
            IEnumerable<string> candidates = (selectorSuffix.Length, trailingSelectorSuffix.Length) switch
            {
                (0, _) => [trailingSelectorSuffix],
                (_, 0) => [selectorSuffix],
                _ =>
                [
                    selectorSuffix + trailingSelectorSuffix,
                trailingSelectorSuffix,
                selectorSuffix
                ]
            };

            foreach (var candidate in candidates)
            {
                if (candidate.Length == 0)
                {
                    continue;
                }

                if (TryResolveSourcePath(elementType!, candidate, out var resolvedSelector))
                {
                    selectorPath = resolvedSelector;
                    break;
                }
            }

            if (selectorPath is null)
            {
                return false;
            }
        }

        if (!TryBuildAggregateMapping(
                kind,
                resolvedCollectionPath,
                elementType!,
                selectorPath,
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

        foreach (var member in GetReadableMembers(type).OrderByDescending(m => m.Name.Length))
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

    private bool TryBuildAggregateMapping(
    AggregateKind kind,
    ResolvedPath collectionPath,
    ITypeSymbol elementType,
    ResolvedPath? selectorPath,
    ITypeSymbol targetType,
    out AggregateMapping mapping)
    {
        mapping = default!;
        var sourceModel = typeModelCache.GetOrAdd(collectionPath.FinalType, TypeModel.Create);
        var targetModel = typeModelCache.GetOrAdd(targetType, TypeModel.Create);
        var elementModel = typeModelCache.GetOrAdd(elementType, TypeModel.Create);

        var hasCountProperty = collectionPath.FinalType
            .GetMembers("Count")
            .OfType<IPropertySymbol>()
            .Any(p =>
                !p.IsStatic &&
                p.GetMethod is not null &&
                IsAccessibleFromGeneratedCode(p) &&
                IsAccessibleFromGeneratedCode(p.GetMethod));

        switch (kind)
        {
            case AggregateKind.Count:
                {
                    if (!TryResolveMapping(
                            compilation.GetSpecialType(SpecialType.System_Int32),
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
                        Selector = null,
                        ElementMapping = null,
                        ResultMapping = resultMapping,
                        SourceHasCountProperty = hasCountProperty
                    };

                    return true;
                }

            case AggregateKind.Any:
            case AggregateKind.All:
                {
                    if (selectorPath is { } selector)
                    {
                        if (selector.FinalType.SpecialType != SpecialType.System_Boolean)
                        {
                            return false;
                        }
                    }

                    if (!TryResolveMapping(
                            compilation.GetSpecialType(SpecialType.System_Boolean),
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
                        Selector = selectorPath is null ? null : MaterializePath(selectorPath),
                        ElementMapping = null,
                        ResultMapping = resultMapping,
                        SourceHasCountProperty = hasCountProperty
                    };

                    return true;
                }

            case AggregateKind.Sum:
            case AggregateKind.Average:
            case AggregateKind.Max:
            case AggregateKind.Min:
                {
                    // Здесь можно расширить проверку числовых типов.
                    // Для первой реализации разрешаем генерацию,
                    // а финальная совместимость проверяется компилятором.

                    mapping = new AggregateMapping
                    {
                        Kind = kind,
                        SourceType = sourceModel,
                        TargetType = targetModel,
                        ElementType = elementModel,
                        Selector = selectorPath is null ? null : MaterializePath(selectorPath),
                        ElementMapping = null,
                        ResultMapping = null,
                        SourceHasCountProperty = hasCountProperty
                    };

                    return true;
                }

            case AggregateKind.First:
            case AggregateKind.Last:
            case AggregateKind.FirstOrDefault:
            case AggregateKind.LastOrDefault:
                {
                    if (selectorPath is null)
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
                            Selector = null,
                            ElementMapping = elementMapping,
                            ResultMapping = null,
                            SourceHasCountProperty = hasCountProperty
                        };

                        return true;
                    }

                    if (!TryResolveMapping(selectorPath.FinalType, targetType, out var resultMapping))
                    {
                        return false;
                    }

                    mapping = new AggregateMapping
                    {
                        Kind = kind,
                        SourceType = sourceModel,
                        TargetType = targetModel,
                        ElementType = elementModel,
                        Selector = MaterializePath(selectorPath),
                        ElementMapping = null,
                        ResultMapping = resultMapping,
                        SourceHasCountProperty = hasCountProperty
                    };

                    return true;
                }

            default:
                return false;
        }
    }

    private bool TryResolveAggregateWithPostExpression(
    AggregateKind kind,
    ITypeSymbol sourceRoot,
    string collectionPrefix,
    string trailingExpression,
    ITypeSymbol targetType,
    out ResolvedPath collectionPath,
    out AggregateMapping mapping)
    {
        collectionPath = default!;
        mapping = default!;

        if (kind is not (
            AggregateKind.First or
            AggregateKind.Last or
            AggregateKind.FirstOrDefault or
            AggregateKind.LastOrDefault))
        {
            return false;
        }

        if (!TryResolveCollectionPrefix(
                sourceRoot,
                collectionPrefix,
                out var resolvedCollectionPath,
                out var predicateSuffix))
        {
            return false;
        }

        if (!IsCollection(resolvedCollectionPath.FinalType, out var elementType))
        {
            return false;
        }

        SourcePath? predicate = null;

        if (predicateSuffix.Length > 0)
        {
            if (!TryResolveSourcePath(elementType!, predicateSuffix, out var predicatePath))
            {
                return false;
            }

            // Предикат обязан быть bool.
            // Для bool? можно позже расширить до x.IsPrimary == true.
            if (predicatePath.FinalType.SpecialType != SpecialType.System_Boolean)
            {
                return false;
            }

            predicate = MaterializePath(predicatePath);
        }

        if (!TryResolveMemberValue(
                elementType!,
                trailingExpression,
                targetType,
                out var postPath,
                out var postMapping))
        {
            return false;
        }

        collectionPath = resolvedCollectionPath;

        mapping = new AggregateMapping
        {
            Kind = kind,
            SourceType = typeModelCache.GetOrAdd(resolvedCollectionPath.FinalType, TypeModel.Create),
            TargetType = typeModelCache.GetOrAdd(targetType, TypeModel.Create),
            ElementType = typeModelCache.GetOrAdd(elementType!, TypeModel.Create),

            Selector = null,
            Predicate = predicate,

            PostSource = MaterializePath(postPath),
            PostMapping = postMapping,

            ElementMapping = null,
            ResultMapping = null,

            SourceHasCountProperty = HasCountProperty(resolvedCollectionPath.FinalType)
        };

        return true;
    }

    private bool HasCountProperty(ITypeSymbol type)
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

    private ImmutableArray<ConstructorCandidate> BuildConstructorCandidates(
    ITypeSymbol source,
    INamedTypeSymbol target,
    ISet<string> assignableMembers,
    ImmutableArray<string> requiredMembers)
    {
        var result = ImmutableArray.CreateBuilder<ConstructorCandidate>();

        foreach (var constructor in target.InstanceConstructors
                     .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                     .OrderByDescending(c => c.Parameters.Length))
        {
            var parameters = ImmutableArray.CreateBuilder<ConstructorParameter>(constructor.Parameters.Length);
            var assignedNames = ImmutableArray.CreateBuilder<string>();
            var canUse = true;

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
                    parameters.Add(new ConstructorParameter
                    {
                        ParameterType = typeModelCache.GetOrAdd(parameter.Type, TypeModel.Create),
                        IsMapped = true,
                        CanUseDefault = false,
                        Source = MaterializePath(sourcePath),
                        Value = valueMapping
                    });

                    assignedNames.Add(parameterName);
                    continue;
                }

                if (parameter.HasExplicitDefaultValue || CanBeNullRuntime(parameter.Type))
                {
                    parameters.Add(new ConstructorParameter
                    {
                        ParameterType = typeModelCache.GetOrAdd(parameter.Type, TypeModel.Create),
                        IsMapped = false,
                        CanUseDefault = true,
                        Source = null,
                        Value = null
                    });

                    if (parameter.Name is { Length: > 0 } fallbackName)
                    {
                        assignedNames.Add(fallbackName);
                    }

                    continue;
                }

                canUse = false;
                break;
            }

            if (!canUse)
            {
                continue;
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
                    continue;
                }
            }

            result.Add(new ConstructorCandidate
            {
                Parameters = parameters.ToImmutable(),
                SetsRequiredMembers = setsRequiredMembers,
                AssignedMemberNames = assignedNames.ToImmutable()
            });
        }

        // Для value type допустим synthetic parameterless candidate,
        // если все required members закрываются обычными присваиваниями.
        if (target.IsValueType && requiredMembers.All(assignableMembers.Contains))
        {
            result.Add(new ConstructorCandidate
            {
                Parameters = ImmutableArray.Create<ConstructorParameter>(),
                SetsRequiredMembers = false,
                AssignedMemberNames = ImmutableArray.Create<string>()
            });
        }

        return result.ToImmutable();
    }

    private bool TryResolveMapping(
    ITypeSymbol source,
    ITypeSymbol target,
    [NotNullWhen(true)] out Mapping? mapping)
    {
        try
        {
            mapping = ResolveMapping(source, target);
            return true;
        }
        catch (MappingGenerationException ex) when (ex is not RecursiveMappingGenerationException)
        {
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

        var members = GetReadableMembers(sourceType).ToList();

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
            if (!cleanSuffix.StartsWith(member.Name, StringComparison.Ordinal))
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
            Segments = path.Segments
                .Select(segment => new SourcePathSegment
                {
                    MemberName = segment.Name,
                    Type = typeModelCache.GetOrAdd(segment.Type, TypeModel.Create)
                }).ToImmutableArray()
        };
    }

    private static IEnumerable<ReadableMember> GetReadableMembers(ITypeSymbol type)
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

    private IEnumerable<TargetMemberInfo> GetTargetMembers(INamedTypeSymbol type)
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
                    IsInitOnly(property),
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

    private static IEnumerable<string> GetRequiredMemberNames(INamedTypeSymbol type)
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

    private static bool IsInitOnly(IPropertySymbol property)
    {
        var setter = property.SetMethod;

        if (setter is null)
        {
            return true;
        }

        foreach (var modifier in setter.ReturnTypeCustomModifiers)
        {
            if (modifier.IsOptional)
            {
                continue;
            }

            var modifierType = modifier.Modifier;

            if (modifierType.Name == "IsExternalInit"
                && modifierType.ContainingNamespace.ToDisplayString() == "System.Runtime.CompilerServices")
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
        elementType = null;

        if (type.SpecialType == SpecialType.System_String)
        {
            return false;
        }

        if (type is IArrayTypeSymbol array)
        {
            elementType = array.ElementType;
            return true;
        }

        var enumerableType = compilation.GetSpecialType(SpecialType.System_Collections_Generic_IEnumerable_T);

        if (type is INamedTypeSymbol named
            && named.IsGenericType
            && SymbolEqualityComparer.Default.Equals(named.ConstructedFrom, enumerableType))
        {
            elementType = named.TypeArguments[0];
            return true;
        }

        foreach (var iface in type.AllInterfaces)
        {
            if (iface.IsGenericType
                && SymbolEqualityComparer.Default.Equals(iface.ConstructedFrom, enumerableType))
            {
                elementType = iface.TypeArguments[0];
                return true;
            }
        }

        return false;
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
        return new PopScope(this);
    }

    private readonly struct PopScope(MappingBuilder owner) : IDisposable
    {
        public void Dispose() => owner.path.Pop();
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
}

internal sealed class RecursiveMappingGenerationException(string message) : MappingGenerationException(message) { }

internal class MappingGenerationException(string message) : Exception(message) { }