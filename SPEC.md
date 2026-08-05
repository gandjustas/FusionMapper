Ниже — полная спека на английском, которую можно давать LLM как системный/проектный контекст, чтобы они не теряли направление и писали код в одном стиле.

# FusionMapper Specification

## 1. Project Goal

FusionMapper is a .NET mapping library that performs fully automatic object-to-object mapping and LINQ projection generation without manual mapping profiles, manual maps, or explicit configuration.

The library must:

- Map objects automatically by convention.
- Build `IQueryable<T>` projections automatically.
- Support constructors, records, `required` members, and `init`-only members.
- Recursively map nested objects.
- Handle recursive object graphs safely.
- Support flattening, for example:

```csharp
source.Category.Name -> target.CategoryName
```

- Support automatic aggregate mapping for collections, for example:

```csharp
source.Items.Count() -> target.ItemsCount
source.Items.Sum(x => x.Price) -> target.ItemsPriceSum
```

- Rewrite calls to `Map().To<T>()` and `Project().To<T>()` inside expression trees into provider-translatable `.Select(...)` calls.
- Use runtime mapping when source generation is unavailable.
- Later, use a Roslyn source generator and interceptors to generate compile-time implementations.

The project must not require the user to define mapping profiles or manual mapping rules in version 1.

---

## 2. Technology Stack

- Language: C#, latest stable or preview features allowed.
- Target: modern .NET.
- Nullable reference types: enabled.
- Test framework: TUnit.
- No external mapping libraries are allowed.
- No AutoMapper, Mapster, or similar dependencies.
- Runtime mapping may use:
  - Reflection.
  - `System.Linq.Expressions`.
  - Compiled expression trees.
- Source generation may use:
  - Roslyn incremental source generators.
  - Interceptors, where supported.
  - Compile-time diagnostics.

---

## 3. Namespaces

Primary library namespace:

```csharp
namespace FusionMapper;
```

Test namespace:

```csharp
namespace FusionMapper.Tests;
```

---

## 4. Current Public Skeleton

The current public API skeleton is:

```csharp
namespace FusionMapper;

public sealed class MappingException : Exception
{
    public MappingException()
    {
    }

    public MappingException(string message)
        : base(message)
    {
    }

    public MappingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
```

```csharp
namespace FusionMapper;

public static class FusionMapper
{
    public static FusionSource<TSource> Map<TSource>(this TSource source)
        => new(source);

    public static FusionProjection<TSource> Project<TSource>(this IQueryable<TSource> source)
        => new(source);
}

public readonly struct FusionSource<TSource>(TSource Value)
{
    public TTarget To<TTarget>()
        => FusionEngine.Map<TSource, TTarget>(Value);

    public TTarget To<TTarget>(TTarget target)
        => FusionEngine.Map(Value, target);
}

public readonly struct FusionProjection<TSource>(IQueryable<TSource> Value)
{
    public IQueryable<TTarget> To<TTarget>()
        => FusionEngine.Project<TSource, TTarget>(Value);
}
```

```csharp
namespace FusionMapper;

internal static class FusionEngine
{
    public static TTarget Map<TSource, TTarget>(TSource source)
        => throw new NotImplementedException(
            "FusionMapper runtime object mapping engine is not implemented yet.");

    public static TTarget Map<TSource, TTarget>(TSource source, TTarget target)
        => throw new NotImplementedException(
            "FusionMapper runtime object mapping engine is not implemented yet.");

    public static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source)
        => throw new NotImplementedException(
            "FusionMapper runtime object mapping engine is not implemented yet.");
}
```

The implementation must preserve this public fluent API.

---

## 5. Authoritative API Behavior

### 5.1. Object Mapping

This call:

```csharp
source.Map().To<TTarget>();
```

must map `source` to a new instance of `TTarget`.

This call:

```csharp
source.Map().To(existingTarget);
```

must map `source` into an existing target object and return the same target instance.

Both forms must also support collection mapping where applicable.

---

### 5.2. Query Projection

This call:

```csharp
queryable.Project().To<TTarget>();
```

must build an expression projection from `TSource` to `TTarget` and return:

