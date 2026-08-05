FusionMapper Specification

1. Project Goal

FusionMapper is a .NET mapping library that performs fully automatic object-to-object mapping and LINQ projection generation without manual mapping profiles, manual maps, or explicit configuration.

The library must:

- Map objects automatically by convention.
- Build `IQueryable<T>` projections automatically.
- Support constructors, records, `required` members, and `init`-only members.
- Map nested objects in acyclic object graphs by composing compiled expression trees.
- Support flattening, for example:
  - `source.Category.Name -> target.CategoryName`
- Support automatic aggregate mapping for collections, for example:
  - `source.Items.Count() -> target.ItemsCount`
  - `source.Items.Sum(x => x.Price) -> target.ItemsPriceSum`
- Rewrite calls to `Map().To<T>()` and `Project().To<T>()` inside expression trees into provider-translatable `.Select(...)` calls.
- Use compiled expression-tree mapping for runtime object mapping.
- Later, use a Roslyn source generator and interceptors to generate compile-time implementations.

The runtime mapping execution model must satisfy the following constraints:

- Mapping must be based on compiled expression trees.
- The compiled mapping delegate must not use runtime reflection during mapping execution.
- Reflection may be used only while discovering members and building the mapping expression, not while executing the compiled mapping delegate.
- Recursive type mapping is not supported.
- Cyclic object graphs are not supported.
- If a recursive or cyclic type/object graph is detected, the mapper must throw `MappingException`.
- Identity resolution is not supported. The mapper does not guarantee that the same source object instance maps to the same target object instance.
- There is no `MappingContext`, visited-object tracker, reference tracker, or per-operation identity cache.

The project must not require the user to define mapping profiles or manual mapping rules in version 1.


2. Technology Stack

Language:

- C#, latest stable or preview features allowed.

Target:

- Modern .NET.

Nullable reference types:

- Enabled.

Test framework:

- TUnit.

Dependencies:

- No external mapping libraries are allowed.
- No AutoMapper, Mapster, or similar dependencies.

Runtime mapping must use:

- `System.Linq.Expressions`.
- Compiled expression trees.

Reflection policy:

- Reflection may be used during mapping plan construction and expression compilation.
- Reflection must not be used inside the compiled mapping delegate to read source members, write target members, invoke constructors, or perform dynamic invocation.
- The compiled mapping delegate should consist of direct field/property accesses, constructor calls, member assignments, conditional expressions, collection projections, and standard LINQ methods where appropriate.
- `PropertyInfo.SetValue`, `FieldInfo.SetValue`, `ConstructorInfo.Invoke`, `Delegate.DynamicInvoke`, and similar reflection-based execution paths are not allowed during mapping execution.

Source generation may use:

- Roslyn incremental source generators.
- Interceptors, where supported.
- Compile-time diagnostics.


3. Namespaces

Primary library namespace:

```csharp
namespace FusionMapper;
```

Test namespace:

```csharp
namespace FusionMapper.Tests;
```


4. Current Public Skeleton

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

The internal engine may be implemented using compiled expression trees and cached mapping delegates. It must not rely on runtime reflection during mapping execution.


5. Authoritative API Behavior

5.1. Object Mapping

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

Object mapping must be executed through compiled expression-tree delegates.

5.2. Query Projection

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

5.3. Expression Tree Rewriting

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

- Take `query.Expression`.
- Visit the expression tree.
- Replace supported FusionMapper calls with translatable LINQ calls.
- Create a new query using:

```csharp
query.Provider.CreateQuery<TTarget>(newExpression)
```

- Do not mutate the original expression.
- Return the original query if no rewrite is needed.

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

The parameterless `Project().To<TTarget>()` projects the query stored inside `FusionProjection<TSource>.Value`.

It is not sufficient for rewriting an arbitrary previously built query.

Tests that build a query containing `Map().To<T>()` must pass that query into the rewriting overload.

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


