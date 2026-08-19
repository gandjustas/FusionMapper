using Microsoft.CodeAnalysis;

namespace FusionMapper.SourceGenerator;

static class FusionAccessorMetadata
{
    private const string FusionSourceMetadataName = "FusionMapper.FusionSource`1";
    private const string FusionProjectionMetadataName = "FusionMapper.FusionProjection`1";
    private const string ValueParameterName = "value";

    public static AccessorFieldNames Resolve(Compilation compilation)
    {
        var (sourceField, sourceResolved) = ResolveBackingFieldName(
            compilation,
            FusionSourceMetadataName,
            ValueParameterName);

        var (projectionField, projectionResolved) = ResolveBackingFieldName(
            compilation,
            FusionProjectionMetadataName,
            ValueParameterName);

        return new AccessorFieldNames(
            sourceField,
            projectionField,
            sourceResolved,
            projectionResolved);
    }

    private static (string Name, bool Resolved) ResolveBackingFieldName(
        Compilation compilation,
        string metadataName,
        string parameterName)
    {
        var fallbackName = $"<{parameterName}>P";

        var type = compilation.GetTypeByMetadataName(metadataName);
        if (type is null)
        {
            return (fallbackName, false);
        }

        // 1. Самый прямой вариант: поле уже называется <value>P.
        var exactField = type
            .GetMembers(fallbackName)
            .OfType<IFieldSymbol>()
            .FirstOrDefault(static f => !f.IsStatic);

        if (exactField is not null)
        {
            return (exactField.Name, true);
        }

        // 2. Ищем конструктор с параметром value.
        var constructor = type.InstanceConstructors
            .FirstOrDefault(c => c.Parameters.Length == 1 && c.Parameters[0].Name == parameterName);

        if (constructor is null)
        {
            return (fallbackName, false);
        }

        var parameterType = constructor.Parameters[0].Type;

        if (parameterType.TypeKind == TypeKind.Error)
        {
            return (fallbackName, false);
        }

        // 3. Ищем implicit backing field, созданный компилятором для primary constructor.
        var implicitField = type
            .GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static f => !f.IsStatic)
            .Where(static f => f.IsImplicitlyDeclared)
            .OrderByDescending(f => f.Name.Contains(parameterName, StringComparison.Ordinal))
            .ThenBy(static f => f.Name.Length)
            .FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.Type, parameterType));

        if (implicitField is not null)
        {
            return (implicitField.Name, true);
        }

        // 4. Если IsImplicitlyDeclared по какой-то причине не выставлен,
        //    ищем compiler-generated-like поле по имени и типу.
        var generatedLikeField = type
            .GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static f => !f.IsStatic)
            .Where(f => f.Name.StartsWith("<", StringComparison.Ordinal)
                        || f.Name.Contains(parameterName, StringComparison.Ordinal))
            .OrderByDescending(f => f.Name.Contains(parameterName, StringComparison.Ordinal))
            .ThenBy(static f => f.Name.Length)
            .FirstOrDefault(f => SymbolEqualityComparer.Default.Equals(f.Type, parameterType));

        if (generatedLikeField is not null)
        {
            return (generatedLikeField.Name, true);
        }

        // 5. Последний разумный вариант: если поле нужного типа ровно одно.
        var candidates = type
            .GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static f => !f.IsStatic)
            .Where(f => SymbolEqualityComparer.Default.Equals(f.Type, parameterType))
            .ToList();

        if (candidates.Count == 1)
        {
            return (candidates[0].Name, true);
        }

        return (fallbackName, false);
    }
}