```csharp
queryable.Select(projectionExpression)
```

where `projectionExpression` is an `Expression<Func<TSource, TTarget>>`.

The projection expression must not invoke custom mapper methods that a LINQ provider cannot translate.

Good projection shape:

```csharp
source => new TargetDto
{
    Name = source.Name,
    Price = source.Price,
    CategoryName = source.Category != null ? source.Category.Name : null
}
```

Bad projection shape:

```csharp
source => SomeMapper.Map(source)
```

The bad shape is not acceptable for `IQueryable` projection.

---

### 5.3. Expression Tree Rewriting

For expression rewriting, the intended API is:

```csharp
public IQueryable<TTarget> To<TTarget>(IQueryable<TTarget> query)
```

on `FusionProjection<TSource>`.

Target signature:

```csharp
public readonly struct FusionProjection<TSource>(IQueryable<TSource> Value)
{
    public IQueryable<TTarget> To<TTarget>()
        => FusionEngine.Project<TSource, TTarget>(Value);

    public IQueryable<TTarget> To<TTarget>(IQueryable<TTarget> query)
        => FusionEngine.Rewrite<TSource, TTarget>(query);
}
```

The corresponding internal engine method should be:

```csharp
internal static IQueryable<TTarget> Rewrite<TSource, TTarget>(IQueryable<TTarget> query)
```

Behavior:

1. Take `query.Expression`.
2. Visit the expression tree.
3. Replace supported FusionMapper calls with translatable LINQ calls.
4. Create a new query using:

```csharp
query.Provider.CreateQuery<TTarget>(newExpression)
```

5. Do not mutate the original expression.
6. Return the original query if no rewrite is needed.

Supported rewrite targets:

```csharp
x.Map().To<T>()
```

and, where present in expression trees:

```csharp
someQueryable.Project().To<T>()
```

They must be rewritten into `.Select(...)` calls with inlined projection bodies.

Example:

Before:

```csharp
source.Select(x => x.Map().To<ProductDto>())
```

After:

```csharp
source.Select(x => new ProductDto
{
    Name = x.Name,
    Price = x.Price
})
```

Important:

- The parameterless `Project().To<TTarget>()` projects the query stored inside `FusionProjection<TSource>.Value`.
- It is not sufficient for rewriting an arbitrary previously built query.
- Tests that build a query containing `Map().To<T>()` must pass that query into the rewriting overload.

Correct rewrite test pattern:

```csharp
var query = source.Select(x => x.Map().To<SimpleTarget>());

var rewritten = source
    .Project()
    .To<SimpleTarget>(query);
```

Incorrect pattern:

```csharp
var query = source.Select(x => x.Map().To<SimpleTarget>());

var rewritten = source
    .Project()
    .To<SimpleTarget>();
```

The incorrect pattern does not rewrite `query`.

---

## 6. Exception Rules

Use only:

```csharp
FusionMapper.MappingException
```

for mapping failures.

Do not introduce another default exception type for mapping errors.

Throw `MappingException` when:

- A required target member cannot be mapped.
- A constructor parameter cannot be mapped.
- Member resolution is ambiguous.
- A recursive projection cannot be safely built.
- A target type has no usable constructor.
- A collection mapping is unsupported.
- An expression tree contains an unsupported FusionMapper call.
- Mapping into an immutable target is impossible.

Exception messages should include:

- Source type.
- Target type.
- Target member or constructor parameter.
- Candidate source paths, when useful.

Example message style:

```text
Cannot map required member 'ProductDto.Name'.
Source type: 'Product'.
No matching source member was found.
```

For null argument errors in public APIs, standard .NET exceptions such as `ArgumentNullException` are acceptable, but mapping-resolution failures must use `MappingException`.

---

## 7. Mapping Rules

### 7.1. General Rules

Mapping is automatic.

The mapper must not require:

- Mapping profiles.
- Manual member configuration.
- Explicit type maps.
- Attributes in version 1.

If mapping is impossible, throw `MappingException`.

Do not silently skip required members.

---

### 7.2. Source Members

Source members are public instance:

- Properties.
- Fields.

