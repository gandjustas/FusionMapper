using AutoMapper;
using Mapster;
using Microsoft.Extensions.Logging.Abstractions;

// --- AutoMapper ---
public class AutoMapperProfile : Profile
{
    public AutoMapperProfile()
    {
        CreateMap<SimpleSource, SimpleDestination>();
        CreateMap<NestedSource, NestedDestination>()
            .ForMember(dest => dest.Level1Title, opt => opt.MapFrom(src => src.Level1.Title))
            .ForMember(dest => dest.Level1Level2Description, opt => opt.MapFrom(src => src.Level1.Level2.Description))
            .ForMember(dest => dest.Level1Level2Value, opt => opt.MapFrom(src => src.Level1.Level2.Value));
        CreateMap<CollectionSource, CollectionDestination>();
    }
}

public static class AutoMapperConfig
{
    public static IMapper CreateMapper() =>
        new MapperConfiguration(cfg => cfg.AddProfile<AutoMapperProfile>(), NullLoggerFactory.Instance).CreateMapper();
}

// --- Mapster ---
public static class MapsterConfig
{
    public static void Configure()
    {
        TypeAdapterConfig<SimpleSource, SimpleDestination>.NewConfig();
        TypeAdapterConfig<NestedSource, NestedDestination>.NewConfig()
            .Map(dest => dest.Level1Title, src => src.Level1.Title)
            .Map(dest => dest.Level1Level2Description, src => src.Level1.Level2.Description)
            .Map(dest => dest.Level1Level2Value, src => src.Level1.Level2.Value);
        TypeAdapterConfig<CollectionSource, CollectionDestination>.NewConfig();
    }
}

// --- Mapperly ---
[Riok.Mapperly.Abstractions.Mapper]
public partial class MapperlyMapper
{
    public partial SimpleDestination Map(SimpleSource source);
    public partial NestedDestination Map(NestedSource source);
    public partial CollectionDestination Map(CollectionSource source);
}