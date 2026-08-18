namespace FusionMapper.Tests;

public class RecursiveMappingTests
{

#if !FUSION_MAPPER_SOURCE_GENERATOR
    [Test]
    public async Task Map_Direct_Recursive_Type_Throws()
    {
        var source = new NodeSource
        {
            Name = "Root",
            Child = new NodeSource
            {
                Name = "Child"
            }
        };

        await Assert.That(() => source.Map().To<NodeTarget>())
            .Throws<MappingException>();
    }

    [Test]
    public async Task Map_Indirect_Recursive_Type_Throws()
    {
        var source = new IndirectSourceA
        {
            Name = "A",
            Bs =
        [
            new ()
            {
                Name = "B",
                As =
                [
                    new () { Name = "A2" }
                ]
            }
        ]
        };

        await Assert.That(() => source.Map().To<IndirectTargetA>())
            .Throws<MappingException>();
    }

    [Test]
    public async Task Map_Recursive_Collection_Element_Throws()
    {
        var source = new CycleSource
        {
            Name = "Parent",
            Children =
            [
                new() { Name = "Child" }
            ]
        };

        // CycleSource.Children содержит элементы того же типа,
        // что и контейнер → рекурсивный маппинг.
        await Assert.That(() => source.Map().To<CycleTarget>())
            .Throws<MappingException>();
    }
#endif


    public class IndirectSourceA
    {
        public string Name { get; set; } = string.Empty;
        public List<IndirectSourceB>? Bs { get; set; }
    }

    public class IndirectSourceB
    {
        public string Name { get; set; } = string.Empty;
        public List<IndirectSourceA>? As { get; set; }
    }

    public class IndirectTargetA
    {
        public string Name { get; set; } = string.Empty;
        public List<IndirectTargetB>? Bs { get; set; }
    }

    public class IndirectTargetB
    {
        public string Name { get; set; } = string.Empty;
        public List<IndirectTargetA>? As { get; set; }
    }
}