6. Exception Rules

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
- A recursive type graph is detected.
- A cyclic object graph is detected or cannot be safely represented.
- A recursive projection cannot be safely built.
- A target type has no usable constructor.
- A collection mapping is unsupported.
- An expression tree contains an unsupported FusionMapper call.
- Mapping into an immutable target is impossible.
- A mapping would require identity resolution or cycle tracking to complete safely.

Exception messages should include:

- Source type.
- Target type.
- Target member or constructor parameter.
- Candidate source paths, when useful.
- Recursive path information, when a recursive or cyclic mapping is rejected.

Example message style:

```text
Cannot map required member 'ProductDto.Name'.
Source type: 'Product'.
No matching source member was found.
```

Example recursive mapping failure message style:

```text
Recursive mapping detected between 'NodeSource' and 'NodeTarget'.
Path: NodeSource -> Child -> NodeSource.
Recursive and cyclic type graphs are not supported.
```

For null argument errors in public APIs, standard .NET exceptions such as `ArgumentNullException` are acceptable, but mapping-resolution failures must use `MappingException`.


7. Mapping Rules

7.1. General Rules

Mapping is automatic.

The mapper must not require:

- Mapping profiles.
- Manual member configuration.
- Explicit type maps.
- Attributes in version 1.

If mapping is impossible, throw `MappingException`.

Do not silently skip required members.

7.2. Source Members

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

7.3. Target Members

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


8. Member Matching Algorithm

For each target member, resolve a source expression in the following order.

8.1. Exact Match

Exact ordinal name match:

```text
source.Name -> target.Name
```

8.2. Case-Insensitive Match

If no exact match exists, use case-insensitive ordinal comparison:

```text
source.name -> target.Name
source.NAME -> target.Name
```

8.3. Flattening

If no direct match exists, attempt flattening.

Target member names are split by:

- PascalCase boundaries.
- Underscores.

Example target member:

```text
CategoryName
```

Split into:

```text
Category, Name
```

Candidate source path:

```text
source.Category.Name
```

Another example:

```text
OrderTotalAmount
```

Candidate source path:

```text
source.Order.Total.Amount
```

Flattening rules:

- Prefer exact segment matches.
- Fall back to case-insensitive segment matches.
- Limit flattening depth to a reasonable value, recommended max depth: 4.
- Prefer shorter paths when scores are otherwise equal.
- Throw `MappingException` on ambiguous equal-score candidates.


9. Matching Priority

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


10. Constructor Mapping

10.1. Constructor Selection

The mapper must choose a constructor automatically.

Rules:

- If the target has a public parameterless constructor and all required members can be initialized through member bindings, it may be used.
- For records, prefer the primary constructor.
- Otherwise, choose the public constructor with the highest number of bindable parameters.
- All required constructor parameters must be bindable.
- If multiple constructors have identical bindability scores, throw `MappingException`.

10.2. Constructor Parameter Matching

Constructor parameter names are matched against source members.

Matching order:

- Exact ordinal match, ignoring parameter case conventions.
- Case-insensitive match.
- Flattening match.

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


11. Required Members

The mapper must respect C# `required` members.

Runtime detection may use:

- `RequiredMemberAttribute`.

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


12. Init-Only Members

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


13. Mapping Into Existing Target

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
- No `MappingContext`, identity tracker, or visited-object tracker is used.

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


14. Null Handling

14.1. Null Source

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

14.2. Null Nested Members

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


15. Nested Object Mapping and Recursive Types

The mapper must map nested objects by composing expression trees, provided the type graph is acyclic.

Mapping to recursive types is not supported.

If building a mapping requires mapping the same source/target type pair again within the same mapping construction path, the mapper must throw `MappingException`.

Example unsupported recursive mapping:

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

Mapping `NodeSource` to `NodeTarget` requires mapping `NodeSource.Child` to `NodeTarget.Child`, which recursively requires the same mapping again.

This must throw:

```csharp
MappingException
```

Finite nested object graphs with non-repeating type pairs are supported.

Example supported nested mapping:

