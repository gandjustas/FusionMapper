using AutoMapper;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Order;
using BenchmarkDotNet.Running;
using FusionMapper;
using Mapster;
using Microsoft.Extensions.Logging.Abstractions;

var summary = BenchmarkRunner.Run<MapperBenchmarks>();



[MemoryDiagnoser] // Включает измерение памяти
[Orderer(SummaryOrderPolicy.FastestToSlowest)] // Сортировка результатов от быстрого к медленному
[RankColumn] // Добавляет колонку с рангом производительности
public class MapperBenchmarks
{
    private SimpleSource _source = null!;
    private IMapper _autoMapper = null!;
    private MapperlyMapper _mapperlyMapper = null!;

    // Настройка перед запуском всех бенчмарков
    [GlobalSetup]
    public void Setup()
    {
        // Инициализация источника данных
        _source = new SimpleSource { Id = 42, Name = "Benchmark Object", Price = 99.99m };

        // Настройка AutoMapper
        var config = new MapperConfiguration(cfg => cfg.AddProfile<AutoMapperProfile>(), NullLoggerFactory.Instance);
        _autoMapper = config.CreateMapper();

        // Настройка Mapster (глобально, один раз)
        TypeAdapterConfig<SimpleSource, SimpleDestination>.NewConfig();

        // Mapperly создается автоматически
        _mapperlyMapper = new MapperlyMapper();
    }

    // 1. Эталон: ручное маппинг (самый быстрый)
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

    // 2. FusionMapper 
    [Benchmark]
    public SimpleDestination FusionMapper()
    {
        // Используем синтаксис из ваших тестов
        return _source.Map().To<SimpleDestination>();
    }

    // 3. Mapster
    [Benchmark]
    public SimpleDestination Mapster()
    {
        return _source.Adapt<SimpleDestination>();
    }

    // 4. Mapperly
    [Benchmark]
    public SimpleDestination Mapperly()
    {
        return _mapperlyMapper.Map(_source);
    }

    // 5. AutoMapper
    [Benchmark]
    public SimpleDestination AutoMapper()
    {
        return _autoMapper.Map<SimpleDestination>(_source);
    }
}