Property access is preferred over field access when both are otherwise equal.

Supported source members include:

- Regular properties.
- Read-only properties.
- Nested object properties.
- Collection properties.
- Constructor parameters, when mapping from records or immutable types if exposed as properties.

Methods are not general source members, except for collection aggregate translation rules described later.

---

### 7.3. Target Members

Target members include:

- Constructor parameters.
- Settable properties.
- Init-only properties.
- Read-only collection properties that can be mutated.
- Fields, where appropriate.

The mapper must support:

- Classes.
- Records.
- Structs, where reasonable.
- Immutable types with constructors.
- Types with `required` members.
- Types with `init`-only members.

---

## 8. Member Matching Algorithm

For each target member, resolve a source expression in the following order.

### 8.1. Exact Match

Exact ordinal name match:

```csharp
source.Name -> target.Name
```

### 8.2. Case-Insensitive Match

If no exact match exists, use case-insensitive ordinal comparison:

```csharp
source.name -> target.Name
source.NAME -> target.Name
```

### 8.3. Flattening

If no direct match exists, attempt flattening.

Target member names are split by:

- PascalCase boundaries.
- Underscores.

Example target member:

```csharp
CategoryName
```

Split into:

```text
Category, Name
```

Candidate source path:

```csharp
source.Category.Name
```

Another example:

```csharp
OrderTotalAmount
```

Candidate source path:

```csharp
source.Order.Total.Amount
```

Flattening rules:

- Prefer exact segment matches.
- Fall back to case-insensitive segment matches.
- Limit flattening depth to a reasonable value, recommended max depth: 4.
- Prefer shorter paths when scores are otherwise equal.
- Throw `MappingException` on ambiguous equal-score candidates.

---

## 9. Matching Priority

Recommended scoring model:

| Match Type | Priority |
|---|---:|
| Exact direct member | 1000 |
| Exact constructor parameter match | 950 |
| Case-insensitive direct member | 900 |
| Flattening with exact segments | 800 |
| Flattening with case-insensitive segments | 700 |
| Aggregate convention | 500 |
| Fallback type conversion | 100 |

If multiple candidates have the same score, throw `MappingException`.

Do not randomly select a candidate.

---

## 10. Constructor Mapping

### 10.1. Constructor Selection

The mapper must choose a constructor automatically.

Rules:

1. If the target has a public parameterless constructor and all required members can be initialized through member bindings, it may be used.
2. For records, prefer the primary constructor.
3. Otherwise, choose the public constructor with the highest number of bindable parameters.
4. All required constructor parameters must be bindable.
5. If multiple constructors have identical bindability scores, throw `MappingException`.

### 10.2. Constructor Parameter Matching

Constructor parameter names are matched against source members.

Matching order:

1. Exact ordinal match, ignoring parameter case conventions.
2. Case-insensitive match.
3. Flattening match.

Example:

```csharp
public record ProductDto(string Name, decimal Price);
```

Source:

```csharp
public class Product
{
    public string Name { get; set; }
    public decimal Price { get; set; }
}
```

Mapping:

```csharp
new ProductDto(
    Name: source.Name,
    Price: source.Price
)
```

If a constructor parameter is required and cannot be mapped, throw `MappingException`.

---

## 11. Required Members

The mapper must respect C# `required` members.

Runtime detection may use:

```csharp
RequiredMemberAttribute
```

Source generation should use Roslyn symbol information, such as property required metadata.

Rules:

- Every required target member must be mapped.
- A required member may be satisfied by:
  - A constructor parameter.
  - A member initialization.
- If a required member cannot be satisfied, throw `MappingException`.

Example failure:

```csharp
public class RequiredTarget
{
    public required string Name { get; set; }
}
```

If source has no member that can map to `Name`, throw.

---

## 12. Init-Only Members

The mapper must support init-only members during object creation.

Example:

```csharp
public class InitTarget
{
    public required string Name { get; init; }
    public int Value { get; init; }
}
```

Mapping must initialize these members during construction:

```csharp
new InitTarget
{
    Name = source.Name,
    Value = source.Value
}
```

Init-only members must not be set when mapping into an existing target object.

