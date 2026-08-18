using System.Collections;
using System.Diagnostics.CodeAnalysis;

namespace FusionMapper.Tests;

public class CoverageGapTests
{
    #region Root mapping gaps

#if !FUSION_MAPPER_SOURCE_GENERATOR
    [Test]
    public async Task Map_String_To_Int_Throws_MappingException()
    {
        await Assert.That(() => "abc".Map().To<int>())
            .Throws<MappingException>();
    }
#endif

    [Test]
    public async Task Map_Null_Object_Source_To_NonNullable_Int_Throws()
    {
        object? source = null;

        await Assert.That(() => source.Map().To<int>())
            .Throws<InvalidOperationException>();
    }

#if !FUSION_MAPPER_SOURCE_GENERATOR
    [Test]
    public async Task Map_List_String_To_List_Int_Throws_MappingException()
    {
        var source = new List<string> { "a" };

        await Assert.That(() => source.Map().To<List<int>>())
            .Throws<MappingException>();
    }
#endif


    [Test]
    public async Task Map_Assignable_Source_To_Base_Target_Returns_Same_Reference()
    {
        var source = new AssignableSource
        {
            Name = "same"
        };

        var result = source.Map().To<AssignableTarget>();

        await Assert.That(ReferenceEquals(result, source)).IsTrue();
        await Assert.That(result.Name).IsEqualTo("same");
    }

    [Test]
    public async Task Map_Base_Source_To_Derived_Target_Throws_InvalidCast()
    {
        var source = new DowncastSource
        {
            Name = "base"
        };

        await Assert.That(() => source.Map().To<DowncastTarget>())
            .Throws<InvalidCastException>();
    }

#endregion

    #region Impossible member skip

    [Test]
    public async Task Impossible_Member_Is_Skipped_In_Creation()
    {
        var source = new SkipImpossibleSource
        {
            A = 1,
            Bad = "text"
        };

        var result = source.Map().To<SkipImpossibleTarget>();

        await Assert.That(result.A).IsEqualTo(1);
        await Assert.That(result.Bad).IsEqualTo(0);
    }

    [Test]
    public async Task Impossible_Member_Is_Skipped_In_Assignment()
    {
        var source = new SkipImpossibleSource
        {
            A = 1,
            Bad = "text"
        };

        var target = new SkipImpossibleTarget
        {
            A = 0,
            Bad = 7
        };

        source.Map().To(target);

        await Assert.That(target.A).IsEqualTo(1);
        await Assert.That(target.Bad).IsEqualTo(7);
    }

    #endregion

    #region Constructor selection gaps

    [Test]
    public async Task Constructor_With_Nullable_Unmapped_Parameter_Uses_Null()
    {
        var source = new CtorNullableSource
        {
            Name = "n"
        };

        var result = source.Map().To<CtorNullableTarget>();

        await Assert.That(result.Name).IsEqualTo("n");
        await Assert.That(result.Description).IsNull();
    }

    [Test]
    public async Task Map_Chooses_Constructor_With_Most_Mapped_Parameters()
    {
        var source = new MultiCtorSource
        {
            Name = "n",
            Value = 5
        };

        var result = source.Map().To<MultiCtorTarget>();

        await Assert.That(result.Kind).IsEqualTo(2);
        await Assert.That(result.Name).IsEqualTo("n");
        await Assert.That(result.Value).IsEqualTo(5);
    }

    [Test]
    public async Task Map_Falls_Back_To_Constructor_With_Fewer_Parameters()
    {
        var source = new FallbackCtorSource
        {
            Name = "n"
        };

        var result = source.Map().To<FallbackCtorTarget>();

        await Assert.That(result.Kind).IsEqualTo(1);
        await Assert.That(result.Name).IsEqualTo("n");
    }

    [Test]
    public async Task Constructor_With_SetsRequiredMembers_Allows_Missing_Required_Member()
    {
        var source = new SetsRequiredSource
        {
            Name = "required"
        };

        var result = source.Map().To<SetsRequiredTarget>();

        await Assert.That(result.Name).IsEqualTo("required");
    }

    #endregion

    #region Required fields and public fields