```csharp
public class OrderSource
{
    public string Id { get; set; }
    public CustomerSource Customer { get; set; }
}

public class OrderTarget
{
    public string Id { get; set; }
    public CustomerTarget Customer { get; set; }
}
```

This is supported as long as `CustomerSource -> CustomerTarget` does not create a recursive type cycle.

Reusing a completed mapping for the same type pair in independent branches is allowed.

Example:

```csharp
public class OrderSource
{
    public AddressSource BillingAddress { get; set; }
    public AddressSource ShippingAddress { get; set; }
}

public class OrderTarget
{
    public AddressTarget BillingAddress { get; set; }
    public AddressTarget ShippingAddress { get; set; }
}
```

This is not recursive. The same `AddressSource -> AddressTarget` mapping may be reused.

A repeated type pair is an error only when it appears in its own mapping construction path, forming a recursive cycle.


16. Cycles, Identity Resolution, and Mapping Context

16.1. Cyclic and Recursive Type Graphs

The mapper must not cause stack overflow on cyclic object graphs.

Because version 1 does not support recursive type mapping or cycle preservation, the mapper must detect recursive type graphs while building mapping expressions and throw `MappingException`.

Example recursive cycle:

```text
Category -> CategoryDto -> Parent -> Category -> CategoryDto
```

This must throw `MappingException`.

Example message:

```text
Recursive mapping detected between 'Category' and 'CategoryDto'.
Path: Category -> Parent -> Category.
Recursive and cyclic type graphs are not supported.
```

16.2. Runtime Cyclic Object Graphs

Cyclic object graphs are not supported.

The mapper does not attempt to preserve cycles.

If a cyclic object graph cannot be represented without cycle tracking or identity resolution, the mapper must fail with `MappingException` rather than entering infinite recursion.

Because recursive type graphs are rejected during mapping construction, many cyclic scenarios are rejected before runtime mapping execution.

16.3. No Identity Resolution

Identity resolution is not supported.

The mapper does not track already mapped source objects.

The mapper does not guarantee that:

- The same source instance maps to the same target instance.
- Multiple references to the same source object preserve reference equality after mapping.
- Object identity is preserved across the mapped graph.

If the same source object is referenced multiple times in an acyclic graph, it may be mapped independently each time.

Example:

```csharp
public class OrderSource
{
    public CustomerSource BillingCustomer { get; set; }
    public CustomerSource ShippingCustomer { get; set; }
}
```

If `BillingCustomer` and `ShippingCustomer` reference the same source instance, the mapper is not required to produce the same `CustomerTarget` instance for both target members.

16.4. No MappingContext

There must be no `MappingContext`.

The mapper must not use:

- A per-operation visited-object dictionary.
- A source-to-target identity map.
- A cycle-tracking context.
- A recursive mapping stack stored in a runtime mapping context.

Cached compiled delegates and projection expressions must be stateless or safe for concurrent use without per-operation state.


17. Collection Mapping

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

Element mapping must follow the same acyclic mapping rules as object mapping.

Example:

```text
List<ItemSource> -> List<ItemTarget>
```

must map each `ItemSource` to `ItemTarget`.

Recursive collection element graphs are not supported.

Example unsupported recursive collection mapping:

```csharp
public class TreeNodeSource
{
    public List<TreeNodeSource> Children { get; set; }
}

public class TreeNodeTarget
{
    public List<TreeNodeTarget> Children { get; set; }
}
```

This must throw `MappingException`.

Null collections:

- If source collection is null and target collection is nullable, map to null.
- If target collection is non-nullable, behavior may be target creation or exception depending on construction requirements.
- Prefer predictable behavior and throw if impossible.


18. Aggregate Collection Mapping

The mapper supports convention-based aggregate mapping.

Supported aggregate suffixes:

- `Count`
- `Sum`
- `Average`
- `Max`
- `Min`
- `Any`

`All` is not supported automatically in version 1 because it requires a predicate.

18.1. Count

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

18.2. Any

Target:

```csharp
public bool ItemsAny { get; set; }
```

Mapping:

```csharp
ItemsAny = source.Items.Any()
```

