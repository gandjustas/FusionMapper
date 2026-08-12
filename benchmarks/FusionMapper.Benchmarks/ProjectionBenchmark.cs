using AutoMapper;
using AutoMapper.QueryableExtensions;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using FusionMapper;
using Mapster;

[MemoryDiagnoser]
[Orderer(SummaryOrderPolicy.FastestToSlowest)]
[RankColumn]
public class ProjectionBenchmark
{
    private IQueryable<SimpleSource> _sourceQuery = null!;
    private IMapper _autoMapper = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Создаем список из 1000 элементов и преобразуем в IQueryable
        var items = Enumerable.Range(0, 1000)
            .Select(i => new SimpleSource { Id = i, Name = $"Item{i}", Price = i * 1.5m })
            .AsQueryable();

        _sourceQuery = items;

        _autoMapper = AutoMapperConfig.CreateMapper();
        MapsterConfig.Configure();
    }

    // Для проекции ручное маппинг не имеет смысла, поэтому используем Select вручную как Baseline
    [Benchmark(Baseline = true)]
    public List<SimpleDestination> ManualProjection()
    {
        return [.. _sourceQuery
            .Select(s => new SimpleDestination
            {
                Id = s.Id,
                Name = s.Name,
                Price = s.Price
            })];
    }

    [Benchmark]
    public List<SimpleDestination> FusionMapperProjection()
    {
        return [.. _sourceQuery
            .Project()
            .To<SimpleDestination>()];
    }

    [Benchmark]
    public List<SimpleDestination> MapsterProjection()
    {
        return [.. _sourceQuery.ProjectToType<SimpleDestination>()];
    }

    [Benchmark]
    public List<SimpleDestination> AutoMapperProjection()
    {
        return [.. _sourceQuery.ProjectTo<SimpleDestination>(_autoMapper.ConfigurationProvider)];
    }

    // Mapperly не имеет встроенной проекции через IQueryable, поэтому мы пропускаем его.
}