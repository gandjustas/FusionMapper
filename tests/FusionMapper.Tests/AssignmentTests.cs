namespace FusionMapper.Tests;

public class AssignmentTests
{
    [Test]
    public async Task SourceNull_LeavesTarget_AsIs()
    {
        var target = new PersonTarget { Name = "old" };

        var result = FusionMapper<PersonSource, PersonTarget>.Map(null!, target);

        await Assert.That(ReferenceEquals(result, target)).IsTrue();
        await Assert.That(result.Name).IsEqualTo("old");
    }

    [Test]
    public async Task SourceNull_DoesNotRequireMappingToBePossible()
    {
        var target = new NothingTarget { B = "old" };

        var result = FusionMapper<NothingSource, NothingTarget>.Map(null!, target);

        await Assert.That(ReferenceEquals(result, target)).IsTrue();
        await Assert.That(result.B).IsEqualTo("old");
    }

    [Test]
    public async Task TargetNull_CreatesNewObject_FromSource()
    {
        var source = new PersonSource { Name = "new" };

        var result = source.Map().To<PersonTarget?>(null);

        await Assert.That(result is not null).IsTrue();
        await Assert.That(result!.Name).IsEqualTo("new");
    }

    [Test]
    public async Task ExistingTarget_FillsProperties_Recursively()
    {
        var inner = new RecursiveTargetInner { Value = 0 };

        var target = new RecursiveTarget
        {
            Name = "old",
            Inner = inner
        };

        var source = new RecursiveSource
        {
            Name = "new",
            Inner = new RecursiveSourceInner
            {
                Value = 42
            }
        };

        var result = source.Map().To(target);

        await Assert.That(ReferenceEquals(result, target)).IsTrue();
        await Assert.That(result.Name).IsEqualTo("new");

        // Существующий вложенный объект должен быть заполнен, а не заменён.
        await Assert.That(ReferenceEquals(result.Inner, inner)).IsTrue();
        await Assert.That(result.Inner!.Value).IsEqualTo(42);
    }

    [Test]
    public async Task ExistingTarget_NullNestedObject_IsCreatedAndAssigned()
    {
        var target = new RecursiveTarget
        {
            Name = "old",
            Inner = null
        };

        var source = new RecursiveSource
        {
            Name = "new",
            Inner = new RecursiveSourceInner
            {
                Value = 42
            }
        };

        var result = source.Map().To(target);

        await Assert.That(result.Inner is not null).IsTrue();
        await Assert.That(result.Inner!.Value).IsEqualTo(42);
    }

    [Test]
    public async Task WritableCollection_IsClearedAndFilled_WithSameCollectionReference()
    {
        var originalList = new List<ItemTarget>
        {
            new() { Id = 999 }
        };

        var target = new ListTarget
        {
            Items = originalList
        };

        var source = new ListSource
        {
            Items =
            [
                new() { Id = 1 },
                new() { Id = 2 }
            ]
        };

        var result = source.Map().To(target);

        await Assert.That(ReferenceEquals(result.Items, originalList)).IsTrue();
        await Assert.That(result.Items.Count).IsEqualTo(2);
        await Assert.That(result.Items[0].Id).IsEqualTo(1);
        await Assert.That(result.Items[1].Id).IsEqualTo(2);
    }