18.3. Sum / Average / Max / Min

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


19. Type Conversion Rules

Version 1 should support at least:

- Same type assignment.
- Assignable reference conversions.
- Nullable wrapping:

```text
int -> int?
```

- Nullable unwrapping where safe:

```text
int? -> int
```

For nullable unwrapping, use predictable behavior:

- If runtime value is null, throw `MappingException`, unless a default convention is explicitly implemented.
- For projections, generate provider-compatible null handling.

Additional conversions, such as numeric widening or enum-string conversion, may be added later but are not mandatory for the first implementation.

Do not introduce lossy conversions silently.


20. IQueryable Projection Rules

When building projections:

- Build an `Expression<Func<TSource, TTarget>>`.
- Cache the projection expression by source/target type pair.
- Inline nested object projections.
- Avoid `Expression.Invoke`.
- Avoid compiled delegates inside expression trees.
- Avoid custom methods unless they are known to be translatable by LINQ providers.
- Use null-safe conditional expressions for nested references.
- Detect recursive type-pair projection cycles and throw `MappingException`.
- Do not rely on identity resolution or a `MappingContext`.

Use standard LINQ aggregate methods:

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


21. Expression Rewriting Rules

The expression rewriter must visit query expression trees and replace FusionMapper fluent calls with translatable LINQ equivalents.

21.1. Rewrite Map().To<T>()

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

21.2. Rewrite Project().To<T>()

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

21.3. Unsupported Calls

The following call is not supported inside query projection expression trees:

```csharp
x.Map().To(existingTarget)
```

Reason:

Mapping into an existing object is a runtime side-effect operation.

It cannot generally be translated by LINQ providers.

If encountered inside an expression tree, throw `MappingException`.

21.4. Rewriter Requirements

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


22. Runtime Engine Responsibilities

The internal engine is responsible for:

- Building object mapping expressions.
- Compiling object mapping delegates.
- Building projection expressions.
- Caching mapping artifacts.
- Validating mapping plans.
- Detecting recursive and cyclic type graphs.
- Rewriting query expressions.
- Throwing `MappingException` with useful diagnostics.

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
  RecursiveMappingDetector
