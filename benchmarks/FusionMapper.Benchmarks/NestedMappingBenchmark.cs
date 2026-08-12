using AutoMapper;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using FusionMapper;
using Mapster;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class NestedMappingBenchmark
{
    private NestedSource _source = null!;
    private IMapper _autoMapper = null!;
    private MapperlyMapper _mapperly = null!;

    [GlobalSetup]
    public void Setup()
    {
        _source = new NestedSource
        {
            Name = "Root",
            Level1 = new Level1
            {
                Title = "Child",
                Level2 = new Level2
                {
                    Description = "Grandchild",
                    Value = 42
                }
            }
        };

        _autoMapper = AutoMapperConfig.CreateMapper();
        MapsterConfig.Configure();
        _mapperly = new MapperlyMapper();
    }

    [Benchmark(Baseline = true)]
    public NestedDestination ManualMapping()
    {
        return new NestedDestination
        {
            Name = _source.Name,
            Level1Title = _source.Level1.Title,
            Level1Level2Description = _source.Level1.Level2.Description,
            Level1Level2Value = _source.Level1.Level2.Value
        };
    }

    [Benchmark]
    public NestedDestination FusionMapper()
    {
        return _source.Map().To<NestedDestination>();
    }

    [Benchmark]
    public NestedDestination Mapster()
    {
        return _source.Adapt<NestedDestination>();
    }

    [Benchmark]
    public NestedDestination Mapperly()
    {
        return _mapperly.Map(_source);
    }

    [Benchmark]
    public NestedDestination AutoMapper()
    {
        return _autoMapper.Map<NestedDestination>(_source);
    }
}