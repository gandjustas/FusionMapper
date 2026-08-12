using AutoMapper;

// --- Модели для бенчмарка ---
public class SimpleSource
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

public class SimpleDestination
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
}

// --- Настройка AutoMapper ---
public class AutoMapperProfile : Profile
{
    public AutoMapperProfile()
    {
        CreateMap<SimpleSource, SimpleDestination>();
    }
}

// --- Настройка Mapperly ---
[Riok.Mapperly.Abstractions.Mapper]
public partial class MapperlyMapper
{
    public partial SimpleDestination Map(SimpleSource source);
}