```

There is no `MappingContext` in the architecture.


23. Caching

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

Cached artifacts must not capture per-operation state, identity maps, or visited-object trackers.


24. Thread Safety

Mapping caches must be thread-safe.

Shared cached delegates and expressions must be stateless or safe for concurrent use.

There is no per-operation `MappingContext`.

The mapper must not require per-call mutable state for identity resolution or cycle tracking.


25. Source Generator Direction

Source generation is a later phase but must remain compatible with the runtime design.

The source generator should:

- Detect calls to:
  - `Map().To<T>()`
  - `Map().To(target)`
  - `Project().To<T>()`
- Resolve `TSource` and `TTarget` at compile time.
- Generate mapping implementations.
- Emit compile-time diagnostics for mapping failures.
- Optionally use interceptors to replace calls with generated implementations.
- Keep runtime fallback for cases that cannot be resolved at compile time.

Generated runtime mapping should follow the same rules:

- No reflection-based member access during mapping execution.
- Direct member access and assignment.
- No identity resolution.
- No `MappingContext`.
- Recursive and cyclic type graphs should produce compile-time diagnostics where detectable.

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


26. Source Generator Diagnostics

The source generator should report diagnostics for:

- Missing required members.
- Ambiguous member matches.
- Ambiguous constructors.
- Recursive or cyclic type graphs.
- Unsupported recursive projections.
- Open generic mappings that cannot be generated.
- Inaccessible source or target types.
- Unsupported expression rewrite scenarios.

Diagnostic message style should be clear and actionable.

Example:

```text
FUS001: Required member 'ProductDto.Name' cannot be mapped from 'Product'.
```

Example recursive diagnostic:

```text
FUS002: Recursive mapping detected between 'NodeSource' and 'NodeTarget'. Recursive and cyclic type graphs are not supported.
```


27. Testing Requirements

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
- Nested object mapping for acyclic graphs.
- Recursive type mapping throws `MappingException`.
- Cyclic type graph detection throws `MappingException`.
- Collection mapping.
- Recursive collection element mapping throws `MappingException`.
- Mapping into existing object.
- Mapping into existing mutable collection.
- Collection aggregate mapping.
- IQueryable projection.
- Expression rewriting.
- Preservation of `Where`, `OrderBy`, and other query operators.
- Non-mutation of original expression trees.
- Compiled mapping execution does not rely on reflection-based member setting or dynamic invocation.
- No identity resolution behavior is expected or required.


28. Expression Rewrite Test Pattern

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

- Original `query.Expression` contains `Map`.
- Rewritten query returns correct data.
- Rewritten expression does not contain `Map`.
- Original expression is not mutated.


29. Implementation Milestones

Milestone 1: Runtime Object Mapping

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
- Nested objects in acyclic graphs.
- Collections.
- Null handling.
- Detection and rejection of recursive/cyclic type graphs.
- `MappingException` diagnostics.
- Compiled expression-tree mapping execution without runtime reflection.

Milestone 2: Mapping Into Existing Object

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
- No `MappingContext`.
- No identity tracking.

Milestone 3: Projection Building

Implement:

```csharp
FusionEngine.Project<TSource, TTarget>(IQueryable<TSource> source)
```

Support:

- Expression projections.
- Constructor mapping inside expressions.
- Nested projections for acyclic graphs.
- Collection projections.
- Aggregate conventions.
- Null-safe conditional expressions.
- Projection caching.
- Detection and rejection of recursive/cyclic projection graphs.

Milestone 4: Expression Rewriting

Implement:

```csharp
FusionEngine.Rewrite<TSource, TTarget>(IQueryable<TTarget> query)
```

Support:

- Rewriting `Map().To<T>()`.
- Rewriting `Project().To<T>()`.
- Preserving other query operators.
- Returning new query via provider.
- Not mutating original expression.

Milestone 5: Source Generator

Implement compile-time generation for known calls.

Support:

- Generated object mappers.
- Generated projection expressions.
- Diagnostics.
- Optional interceptors.
- Runtime fallback compatibility.
- Compile-time detection of recursive/cyclic type graphs where possible.


30. Rules for LLM Code Generation

When writing code for FusionMapper, an LLM must:

- Preserve the public fluent API.
- Keep the internal mapping engine internal.
- Use `MappingException` for mapping errors.
- Not introduce external mapping libraries.
- Not require manual mapping profiles.
- Build runtime object mappings as compiled expression trees.
- Avoid runtime reflection inside compiled mapping delegates.
- Prefer expression trees for projections.
- Avoid custom method calls inside generated projection expressions.
- Ensure expression rewriting does not mutate original expressions.
- Write TUnit-compatible tests when tests are requested.
- Update expression rewrite tests to pass the actual query into the rewriting API.
- Throw clear `MappingException` errors instead of silently ignoring impossible mappings.
- Keep implementation incremental and compatible with the milestone plan.
- Avoid `NotImplementedException` in completed features.
- Do not change the intended architecture unless explicitly requested.
- Do not implement `MappingContext`.
- Do not implement identity resolution.
- Do not implement cycle preservation.
- Throw `MappingException` for recursive or cyclic type/object graphs.


31. Definition of Done

A feature is done when:

- It works for the specified scenario.
- It throws `MappingException` for invalid scenarios.
- It does not break existing public API.
- It has tests where applicable.
- Runtime mapping execution is based on compiled expression trees.
- Runtime mapping execution does not use reflection-based member access or invocation.
- Projection output is expression-tree based.
- Expression rewriting produces provider-friendly `.Select(...)` calls.
- Recursive and cyclic mappings throw `MappingException`.
- No identity resolution is introduced.
- No `MappingContext` is introduced.
- No manual mapping configuration is required.
- No external mapping dependency is introduced.
- Code follows the namespace and architecture rules in this specification.
```