---

## 13. Mapping Into Existing Target

This API:

```csharp
source.Map().To(target);
```

must update an existing target instance.

Rules:

- The target constructor is not invoked.
- Writable properties and fields may be updated.
- Init-only members must not be updated.
- Read-only non-collection members must not be updated.
- Required members are not revalidated because the target already exists.
- If `target` is null, throw `ArgumentNullException`.

Collection behavior:

- If the target collection property is writable, assign a new mapped collection when appropriate.
- If the target collection property is read-only but the collection instance is mutable, clear and repopulate it.
- If the collection cannot be assigned or mutated, throw `MappingException`.

Example:

```csharp
public class OrderTarget
{
    public List<ItemTarget> Items { get; } = new();
}
```

The mapper should clear and repopulate `Items`.

---

## 14. Null Handling

### 14.1. Null Source

If the source object is null:

- If the target type is a reference type or nullable value type, return `default`.
- If the target type is a non-nullable value type, throw `MappingException`.

Example:

```csharp
Product? source = null;
var result = source.Map().To<ProductDto>();
```

Expected:

```csharp
result == null
```

### 14.2. Null Nested Members

For projections, nested member access must be null-safe.

Instead of:

```csharp
source.Category.Name
```

generate:

```csharp
source.Category != null ? source.Category.Name : null
```

For runtime object mapping, normal null-safe traversal is expected.

---

## 15. Recursive Object Mapping

The mapper must map nested objects recursively.

Example:

```csharp
public class NodeSource
{
    public string Name { get; set; }
    public NodeSource? Child { get; set; }
}

public class NodeTarget
{
    public string Name { get; set; }
    public NodeTarget? Child { get; set; }
}
```

Mapping must recursively map `Child`.

---

## 16. Cycles

The mapper must not cause stack overflow on cyclic object graphs.

Example:

```csharp
parent.Children.Add(child);
child.Parent = parent;
```

### 16.1. Runtime Object Mapping

For runtime object mapping:

- Use a per-operation mapping context.
- Track already mapped source objects by reference.
- Reuse already created target instances where possible.
- If a cycle cannot be safely represented, throw `MappingException`.

Preferred behavior:

- Preserve object identity where possible.
- Avoid infinite recursion.
- Support cyclic graphs for mutable target types with accessible construction or initialization paths.

If cycle preservation is impossible for an immutable target, throw a clear `MappingException`.

### 16.2. IQueryable Projections

For projections:

- Cycles are dangerous because LINQ providers cannot use runtime mapping context.
- Detect type-pair recursion while building projection expressions.
- Default behavior: throw `MappingException`.

Example:

```text
Recursive projection detected:
Category -> CategoryDto -> Parent -> Category -> CategoryDto
```

A future version may allow policies such as:

- Throw.
- Nullify back references.
- Limit depth.

Version 1 default: throw.

---

## 17. Collection Mapping

The mapper must support mapping between collection types.

Supported source collection types include:

- Arrays.
- `List<T>`.
- `IEnumerable<T>`.
- `ICollection<T>`.
- `IList<T>`.
- `IReadOnlyList<T>`.
- `IReadOnlyCollection<T>`.

Supported target collection types include:

- Arrays.
- `List<T>`.
- `IEnumerable<T>`.
- `ICollection<T>`.
- `IList<T>`.
- `IReadOnlyList<T>`.
- `IReadOnlyCollection<T>`.

Materialization rules:

- For arrays, use `ToArray()`.
- For list-like and read-only collection interfaces, use `ToList()` unless a better target-specific materialization exists.
- For `IEnumerable<T>`, the mapper may leave the projection as `Select(...)` or materialize if required by the target.

Element mapping must be recursive.

Example:

```csharp
List<ItemSource> -> List<ItemTarget>
```

must map each `ItemSource` to `ItemTarget`.

Null collections:

- If source collection is null and target collection is nullable, map to null.
- If target collection is non-nullable, behavior may be target creation or exception depending on construction requirements. Prefer predictable behavior and throw if impossible.

---

## 18. Aggregate Collection Mapping

The mapper supports convention-based aggregate mapping.

