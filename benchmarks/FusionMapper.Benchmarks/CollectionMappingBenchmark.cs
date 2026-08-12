using AutoMapper;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using FusionMapper;
using Mapster;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class CollectionMappingBenchmark
{
    private CollectionSource _source = null!;
    private IMapper _autoMapper = null!;
    private MapperlyMapper _mapperly = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Создаем список из 1000 элементов
        var items = Enumerable.Range(0, 1000)
            .Select(i => new SimpleSource { Id = i, Name = $"Item{i}", Price = i * 1.5m })
            .ToList();
        _source = new CollectionSource { Items = items };

        _autoMapper = AutoMapperConfig.CreateMapper();
        MapsterConfig.Configure();
        _mapperly = new MapperlyMapper();
    }

    [Benchmark(Baseline = true)]
    public CollectionDestination ManualMapping()
    {
        var dest = new CollectionDestination
        {
            Items = [.. _source.Items.Select(s => new SimpleDestination
            {
                Id = s.Id,
                Name = s.Name,
                Price = s.Price
            })]
        };
        return dest;
    }

    [Benchmark]
    public CollectionDestination FusionMapper()
    {
        return _source.Map().To<CollectionDestination>();
    }

    [Benchmark]
    public CollectionDestination Mapster()
    {
        return _source.Adapt<CollectionDestination>();
    }

    [Benchmark]
    public CollectionDestination Mapperly()
    {
        return _mapperly.Map(_source);
    }

    [Benchmark]
    public CollectionDestination AutoMapper()
    {
        return _autoMapper.Map<CollectionDestination>(_source);
    }
}