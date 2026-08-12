namespace FusionMapper.Tests;

public class PolymorphicMappingTests
{
    // Базовые классы
    private class Animal
    {
        public string Name { get; set; } = string.Empty;
    }

    private class Dog : Animal
    {
        public string Breed { get; set; } = string.Empty;
    }

    private class Cat : Animal
    {
        public int Lives { get; set; }
    }

    // Целевые DTO
    private class AnimalDto
    {
        public string Name { get; set; } = string.Empty;
    }

    private class DogDto : AnimalDto
    {
        public string Breed { get; set; } = string.Empty;
    }

    private class CatDto : AnimalDto
    {
        public int Lives { get; set; }
    }

    [Test]
    public async Task Map_Base_To_Base()
    {
        var source = new Animal { Name = "Generic" };
        var result = source.Map().To<AnimalDto>();
        await Assert.That(result.Name).IsEqualTo("Generic");
    }

    [Test]
    public async Task Map_Derived_To_Derived()
    {
        var source = new Dog { Name = "Rex", Breed = "Labrador" };
        var result = source.Map().To<DogDto>();
        await Assert.That(result.Name).IsEqualTo("Rex");
        await Assert.That(result.Breed).IsEqualTo("Labrador");
    }

    [Test]
    public async Task Map_Derived_To_Base_Should_Lose_Specific_Properties()
    {
        var source = new Dog { Name = "Rex", Breed = "Labrador" };
        var result = source.Map().To<AnimalDto>();
        await Assert.That(result.Name).IsEqualTo("Rex");
        // Свойство Breed не существует в AnimalDto, оно будет проигнорировано
    }

    [Test]
    public async Task Map_List_Of_Animals_With_Mixed_Types_Throws_Or_Ignores()
    {
        var animals = new List<Animal>
        {
            new Dog { Name = "Dog1", Breed = "Poodle" },
            new Cat { Name = "Cat1", Lives = 9 }
        };

        // Если маппер не поддерживает полиморфное маппинг коллекций,
        // он может либо выбросить исключение, либо использовать только базовые свойства.
        // Мы ожидаем, что он замаппит только общие свойства.
        var results = animals.Map().To<List<AnimalDto>>();

        await Assert.That(results.Count).IsEqualTo(2);
        await Assert.That(results[0].Name).IsEqualTo("Dog1");
        await Assert.That(results[1].Name).IsEqualTo("Cat1");
        // Дополнительные свойства (Breed, Lives) будут потеряны, так как целевой тип AnimalDto
        // Это ожидаемо.
    }

    // Если маппер поддерживает полиморфизм с использованием динамического типа,
    // можно добавить тест с использованием интерфейсов.
}