Supported aggregate suffixes:

- `Count`
- `Sum`
- `Average`
- `Max`
- `Min`
- `Any`

`All` is not supported automatically in version 1 because it requires a predicate.

### 18.1. Count

Target:

```csharp
public int ItemsCount { get; set; }
```

Source:

```csharp
public List<Item> Items { get; set; }
```

Mapping:

```csharp
ItemsCount = source.Items.Count()
```

### 18.2. Any

Target:

```csharp
public bool ItemsAny { get; set; }
```

Mapping:

```csharp
ItemsAny = source.Items.Any()
```

### 18.3. Sum / Average / Max / Min

For numeric collections:

```csharp
public List<decimal> Prices { get; set; }
```

Target:

```csharp
public decimal PricesSum { get; set; }
public decimal PricesAverage { get; set; }
public decimal PricesMax { get; set; }
public decimal PricesMin { get; set; }
```

Mapping:

```csharp
PricesSum = source.Prices.Sum()
PricesAverage = source.Prices.Average()
PricesMax = source.Prices.Max()
PricesMin = source.Prices.Min()
```

For complex element types, support selector inference where possible.

Example target:

```csharp
public decimal ItemsValueSum { get; set; }
```

Source:

```csharp
public List<ItemSource> Items { get; set; }

public class ItemSource
{
    public decimal Value { get; set; }
}
```

Mapping:

```csharp
ItemsValueSum = source.Items.Sum(x => x.Value)
```

Aggregate parsing rules:

- The last segment is the operation.
- The preceding segment or segments identify the source collection.
- Remaining middle segments identify the selector path.
- Prefer exact matches.
- Fall back to case-insensitive matches.
- Throw `MappingException` when the aggregate target cannot be resolved unambiguously.

---

## 19. Type Conversion Rules

Version 1 should support at least:

- Same type assignment.
- Assignable reference conversions.
- Nullable wrapping:

```csharp
int -> int?
```

- Nullable unwrapping where safe:

```csharp
int? -> int
```

For nullable unwrapping, use predictable behavior:

- If runtime value is null, throw `MappingException`, unless a default convention is explicitly implemented.
- For projections, generate provider-compatible null handling.

Additional conversions, such as numeric widening or enum-string conversion, may be added later but are not mandatory for the first implementation.

Do not introduce lossy conversions silently.

---

## 20. IQueryable Projection Rules

When building projections:

1. Build an `Expression<Func<TSource, TTarget>>`.
2. Cache the projection expression by source/target type pair.
3. Inline nested object projections.
4. Avoid `Expression.Invoke`.
5. Avoid compiled delegates inside expression trees.
6. Avoid custom methods unless they are known to be translatable by LINQ providers.
7. Use null-safe conditional expressions for nested references.
8. Use standard LINQ aggregate methods:
   - `Count()`
   - `Sum(...)`
   - `Average(...)`
   - `Max(...)`
   - `Min(...)`
   - `Any()`

Example:

```csharp
source => new ProductDto
{
    Name = source.Name,
    Price = source.Price,
    CategoryName = source.Category != null ? source.Category.Name : null,
    ItemsCount = source.Items.Count(),
    ItemsPriceSum = source.Items.Sum(x => x.Price)
}
```

This expression must be suitable for providers such as EF Core.

---

## 21. Expression Rewriting Rules

The expression rewriter must visit query expression trees and replace FusionMapper fluent calls with translatable LINQ equivalents.

### 21.1. Rewrite Map().To<T>()

Before:

```csharp
source.Select(x => x.Map().To<ProductDto>())
```

After:

```csharp
source.Select(x => new ProductDto
{
    Name = x.Name,
    Price = x.Price
})
```

The replacement must inline the projection body.

Do not generate:

```csharp
source.Select(x => MapHelper.Map(x))
```

That form is not generally provider-translatable.

---

### 21.2. Rewrite Project().To<T>()

If an expression tree contains:

```csharp
someQueryable.Project().To<ProductDto>()
```

rewrite it to:

```csharp
someQueryable.Select(x => new ProductDto
{
    ...
})
```

The exact expression should use `System.Linq.Queryable.Select`.