    [Test]
    public async Task Map_Required_Field_Success()
    {
        var source = new RequiredFieldSource
        {
            Name = "field"
        };

        var result = source.Map().To<RequiredFieldTarget>();

        await Assert.That(result.Name).IsEqualTo("field");
    }

#if !FUSION_MAPPER_SOURCE_GENERATOR
    [Test]
    public async Task Map_Required_Field_Missing_Source_Throws()
    {
        var source = new RequiredFieldMissingSource
        {
            Title = "title"
        };

        var ex = await Assert.That(() => source.Map().To<RequiredFieldTarget>())
            .Throws<MappingException>();

        await Assert.That(ex!.Message.Contains("Name", StringComparison.OrdinalIgnoreCase)).IsTrue();
    }
#endif

    [Test]
    public async Task Map_Target_Public_Field_From_Source_Property()
    {
        var source = new TargetFieldSource
        {
            Name = "field-value"
        };

        var result = source.Map().To<TargetFieldTarget>();

        await Assert.That(result.Name).IsEqualTo("field-value");
    }

    [Test]
    public async Task Map_To_Existing_Should_Mutate_ReadOnly_Reference_Field()
    {
        var source = new ReadOnlyFieldSource
        {
            Inner = new ReadOnlyFieldInnerSource
            {
                Value = 42
            }
        };

        var target = new ReadOnlyFieldTarget();

        source.Map().To(target);

        await Assert.That(target.Inner.Value).IsEqualTo(42);
    }

#endregion

    #region Flattening and member resolution

    [Test]
    public async Task Map_Target_Underscore_Property_Maps_To_Source_Property()
    {
        var source = new UnderscoreSource
        {
            Name = "underscore"
        };

        var result = source.Map().To<UnderscoreTarget>();

        await Assert.That(result._Name).IsEqualTo("underscore");
    }

    [Test]
    public async Task Map_Direct_Member_Wins_Over_Flattened_Path()
    {
        var source = new DirectWinsSource
        {
            CustomerName = "direct",
            Customer = new DirectWinsCustomer
            {
                Name = "nested"
            }
        };

        var result = source.Map().To<DirectWinsTarget>();

        await Assert.That(result.CustomerName).IsEqualTo("direct");
    }

    [Test]
    public async Task Map_Flattening_Case_Insensitive()
    {
        var source = new CaseFlatSource
        {
            customer = new CaseFlatCustomer
            {
                NAME = "case-insensitive"
            }
        };

        var result = source.Map().To<CaseFlatTarget>();

        await Assert.That(result.CustomerName).IsEqualTo("case-insensitive");
    }

    [Test]
    public async Task Ambiguous_CaseInsensitive_Members_Are_Mapped_Without_Error()
    {
        var source = new AmbiguousSource
        {
            Name = "a",
            NAME = "b"
        };

        var result = source.Map().To<AmbiguousTarget>();

        // Здесь важна сама ветка case-insensitive выбора.
        // Если потребуется детерминированность, нужно отдельно фиксировать правило выбора.
        await Assert.That(result.name).IsNotNull();
    }

    #endregion

    #region Aggregates: All / Any / selector aggregates

    [Test]
    public async Task Map_ItemsActiveAll_Returns_True_When_All_Active()
    {
        var source = new ActiveAggregateSource
        {
            Items =
            [
                new ActiveItem { Active = true },
                new ActiveItem { Active = true }
            ]
        };

        var result = source.Map().To<ActiveAggregateTarget>();

        await Assert.That(result.ItemsActiveAll).IsTrue();
        await Assert.That(result.ItemsActiveAny).IsTrue();
    }

    [Test]
    public async Task Map_ItemsActiveAll_Returns_False_When_Not_All_Active()
    {
        var source = new ActiveAggregateSource
        {
            Items =
            [
                new ActiveItem { Active = true },
                new ActiveItem { Active = false }
            ]
        };

        var result = source.Map().To<ActiveAggregateTarget>();

        await Assert.That(result.ItemsActiveAll).IsFalse();
        await Assert.That(result.ItemsActiveAny).IsTrue();
    }

    [Test]
    public async Task Map_ItemsActiveAll_Empty_Collection_Returns_True_And_Any_False()
    {
        var source = new ActiveAggregateSource();

        var result = source.Map().To<ActiveAggregateTarget>();

        await Assert.That(result.ItemsActiveAll).IsTrue();
        await Assert.That(result.ItemsActiveAny).IsFalse();
    }

