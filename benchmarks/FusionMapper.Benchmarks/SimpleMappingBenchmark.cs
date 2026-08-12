using AutoMapper;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using FusionMapper;
using Mapster;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class SimpleMappingBenchmark
{
    private SimpleSource _source = null!;
    private IMapper _autoMapper = null!;
    private MapperlyMapper _mapperly = null!;

    [GlobalSetup]
    public void Setup()
    {
        _source = new SimpleSource { Id = 42, Name = "Benchmark", Price = 99.99m };
        _autoMapper = AutoMapperConfig.CreateMapper();
        MapsterConfig.Configure();
        _mapperly = new MapperlyMapper();
    }

    [Benchmark(Baseline = true)]
    public SimpleDestination ManualMapping()
    {
        return new SimpleDestination
        {
            Id = _source.Id,
            Name = _source.Name,
            Price = _source.Price
        };
    }

    [Benchmark]
    public SimpleDestination FusionMapper()
    {
        return _source.Map().To<SimpleDestination>();
    }

    [Benchmark]
    public SimpleDestination Mapster()
    {
        return _source.Adapt<SimpleDestination>();
    }

    [Benchmark]
    public SimpleDestination Mapperly()
    {
        return _mapperly.Map(_source);
    }

    [Benchmark]
    public SimpleDestination AutoMapper()
    {
        return _autoMapper.Map<SimpleDestination>(_source);
    }
}