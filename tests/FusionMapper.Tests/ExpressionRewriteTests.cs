namespace FusionMapper.Tests;

public class ExpressionRewriteTests
{
    [Test]
    public async Task ProjectTo_Rewrites_Map_To_In_IQueryable()
    {
        var source = new[]
        {
            new SimpleSource { Name = "A", Value = 1 },
            new SimpleSource { Name = "B", Value = 2 }
        }.AsQueryable();

        var query = source
            .Select(x => x.Map().To<SimpleTarget>());

        await Assert.That(
            ExpressionHelper.ContainsMethodName(query.Expression, "Map")
        ).IsTrue();

        var rewritten = query
            .Project()
            .To<SimpleTarget>();

        var result = rewritten.ToList();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Name).IsEqualTo("A");
        await Assert.That(result[1].Name).IsEqualTo("B");

        await Assert.That(
            ExpressionHelper.ContainsMethodName(rewritten.Expression, "Map")
        ).IsFalse();
    }

    [Test]
    public async Task ProjectTo_Rewrites_Map_To_With_Where()
    {
        var source = new[]
        {
            new SimpleSource { Name = "A", Value = 1 },
            new SimpleSource { Name = "B", Value = 2 },
            new SimpleSource { Name = "C", Value = 3 }
        }.AsQueryable();

        var filteredSource = source.Where(x => x.Value >= 2);

        var query = filteredSource
            .Select(x => x.Map().To<SimpleTarget>());

        await Assert.That(
            ExpressionHelper.ContainsMethodName(query.Expression, "Map")
        ).IsTrue();

        var rewritten = query
            .Project()
            .To<SimpleTarget>();

        var result = rewritten.ToList();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0].Name).IsEqualTo("B");
        await Assert.That(result[1].Name).IsEqualTo("C");

        await Assert.That(
            ExpressionHelper.ContainsMethodName(rewritten.Expression, "Map")
        ).IsFalse();
    }

    [Test]
    public async Task ProjectTo_Rewrites_Map_To_In_Nested_Member_Access()
    {
        var source = new[]
        {
            new SimpleSource { Name = "A", Value = 1 },
            new SimpleSource { Name = "B", Value = 2 }
        }.AsQueryable();

        var query = source
            .Select(x => x.Map().To<SimpleTarget>().Name);

        await Assert.That(
            ExpressionHelper.ContainsMethodName(query.Expression, "Map")
        ).IsTrue();

        var rewritten = query
            .Project()
            .To<string>();

        var result = rewritten.ToList();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]).IsEqualTo("A");
        await Assert.That(result[1]).IsEqualTo("B");

        await Assert.That(
            ExpressionHelper.ContainsMethodName(rewritten.Expression, "Map")
        ).IsFalse();
    }

    [Test]
    public async Task ProjectTo_Rewrites_Multiple_Map_Calls_In_One_Expression()
    {
        var source = new[]
        {
            new SimpleSource { Name = "A", Value = 1 },
            new SimpleSource { Name = "B", Value = 2 }
        }.AsQueryable();

        var query = source
            .Select(x => new SimpleTarget
            {
                Name = x.Map().To<SimpleTarget>().Name,
                Value = x.Map().To<SimpleTarget>().Value
            });

        await Assert.That(
            ExpressionHelper.ContainsMethodName(query.Expression, "Map")
        ).IsTrue();

        var rewritten = query
            .Project()
            .To<SimpleTarget>();

        var result = rewritten.ToList();

        await Assert.That(result.Count).IsEqualTo(2);

        await Assert.That(result[0].Name).IsEqualTo("A");
        await Assert.That(result[0].Value).IsEqualTo(1);

        await Assert.That(result[1].Name).IsEqualTo("B");
        await Assert.That(result[1].Value).IsEqualTo(2);

        await Assert.That(
            ExpressionHelper.ContainsMethodName(rewritten.Expression, "Map")
        ).IsFalse();
    }

    [Test]
    public async Task ProjectTo_Does_Not_Mutate_Original_IQueryable_Expression()
    {
        var source = new[]
        {
            new SimpleSource { Name = "A", Value = 1 }
        }.AsQueryable();

        var query = source
            .Select(x => x.Map().To<SimpleTarget>());

        var originalExpression = query.Expression;

        var rewritten = query
            .Project()
            .To<SimpleTarget>();

        await Assert.That(
            ExpressionHelper.ContainsMethodName(originalExpression, "Map")
        ).IsTrue();

        await Assert.That(
            ExpressionHelper.ContainsMethodName(rewritten.Expression, "Map")
        ).IsFalse();

        await Assert.That(
            ReferenceEquals(originalExpression, rewritten.Expression)
        ).IsFalse();
    }

    [Test]
    public async Task ProjectTo_Without_Map_Or_Project_Calls_Does_Not_Break_Query()
    {
        var source = new[]
        {
            new SimpleSource { Name = "A", Value = 1 },
            new SimpleSource { Name = "B", Value = 2 }
        }.AsQueryable();

        var query = source.Select(x => x.Name);

        var rewritten = query
            .Project()
            .To<string>();

        await Assert.That(
            ExpressionHelper.ContainsMethodName(rewritten.Expression, "Map")
        ).IsFalse();

        var result = rewritten.ToList();

        await Assert.That(result.Count).IsEqualTo(2);
        await Assert.That(result[0]).IsEqualTo("A");
    }

}