    [Test]
    public async Task Map_ItemsValue_Average_Max_Min_With_Selector()
    {
        var source = new AggregateSource();

        source.Items.Add(new ItemSource { Value = 1m });
        source.Items.Add(new ItemSource { Value = 3m });

        var result = source.Map().To<SelectorAggregateTarget>();

        await Assert.That(result.ItemsValueAverage).IsEqualTo(2m);
        await Assert.That(result.ItemsValueMax).IsEqualTo(3m);
        await Assert.That(result.ItemsValueMin).IsEqualTo(1m);
    }

    #endregion

    #region Empty collection aggregates

    [Test]
    public async Task Map_First_On_Empty_Object_Collection_Throws()
    {
        var source = new AggregateSource();

        await Assert.That(() => source.Map().To<ItemsFirstTarget>())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Map_First_On_Empty_Value_Collection_Throws()
    {
        var source = new AggregateSource();

        await Assert.That(() => source.Map().To<PricesFirstTarget>())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Map_Average_On_Empty_Collection_Throws()
    {
        var source = new AggregateSource();

        await Assert.That(() => source.Map().To<PricesAverageTarget>())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Map_Max_On_Empty_Collection_Throws()
    {
        var source = new AggregateSource();

        await Assert.That(() => source.Map().To<PricesMaxTarget>())
            .Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Map_Min_On_Empty_Collection_Throws()
    {
        var source = new AggregateSource();

        await Assert.That(() => source.Map().To<PricesMinTarget>())
            .Throws<InvalidOperationException>();
    }

    #endregion

    #region Collection creation: Add / AddRange / no-add

    [Test]
    public async Task Map_List_To_Custom_AddRange_Collection()
    {
        var source = new List<string>
        {
            "a",
            "b"
        };

        var result = source.Map().To<AddRangeCollection<string>>();

        await Assert.That(result.Values.Count).IsEqualTo(2);
        await Assert.That(result.Values).Contains("a");
        await Assert.That(result.Values).Contains("b");
    }

    [Test]
    public async Task Map_List_To_Custom_AddOnly_Collection()
    {
        var source = new List<string>
        {
            "a",
            "b"
        };

        var result = source.Map().To<AddOnlyCreateCollection<string>>();

        await Assert.That(result.Values.Count).IsEqualTo(2);
        await Assert.That(result.Values).Contains("a");
        await Assert.That(result.Values).Contains("b");
    }

    [Test]
    public async Task Map_List_To_Collection_Without_Add_Or_AddRange_Throws()
    {
        var source = new List<string>
        {
            "a"
        };

        await Assert.That(() => source.Map().To<NoAddCollection<string>>())
            .Throws<MappingException>();
    }

    #endregion

    #region Existing collection edge cases

    [Test]
    public async Task Existing_Collection_With_Impossible_Element_Mapping_Is_Skipped()
    {
        var source = new BadElementListSource
        {
            A = 5,
            Items =
            [
                new BadElementSource()
            ]
        };

        var target = new BadElementListTarget
        {
            A = 0,
            Items =
            [
                new BadElementTarget
                {
                    X = 7
                }
            ]
        };

        source.Map().To(target);

        await Assert.That(target.A).IsEqualTo(5);
        await Assert.That(target.Items.Count).IsEqualTo(1);
        await Assert.That(target.Items[0].X).IsEqualTo(7);
    }

    [Test]
    public async Task Existing_Target_Source_Null_Collection_Leaves_Target_Collection_Unchanged()
    {
        var source = new NullableListSource
        {
            A = 5,
            Items = null
        };

        var target = new NullableListTarget
        {
            A = 0,
            Items = ["old"]
        };

        source.Map().To(target);

        await Assert.That(target.A).IsEqualTo(5);
        await Assert.That(target.Items).Contains("old");
    }

    [Test]
    public async Task Existing_Target_Null_Collection_Property_Is_Created_From_Source()
    {
        var source = new NullableListSource
        {
            A = 5,
            Items = ["new"]
        };

        var target = new NullableListTarget
        {
            A = 0,
            Items = null
        };

        source.Map().To(target);

        await Assert.That(target.A).IsEqualTo(5);
        await Assert.That(target.Items).IsNotNull();
        await Assert.That(target.Items).Contains("new");
    }

    #endregion

    #region Existing read-only object mutation

    [Test]
    public async Task Existing_Target_ReadOnly_Object_Property_Is_Mutated()
    {
        var source = new RoObjSource
        {
            Inner = new RoInnerSource
            {
                Value = 42
            }
        };

        var target = new RoObjTarget();

        source.Map().To(target);

        await Assert.That(target.Inner.Value).IsEqualTo(42);
    }

    [Test]
    public async Task Existing_Target_ReadOnly_Object_Property_With_Null_Source_Is_Skipped()
    {
        var source = new RoObjSource
        {
            Inner = null
        };

        var target = new RoObjTarget();
        target.Inner.Value = 7;

        source.Map().To(target);

        await Assert.That(target.Inner.Value).IsEqualTo(7);
    }

    [Test]
    public async Task Existing_Target_ReadOnly_Object_Property_When_Null_Is_Skipped()
    {
        var source = new RoObjSource
        {
            Inner = new RoInnerSource
            {
                Value = 42
            }
        };

        var target = new RoObjNullTarget();

        source.Map().To(target);

        await Assert.That(target.Inner).IsNull();
    }

    #endregion

    #region Assignment conversions

    [Test]
    public async Task Existing_Target_String_To_Enum_Is_Converted()
    {
        var source = new StringEnumSource
        {
            Color = "Green"
        };

        var target = new StringEnumTarget();

        source.Map().To(target);

        await Assert.That(target.Color).IsEqualTo(LocalColor.Green);
    }

    [Test]
    public async Task Existing_Target_String_To_Enum_Invalid_Value_Throws()
    {
        var source = new StringEnumSource
        {
            Color = "Invalid"
        };

        var target = new StringEnumTarget();

        await Assert.That(() => source.Map().To(target))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task Existing_Target_Nullable_Int_With_Value_Updates_Target()
    {
        var source = new NullableAssignSource
        {
            Value = 5
        };

        var target = new NullableAssignTarget
        {
            Value = 0
        };

        source.Map().To(target);

        await Assert.That(target.Value).IsEqualTo(5);
    }

    [Test]
    public async Task Existing_Target_Nullable_Int_Null_Leaves_Target()
    {
        var source = new NullableAssignSource
        {
            Value = null
        };

        var target = new NullableAssignTarget
        {
            Value = 7
        };

        source.Map().To(target);

        await Assert.That(target.Value).IsEqualTo(7);
    }

    #endregion

    #region ExpressionRewriter negative and missing branches

    [Test]
    public async Task Rewrite_Null_Query_Throws()
    {
        await Assert.That(() => ExpressionRewriter.Rewrite<SimpleTarget>(null!))
            .Throws<ArgumentNullException>();
    }

#if !FUSION_MAPPER_SOURCE_GENERATOR

    [Test]
    public async Task Rewrite_Map_To_Existing_Target_Throws()
    {
        var source = new[]
        {
            new SimpleSource()
        }.AsQueryable();

        var existing = new SimpleTarget();

        var query = source
            .Select(x => x.Map().To(existing));

        await Assert.That(() => ExpressionRewriter.Rewrite(query).ToList())
            .Throws<MappingException>();
    }
#endif


    [Test]
    public async Task Rewrite_Map_Without_To_Throws()
    {
        var source = new[]
        {
            new SimpleSource()
        }.AsQueryable();

        var query = source
            .Select(x => x.Map());

        await Assert.That(() => ExpressionRewriter.Rewrite(query).ToList())
            .Throws<MappingException>();
    }

    [Test]
    public async Task Rewrite_Project_Without_To_Throws()
    {
        var source = new[]
        {
            new SimpleSource()
        }.AsQueryable();

        var query = source
            .Select(x => FusionMapper.Project(source));

        await Assert.That(() => ExpressionRewriter.Rewrite(query).ToList())
            .Throws<MappingException>();
    }

    [Test]
    public async Task Rewrite_Project_To_Inside_Expression_Is_Rewritten()
    {
        var source = new[]
        {
            new SimpleSource
            {
                Name = "A"
            }
        }.AsQueryable();

        var query = new[] { 0 }
            .AsQueryable()
            .Select(_ => FusionMapper
                .Project(source)
                .To<SimpleTarget>()
                .FirstOrDefault());

        var rewritten = ExpressionRewriter.Rewrite(query);
        var result = rewritten.ToList();

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0]!.Name).IsEqualTo("A");
        await Assert.That(
            ExpressionHelper.ContainsMethodName(rewritten.Expression, "Project")
        ).IsFalse();
    }

    [Test]
    public async Task Rewrite_Map_With_Derived_Source_To_Base_Parameter_Uses_Convert()
    {
        var source = new[]
        {
            new RewriteDerivedSource
            {
                Name = "A"
            }
        }.AsQueryable();

        var query = source
            .Select(x => FusionMapper
                .Map<RewriteBaseSource>(x)
                .To<RewriteBaseTarget>());

        var rewritten = ExpressionRewriter.Rewrite(query);
        var result = rewritten.ToList();

        await Assert.That(result.Count).IsEqualTo(1);
        await Assert.That(result[0].Name).IsEqualTo("A");
    }

#endregion


    #region Models

    public class SkipImpossibleSource
    {
        public int A { get; set; }
        public string Bad { get; set; } = string.Empty;
    }

    public class SkipImpossibleTarget
    {
        public int A { get; set; }
        public int Bad { get; set; }
    }

    public class CtorNullableSource
    {
        public string Name { get; set; } = string.Empty;
    }

    public class CtorNullableTarget
    {
        public CtorNullableTarget(string name, string? description)
        {
            Name = name;
            Description = description;
        }

        public string Name { get; }
        public string? Description { get; }
    }

    public class MultiCtorSource
    {
        public string Name { get; set; } = string.Empty;
        public int Value { get; set; }
    }

    public class MultiCtorTarget
    {
        public MultiCtorTarget(string name)
        {
            Name = name;
            Kind = 1;
        }

        public MultiCtorTarget(string name, int value)
        {
            Name = name;
            Value = value;
            Kind = 2;
        }

        public string Name { get; }
        public int Value { get; }
        public int Kind { get; }
    }

    public class FallbackCtorSource
    {
        public string Name { get; set; } = string.Empty;
    }

    public class FallbackCtorTarget
    {
        public FallbackCtorTarget(string name, int value)
        {
            Name = name;
            Value = value;
            Kind = 2;
        }

        public FallbackCtorTarget(string name)
        {
            Name = name;
            Kind = 1;
        }

        public string Name { get; }
        public int Value { get; }
        public int Kind { get; }
    }

    public class SetsRequiredSource
    {
        public string Name { get; set; } = string.Empty;
    }

    public class SetsRequiredTarget
    {
        [SetsRequiredMembers]
        public SetsRequiredTarget(string name)
        {
            Name = name;
        }

        public required string Name { get; init; }
    }

    public class RequiredFieldSource
    {
        public string Name { get; set; } = string.Empty;
    }

    public class RequiredFieldMissingSource
    {
        public string Title { get; set; } = string.Empty;
    }

    public class RequiredFieldTarget
    {
        public required string Name;
    }

    public class TargetFieldSource
    {
        public string Name { get; set; } = string.Empty;
    }

    public class TargetFieldTarget
    {
        public string Name = string.Empty;
    }

    public class ReadOnlyFieldInnerSource
    {
        public int Value { get; set; }
    }

    public class ReadOnlyFieldInnerTarget
    {
        public int Value { get; set; }
    }

    public class ReadOnlyFieldSource
    {
        public ReadOnlyFieldInnerSource Inner { get; set; } = new();
    }

    public class ReadOnlyFieldTarget
    {
        public readonly ReadOnlyFieldInnerTarget Inner = new();
    }

    public class UnderscoreSource
    {
        public string Name { get; set; } = string.Empty;
    }

    public class UnderscoreTarget
    {
        public string _Name { get; set; } = string.Empty;
    }

    public class DirectWinsSource
    {
        public string CustomerName { get; set; } = string.Empty;
        public DirectWinsCustomer Customer { get; set; } = new();
    }

    public class DirectWinsCustomer
    {
        public string Name { get; set; } = string.Empty;
    }

    public class DirectWinsTarget
    {
        public string CustomerName { get; set; } = string.Empty;
    }

    public class CaseFlatSource
    {
        public CaseFlatCustomer customer { get; set; } = new();
    }

    public class CaseFlatCustomer
    {
        public string NAME { get; set; } = string.Empty;
    }

    public class CaseFlatTarget
    {
        public string CustomerName { get; set; } = string.Empty;
    }

    public class ActiveItem
    {
        public bool Active { get; set; }
    }

    public class ActiveAggregateSource
    {
        public List<ActiveItem> Items { get; set; } = [];
    }

    public class ActiveAggregateTarget
    {
        public bool ItemsActiveAll { get; set; }
        public bool ItemsActiveAny { get; set; }
    }

    public class SelectorAggregateTarget
    {
        public decimal ItemsValueAverage { get; set; }
        public decimal ItemsValueMax { get; set; }
        public decimal ItemsValueMin { get; set; }
    }

    public class ItemsFirstTarget
    {
        public ItemTarget ItemsFirst { get; set; } = new();
    }

    public class PricesFirstTarget
    {
        public decimal PricesFirst { get; set; }
    }

    public class PricesAverageTarget
    {
        public decimal PricesAverage { get; set; }
    }

    public class PricesMaxTarget
    {
        public decimal PricesMax { get; set; }
    }

    public class PricesMinTarget
    {
        public decimal PricesMin { get; set; }
    }

    public class AddRangeCollection<T> : IEnumerable<T>
    {
        private readonly List<T> _items = [];

        public IReadOnlyList<T> Values => _items;

        public void AddRange(IEnumerable<T> items) => _items.AddRange(items);

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public class AddOnlyCreateCollection<T> : IEnumerable<T>
    {
        private readonly List<T> _items = [];

        public IReadOnlyList<T> Values => _items;

        public void Add(T item) => _items.Add(item);

        public IEnumerator<T> GetEnumerator() => _items.GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public class NoAddCollection<T> : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => Enumerable.Empty<T>().GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    public class BadElementSource
    {
        public string Y { get; set; } = string.Empty;
    }

    public class BadElementTarget
    {
        public required int X { get; set; }
    }

    public class BadElementListSource
    {
        public int A { get; set; }
        public List<BadElementSource> Items { get; set; } = [];
    }

    public class BadElementListTarget
    {
        public int A { get; set; }
        public List<BadElementTarget> Items { get; set; } = [];
    }

    public class NullableListSource
    {
        public int A { get; set; }
        public List<string>? Items { get; set; }
    }

    public class NullableListTarget
    {
        public int A { get; set; }
        public List<string>? Items { get; set; }
    }

    public class RoInnerSource
    {
        public int Value { get; set; }
    }

    public class RoInnerTarget
    {
        public int Value { get; set; }
    }

    public class RoObjSource
    {
        public RoInnerSource? Inner { get; set; }
    }

    public class RoObjTarget
    {
        public RoInnerTarget Inner { get; } = new();
    }

    public class RoObjNullTarget
    {
        public RoInnerTarget Inner { get; } = null!;
    }

    public enum LocalColor
    {
        Red,
        Green
    }

    public class StringEnumSource
    {
        public string Color { get; set; } = string.Empty;
    }

    public class StringEnumTarget
    {
        public LocalColor Color { get; set; }
    }

    public class NullableAssignSource
    {
        public int? Value { get; set; }
    }

    public class NullableAssignTarget
    {
        public int Value { get; set; }
    }

    public class AssignableTarget
    {
        public string Name { get; set; } = string.Empty;
    }

    public class AssignableSource : AssignableTarget
    {
        public int Extra { get; set; }
    }

    public class DowncastSource
    {
        public string Name { get; set; } = string.Empty;
    }

    public class DowncastTarget : DowncastSource
    {
        public int Extra { get; set; }
    }

    public class RewriteBaseSource
    {
        public string Name { get; set; } = string.Empty;
    }

    public class RewriteDerivedSource : RewriteBaseSource
    {
        public int Extra { get; set; }
    }

    public class RewriteBaseTarget
    {
        public string Name { get; set; } = string.Empty;
    }

    #endregion
}