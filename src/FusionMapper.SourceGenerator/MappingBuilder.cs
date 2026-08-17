using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace FusionMapper.SourceGenerator;

class MappingBuilder(Compilation compilation)
{
    private readonly Stack<(ITypeSymbol Source, ITypeSymbol Target)> path = new();
    private readonly Dictionary<(TypeModel Source, TypeModel Target), Mapping> mappings = [];

    internal static ImmutableDictionary<(TypeModel Source, TypeModel Target), Mapping> CreateMappings(
        ImmutableArray<RawCandidate> candidates,
        Compilation compilation,
        CancellationToken ct)
    {
        var builder = new MappingBuilder(compilation);

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            if (candidate.SourceSymbol.IsAnonymousType || candidate.TargetSymbol.IsAnonymousType)
                continue;

            builder.AddMapping(candidate);
        }

        return builder.mappings.ToImmutableDictionary();
    }

    private void AddMapping(RawCandidate candidate)
    {
        var key = (candidate.Source, candidate.Target);

        if (mappings.ContainsKey(key))
            return;

        mappings[key] = Build(candidate.SourceSymbol, candidate.TargetSymbol);
    }


    public Mapping Build(ITypeSymbol sourceSymbol, ITypeSymbol targetSymbol)
    {
        var source = TypeModel.Create(sourceSymbol);
        var target = TypeModel.Create(targetSymbol);

        var key = (Source: source, Target: target);

        if (mappings.TryGetValue(key, out var cachedMapping))
        {
            return cachedMapping;
        }

        var mapping = ResolveMapping(sourceSymbol, targetSymbol);
        mappings[key] = mapping;
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

        if (source.TypeKind == TypeKind.Enum && target.SpecialType == SpecialType.System_String)
        {
            return CreateAssignMapping(source, target, AssignmentKind.EnumToString);
        }

        if (source.SpecialType == SpecialType.System_String && target.TypeKind == TypeKind.Enum)
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

    private static AssignMapping CreateAssignMapping(
    ITypeSymbol source,
    ITypeSymbol target,
    AssignmentKind kind)
    {
        return new AssignMapping
        {
            SourceType = TypeModel.Create(source),
            TargetType = TypeModel.Create(target),
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
            SourceType = TypeModel.Create(source),
            TargetType = TypeModel.Create(target),

            ElementType = TypeModel.Create(targetElement),
            ElementMapping = elementMapping,

            HasClearMethod = HasInstanceMethod(target, "Clear", parameterCount: 0),
            HasAddMethod = HasInstanceMethod(target, "Add", parameterCount: 1),
            HasAddRangeMethod = HasInstanceMethod(target, "AddRange", parameterCount: 1)
        };
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
        var assignedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in GetCreatableMembers(target))
        {
            if (!TryResolveSourcePath(source, member.Name, out var sourcePath))
            {
                continue;
            }

            if (!TryResolveMapping(sourcePath.FinalType, member.Type, out var valueMapping))
            {
                continue;
            }

            bindings.Add(new MemberBinding
            {
                TargetMemberName = member.Name,
                Source = MaterializePath(sourcePath),
                Value = valueMapping,
                IsRequired = member.IsRequired,
                IsInitOnly = member.IsInitOnly
            });

            assignedMembers.Add(member.Name);
        }

        var requiredMembers = GetRequiredMemberNames(target).ToList();

        var constructor = ResolveConstructor(
            source,
            target,
            assignedMembers,
            requiredMembers);

        return new ObjectMapping
        {
            SourceType = TypeModel.Create(source),
            TargetType = TypeModel.Create(target),

            Constructor = constructor,
            Bindings = bindings.ToImmutable()
        };
    }


    private ObjectConstructor ResolveConstructor(
    ITypeSymbol source,
    INamedTypeSymbol target,
    ISet<string> assignedMembers,
    IReadOnlyCollection<string> requiredMembers)
    {
        foreach (var constructor in target.InstanceConstructors
                     .Where(c => c.DeclaredAccessibility == Accessibility.Public)
                     .OrderByDescending(c => c.Parameters.Length))
        {
            var arguments = ImmutableArray.CreateBuilder<ConstructorArgument>(constructor.Parameters.Length);
            var constructorAssignedMembers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var canUse = true;

            foreach (var parameter in constructor.Parameters)
            {
                if (parameter.Name is { Length: > 0 } parameterName
                    && TryResolveSourcePath(source, parameterName, out var sourcePath)
                    && TryResolveMapping(sourcePath.FinalType, parameter.Type, out var valueMapping))
                {
                    arguments.Add(new ConstructorArgument
                    {
                        ArgumentType = TypeModel.Create(parameter.Type),
                        IsDefault = false,
                        Source = MaterializePath(sourcePath),
                        Value = valueMapping
                    });

                    constructorAssignedMembers.Add(parameterName);
                    continue;
                }

                // Если параметр может принимать null, разрешаем fallback в default.
                // Для non-nullable value types это запрещено.
                if (CanBeNullRuntime(parameter.Type))
                {
                    arguments.Add(new ConstructorArgument
                    {
                        ArgumentType = TypeModel.Create(parameter.Type),
                        IsDefault = true,
                        Source = null,
                        Value = null
                    });

                    if (parameter.Name is { Length: > 0 } fallbackName)
                    {
                        constructorAssignedMembers.Add(fallbackName);
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

            var unassignedRequired = requiredMembers
                .Where(required =>
                    !assignedMembers.Contains(required)
                    && !constructorAssignedMembers.Contains(required))
                .ToList();

            if (!setsRequiredMembers && unassignedRequired.Count > 0)
            {
                continue;
            }

            return new ObjectConstructor
            {
                Arguments = arguments.ToImmutable()
            };
        }

        // Для value type допустим fallback на default constructor,
        // если нет required members, которые обязательно нужно назначить.
        if (target.IsValueType && requiredMembers.All(assignedMembers.Contains))
        {
            return new ObjectConstructor
            {
                Arguments = []
            };
        }

        throw new MappingGenerationException(
            $"No suitable constructor or required members are not mapped for type '{target.ToDisplayString()}'.");
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
        catch (MappingGenerationException)
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

    private static SourcePath MaterializePath(ResolvedPath path)
    {
        return new SourcePath
        {
            Segments = [.. path.Segments
                .Select(segment => new SourcePathSegment
                {
                    MemberName = segment.Name,
                    Type = TypeModel.Create(segment.Type)
                })]
        };
    }

    private IEnumerable<ReadableMember> GetReadableMembers(ITypeSymbol type)
    {
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.Parameters.Length > 0 || property.GetMethod is null)
            {
                continue;
            }

            if (!IsAccessibleFromGeneratedCode(property) || !IsAccessibleFromGeneratedCode(property.GetMethod))
            {
                continue;
            }

            yield return new ReadableMember(property.Name, property.Type);
        }

        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic)
            {
                continue;
            }

            if (!IsAccessibleFromGeneratedCode(field))
            {
                continue;
            }

            yield return new ReadableMember(field.Name, field.Type);
        }
    }

    private IEnumerable<WritableMember> GetCreatableMembers(INamedTypeSymbol type)
    {
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsStatic || property.Parameters.Length > 0 || property.SetMethod is null)
            {
                continue;
            }

            if (!IsAccessibleFromGeneratedCode(property) || !IsAccessibleFromGeneratedCode(property.SetMethod))
            {
                continue;
            }

            yield return new WritableMember(
                property.Name,
                property.Type,
                property.IsRequired,
                IsInitOnly(property));
        }

        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsStatic || field.IsReadOnly || field.IsConst)
            {
                continue;
            }

            if (!IsAccessibleFromGeneratedCode(field))
            {
                continue;
            }

            yield return new WritableMember(
                field.Name,
                field.Type,
                field.IsRequired,
                IsInitOnly: false);
        }
    }

    private static IEnumerable<string> GetRequiredMemberNames(INamedTypeSymbol type)
    {
        foreach (var property in type.GetMembers().OfType<IPropertySymbol>())
        {
            if (property.IsRequired)
            {
                yield return property.Name;
            }
        }

        foreach (var field in type.GetMembers().OfType<IFieldSymbol>())
        {
            if (field.IsRequired)
            {
                yield return field.Name;
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

    private bool IsAccessibleFromGeneratedCode(ISymbol symbol)
    {
        if (symbol.DeclaredAccessibility == Accessibility.Public)
        {
            return true;
        }

        if (symbol.DeclaredAccessibility == Accessibility.Internal)
        {
            return SymbolEqualityComparer.Default.Equals(
                symbol.ContainingAssembly,
                compilation.Assembly);
        }

        return false;
    }

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
                throw new MappingGenerationException(
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



    private sealed record ReadableMember(string Name, ITypeSymbol Type);
    private sealed record WritableMember(string Name, ITypeSymbol Type, bool IsRequired, bool IsInitOnly);

    private sealed record ResolvedPath(ImmutableArray<ResolvedSegment> Segments, ITypeSymbol FinalType);

    private sealed record ResolvedSegment(string Name, ITypeSymbol Type);
}

internal sealed class MappingGenerationException(string message) : Exception(message)
{
}