    [Test]
    public async Task RootList_IsClearedAndFilled()
    {
        var target = new List<ItemTarget>
        {
            new() { Id = 999 }
        };

        var source = new List<ItemSource>
        {
            new() { Id = 1 }
        };

        var result = source.Map().To(target);

        await Assert.That(ReferenceEquals(result, target)).IsTrue();
        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Id).IsEqualTo(1);
    }

    [Test]
    public async Task ReadOnlyCollection_IsMutated_WhenTargetCollectionExists()
    {
        var target = new ReadOnlyListTarget();

        var originalList = target.Items;
        originalList.Add(new ItemTarget { Id = 999 });

        var source = new ReadOnlyListSource
        {
            Items =
            [
                new() { Id = 1 }
            ]
        };

        var result = source.Map().To(target);

        await Assert.That(ReferenceEquals(result.Items, originalList)).IsTrue();
        await Assert.That(result.Items.Count).IsEqualTo(1);
        await Assert.That(result.Items[0].Id).IsEqualTo(1);
    }

    [Test]
    public async Task ReadOnlyCollection_WhenNull_IsSkippedWithoutError()
    {
        var target = new ReadOnlyNullTarget();

        var source = new ReadOnlyListSource
        {
            Items =
            [
                new() { Id = 1 }
            ]
        };

        var result = source.Map().To(target);

        await Assert.That(result.Items is null).IsTrue();
    }

    [Test]
    public async Task ArrayProperty_ExistingArray_IsSkipped()
    {
        var originalArray = new[] { 1, 2, 3 };

        var target = new ArrayTarget
        {
            A = 0,
            Values = originalArray
        };

        var source = new ArraySource
        {
            A = 5,
            Values = [9]
        };

        var result = source.Map().To(target);

        await Assert.That(result.A).IsEqualTo(5);

        // Массив нельзя очистить через Clear/Add, поэтому существующий массив пропускаем.
        await Assert.That(ReferenceEquals(result.Values, originalArray)).IsTrue();
    }

    [Test]
    public async Task RootArray_ExistingTarget_ThrowsMappingException()
    {
        var target = new[] { 1, 2 };
        var source = new[] { 3, 4 };

        await Assert.That(() => source.Map().To(target)).Throws<MappingException>();
    }

    [Test]
    public async Task CollectionWithoutClearOrAdd_ExistingCollection_IsSkipped()
    {
        var originalCollection = new List<int> { 1, 2, 3 };

        var target = new EnumerableTarget
        {
            A = 0,
            Values = originalCollection
        };

        var source = new EnumerableSource
        {
            A = 5,
            Values = [9]
        };

        var result = source.Map().To(target);

        await Assert.That(result.A).IsEqualTo(5);

        // IEnumerable<int> нельзя безопасно очистить/заполнить как существующую коллекцию.
        await Assert.That(ReferenceEquals(result.Values, originalCollection)).IsTrue();
    }

    [Test]
    public async Task NothingMapped_ForExistingTarget_ThrowsMappingException()
    {
        var source = new NothingSource
        {
            A = 1
        };

        var target = new NothingTarget
        {
            B = "old"
        };

        await Assert.That(() => source.Map().To(target)).Throws<MappingException>();
    }

    [Test]
    public async Task NothingMapped_ForNullTarget_CreatesNewTarget()
    {
        var source = new NothingSource
        {
            A = 1
        };

        var result = source.Map().To<NothingTarget>(null!);

        await Assert.That(result is not null).IsTrue();
    }

    [Test]
    public async Task ImpossibleMember_IsSkipped_WhenOtherMembersAreMapped()
    {
        var target = new MixedTarget
        {
            A = 0,
            B = "old"
        };

        var source = new MixedSource
        {
            A = 10
        };

        var result = source.Map().To(target);

        await Assert.That(result.A).IsEqualTo(10);

        // Для B нет source-члена, поэтому свойство пропускается.
        await Assert.That(result.B).IsEqualTo("old");
    }

    [Test]
    public async Task ExistingStringProperty_FromEnum_IsConverted()
    {
        var source = new EnumSource
        {
            Color = ConsoleColor.Red
        };

        var target = new StringTarget
        {
            Color = "old"
        };

        var result = FusionMapper<EnumSource, StringTarget>.Map(source, target);

        await Assert.That(result.Color).IsEqualTo("Red");
    }

    public class EnumSource
    {
        public ConsoleColor Color { get; set; }
    }

    public class StringTarget
    {
        public string? Color { get; set; }
    }

    public class PersonSource
    {
        public string? Name { get; set; }
    }

    public class PersonTarget
    {
        public string? Name { get; set; }
    }

    public class RecursiveSource
    {
        public string? Name { get; set; }
        public RecursiveSourceInner? Inner { get; set; }
    }

    public class RecursiveSourceInner
    {
        public int Value { get; set; }
    }

    public class RecursiveTarget
    {
        public string? Name { get; set; }
        public RecursiveTargetInner? Inner { get; set; }
    }

    public class RecursiveTargetInner
    {
        public int Value { get; set; }
    }

    public class ItemSource
    {
        public int Id { get; set; }
    }

    public class ItemTarget
    {
        public int Id { get; set; }
    }

    public class ListSource
    {
        public List<ItemSource> Items { get; set; } = [];
    }

    public class ListTarget
    {
        public List<ItemTarget> Items { get; set; } = [];
    }

    public class ReadOnlyListSource
    {
        public List<ItemSource> Items { get; set; } = [];
    }

    public class ReadOnlyListTarget
    {
        public List<ItemTarget> Items { get; } = [];
    }

    public class ReadOnlyNullTarget
    {
        public List<ItemTarget> Items { get; } = null!;
    }

    public class ArraySource
    {
        public int A { get; set; }
        public int[] Values { get; set; } = [];
    }

    public class ArrayTarget
    {
        public int A { get; set; }
        public int[] Values { get; set; } = [];
    }

    public class EnumerableSource
    {
        public int A { get; set; }
        public IEnumerable<int> Values { get; set; } = [];
    }

    public class EnumerableTarget
    {
        public int A { get; set; }
        public IEnumerable<int> Values { get; set; } = [];
    }

    public class NothingSource
    {
        public int A { get; set; }
    }

    public class NothingTarget
    {
        public string? B { get; set; }
    }

    public class MixedSource
    {
        public int A { get; set; }
    }

    public class MixedTarget
    {
        public int A { get; set; }
        public string? B { get; set; }
    }
}