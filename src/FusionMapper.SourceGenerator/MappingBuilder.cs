using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace FusionMapper.SourceGenerator;

class MappingBuilder
{
    private readonly Compilation compilation;
    readonly Dictionary<(TypeModel Source, TypeModel Target), Mapping> mappings = [];

    private MappingBuilder(Compilation compilation)
    {
        this.compilation = compilation;
    }
    internal static ImmutableDictionary<(TypeModel Source, TypeModel Target), Mapping> CreateMappings(ImmutableArray<RawCandidate> candidates, Compilation compilation, CancellationToken ct)
    {
        var builder = new MappingBuilder(compilation);

        foreach (var c in candidates)
        {
            builder.AddMapping(c.Source, c.Target);
        }
        return builder.mappings.ToImmutableDictionary();
    }

    private void AddMapping(TypeModel source, TypeModel target)
    {
        if(!mappings.TryGetValue((source, target), out _))
        {            
            var sourceType = compilation.GetTypeByMetadataName(source.FullName);
            var targetType = compilation.GetTypeByMetadataName(target.FullName);            
            mappings.Add((source, target), BuildMapping(sourceType, targetType));
        }
        
    }

    private Mapping BuildMapping(INamedTypeSymbol? sourceType, INamedTypeSymbol? targetType)
    {
        // Itentionaly do nothing for now, we will implement this later when we have more information about the source and target types.
        return new();
    }

}