---

### 21.3. Unsupported Calls

The following call is not supported inside query projection expression trees:

```csharp
x.Map().To(existingTarget)
```

Reason:

- Mapping into an existing object is a runtime side-effect operation.
- It cannot generally be translated by LINQ providers.

If encountered inside an expression tree, throw `MappingException`.

---

### 21.4. Rewriter Requirements

The rewriter must:

- Visit all lambda expressions.
- Visit member initialization expressions.
- Visit nested method calls.
- Preserve `Where`, `OrderBy`, `Skip`, `Take`, and other query operators.
- Not mutate the original expression tree.
- Return the original query if no rewrite is needed.
- Use the original query provider to create the rewritten query.

Pseudo-behavior:

```csharp
public static IQueryable<TTarget> Rewrite<TSource, TTarget>(IQueryable<TTarget> query)
{
    var rewriter = new FusionExpressionRewriter();
    var newExpression = rewriter.Visit(query.Expression);

    if (newExpression == query.Expression)
        return query;

    return query.Provider.CreateQuery<TTarget>(newExpression);
}
```

---

## 22. Runtime Engine Responsibilities

The internal `FusionEngine` is responsible for:

1. Building object mapping delegates.
2. Building projection expressions.
3. Caching mapping artifacts.
4. Validating mapping plans.
5. Rewriting query expressions.
6. Throwing `MappingException` with useful diagnostics.

Recommended internal structure:

```text
FusionEngine
  MappingPlanBuilder
  MemberResolver
  ConstructorResolver
  ObjectMappingCompiler
  ProjectionBuilder
  ExpressionRewriter
  MappingCache
  MappingContext
```

---

## 23. Caching

The mapper must cache:

- Compiled object mapping delegates.
- Projection expressions.
- Mapping plans.

Cache keys should include:

- Source type.
- Target type.
- Mapping mode, if necessary:
  - Object mapping.
  - Projection mapping.

Use thread-safe caching, for example:

```csharp
ConcurrentDictionary<TypePair, Delegate>
ConcurrentDictionary<TypePair, LambdaExpression>
```

Do not rebuild the same mapping plan on every call.

---

## 24. Thread Safety

- Mapping caches must be thread-safe.
- Mapping context must be per-operation.
- Shared cached delegates and expressions must be stateless or safe for concurrent use.

---

## 25. Source Generator Direction

Source generation is a later phase but must remain compatible with the runtime design.

The source generator should:

1. Detect calls to:

```csharp
Map().To<T>()
Map().To(target)
Project().To<T>()
```

2. Resolve `TSource` and `TTarget` at compile time.
3. Generate mapping implementations.
4. Emit compile-time diagnostics for mapping failures.
5. Optionally use interceptors to replace calls with generated implementations.
6. Keep runtime fallback for cases that cannot be resolved at compile time.

Important source generation rule:

For `IQueryable` projections, generated code must produce expression trees that are translatable by LINQ providers.

Good generated projection:

```csharp
public static Expression<Func<Product, ProductDto>> Projection { get; } =
    source => new ProductDto
    {
        Name = source.Name,
        Price = source.Price
    };
```

Bad generated projection:

```csharp
public static Expression<Func<Product, ProductDto>> Projection { get; } =
    source => Map_Product_ProductDto(source);
```

The bad version is not generally translatable.

---

## 26. Source Generator Diagnostics

The source generator should report diagnostics for:

- Missing required members.
- Ambiguous member matches.
- Ambiguous constructors.
- Unsupported recursive projections.
- Open generic mappings that cannot be generated.
- Inaccessible source or target types.
- Unsupported expression rewrite scenarios.

Diagnostic message style should be clear and actionable.

Example:

```text
FUS001: Required member 'ProductDto.Name' cannot be mapped from 'Product'.
```

---

## 27. Testing Requirements

Tests use TUnit.

Preferred TUnit style:

```csharp
[Test]
public async Task Some_Test()
{
    var result = ...;

    await Assert.That(result).IsEqualTo(expected);
}
```

Use async test methods and await assertions.

Expected exception type for mapping failures:

