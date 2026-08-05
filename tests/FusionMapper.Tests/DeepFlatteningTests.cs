namespace FusionMapper.Tests;

// Модель для глубокого flattening
public class DeepFlattenTarget
{
    // source.Level2.Title
    public string? Level2Title { get; set; }

    // source.Level2.Level3.Description
    public string? Level2Level3Description { get; set; }

    // source.Level2.Level3.Level4.Value
    public int? Level2Level3Level4Value { get; set; }

    // source.ExtraData.Metadata (альтернативная ветка)
    public string? ExtraDataMetadata { get; set; }
}

public class DeepFlatteningTests
{
    [Test]
    public async Task Map_Deep_Flattening_Success()
    {
        var source = new Level1Source
        {
            Name = "Root",
            Level2 = new Level2Source
            {
                Title = "Child",
                Level3 = new Level3Source
                {
                    Description = "Grandchild",
                    Level4 = new Level4Source
                    {
                        Value = 42
                    }
                }
            },
            ExtraData = new ExtraData
            {
                Metadata = "Meta"
            }
        };

        var result = source.Map().To<DeepFlattenTarget>();

        await Assert.That(result.Level2Title).IsEqualTo("Child");
        await Assert.That(result.Level2Level3Description).IsEqualTo("Grandchild");
        await Assert.That(result.Level2Level3Level4Value).IsEqualTo(42);
        await Assert.That(result.ExtraDataMetadata).IsEqualTo("Meta");
    }

    [Test]
    public async Task Map_Deep_Flattening_With_Null_Intermediate()
    {
        var source = new Level1Source
        {
            Name = "Root",
            Level2 = null, // обрываем цепочку
            ExtraData = new ExtraData { Metadata = "Meta" }
        };

        var result = source.Map().To<DeepFlattenTarget>();

        await Assert.That(result.Level2Title).IsNull();
        await Assert.That(result.Level2Level3Description).IsNull();
        await Assert.That(result.Level2Level3Level4Value).IsNull();
        await Assert.That(result.ExtraDataMetadata).IsEqualTo("Meta");
    }

    [Test]
    public async Task Map_Deep_Flattening_With_Null_Deepest()
    {
        var source = new Level1Source
        {
            Level2 = new Level2Source
            {
                Title = "Child",
                Level3 = new Level3Source
                {
                    Description = "Grandchild",
                    Level4 = null // Level4 отсутствует
                }
            }
        };

        var result = source.Map().To<DeepFlattenTarget>();

        await Assert.That(result.Level2Title).IsEqualTo("Child");
        await Assert.That(result.Level2Level3Description).IsEqualTo("Grandchild");
        await Assert.That(result.Level2Level3Level4Value).IsNull(); // т.к. Level4 == null
    }
}