```csharp
MappingException
```

Tests should cover:

- Simple property mapping.
- Case-insensitive mapping.
- Exact match priority.
- Ambiguous mapping failure.
- Flattening.
- Null nested member handling.
- Constructor mapping.
- Record mapping.
- Required member validation.
- Init-only member mapping.
- Recursive mapping.
- Cyclic object mapping.
- Collection mapping.
- Mapping into existing object.
- Mapping into existing mutable collection.
- Collection aggregate mapping.
- IQueryable projection.
- Expression rewriting.
- Preservation of `Where`, `OrderBy`, and other query operators.
- Non-mutation of original expression trees.

---

## 28. Expression Rewrite Test Pattern

Correct test pattern:

```csharp
var source = new[]
{
    new SimpleSource { Name = "A", Value = 1 },
    new SimpleSource { Name = "B", Value = 2 }
}.AsQueryable();

var query = source
    .Select(x => x.Map().To<SimpleTarget>());

var rewritten = source
    .Project()
    .To<SimpleTarget>(query);

var result = rewritten.ToList();
```

The test must assert:

1. Original `query.Expression` contains `Map`.
2. Rewritten query returns correct data.
3. Rewritten expression does not contain `Map`.
4. Original expression is not mutated.

---

## 29. Implementation Milestones

### Milestone 1: Runtime Object Mapping

Implement:

```csharp
FusionEngine.Map<TSource, TTarget>(TSource source)
```

Support:

- Simple properties.
- Case-insensitive matching.
- Constructor mapping.
- Records.
- Required members.
- Init-only members.
- Nested objects.
- Collections.
- Null handling.
- Basic cycle protection.
- `MappingException` diagnostics.

### Milestone 2: Mapping Into Existing Object

Implement:

```csharp
FusionEngine.Map<TSource, TTarget>(TSource source, TTarget target)
```

Support:

- Writable members.
- Mutable collections.
- Read-only collection mutation.
- No required revalidation.
- No constructor invocation.

### Milestone 3: Projection Building

Implement:

```csharp
FusionEngine.Project<TSource, TTarget>(IQueryable<TSource> source)
```

Support:

- Expression projections.
- Constructor mapping inside expressions.
- Nested projections.
- Collection projections.
- Aggregate conventions.
- Null-safe conditional expressions.
- Projection caching.

### Milestone 4: Expression Rewriting

Implement:

```csharp
FusionEngine.Project<TSource, TTarget>(IQueryable<TSource> source)
```

Support:

- Rewriting `Map().To<T>()`.
- Rewriting `Project().To<T>()`.
- Preserving other query operators.
- Returning new query via provider.
- Not mutating original expression.

### Milestone 5: Source Generator

Implement compile-time generation for known calls.

Support:

- Generated object mappers.
- Generated projection expressions.
- Diagnostics.
- Optional interceptors.
- Runtime fallback compatibility.

---

## 30. Rules for LLM Code Generation

When writing code for FusionMapper, an LLM must:

1. Preserve the public fluent API.
2. Keep `FusionEngine` internal.
3. Use `MappingException` for mapping errors.
4. Not introduce external mapping libraries.
5. Not require manual mapping profiles.
6. Prefer expression trees for projections.
7. Avoid custom method calls inside generated projection expressions.
8. Ensure expression rewriting does not mutate original expressions.
9. Write TUnit-compatible tests when tests are requested.
10. Update expression rewrite tests to pass the actual query into the rewriting API.
11. Throw clear `MappingException` errors instead of silently ignoring impossible mappings.
12. Keep implementation incremental and compatible with the milestone plan.
13. Avoid `NotImplementedException` in completed features.
14. Do not change the intended architecture unless explicitly requested.

---

## 31. Definition of Done

A feature is done when:

- It works for the specified scenario.
- It throws `MappingException` for invalid scenarios.
- It does not break existing public API.
- It has tests where applicable.
- Projection output is expression-tree based.
- Expression rewriting produces provider-friendly `.Select(...)` calls.
- No manual mapping configuration is required.
- No external mapping dependency is introduced.
- Code follows the namespace and architecture rules in this specification.
