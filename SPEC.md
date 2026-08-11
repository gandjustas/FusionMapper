# FusionMapper Specification

## Project Goal

FusionMapper is a .NET mapping library that performs fully automatic object-to-object mapping and LINQ projection generation without manual mapping profiles, manual maps, or explicit configuration.

The library must:

- Map objects automatically by convention.
- Build `IQueryable<T>` projections automatically.
- Support constructors, records, `required` members, and `init`-only members.
- Map nested objects in acyclic object graphs by composing compiled expression trees.
- Support flattening, for example:

```csharp
source.Category.Name -> target.CategoryName
```

- Support automatic aggregate and terminal collection mapping, for example:

```csharp
source.Items.Count() -> target.ItemsCount
source.Items.Sum(x => x.Price) -> target.ItemsPriceSum
source.Items.First() -> target.ItemsFirst
source.Items.Select(x => x.Name).First() -> target.ItemsNameFirst
source.Order.Items.First().Name -> target.OrderItemsFirstName
```

- Rewrite calls to `Map().To<T>()` and `Project().To<T>()` inside expression trees into provider-translatable `.Select(...)` calls. This is a target feature and is not fully implemented yet.
- Use compiled expression-tree mapping for runtime object mapping.
- Later, use a Roslyn source generator and interceptors to generate compile-time implementations.

The runtime mapping execution model must satisfy the following constraints:

- Mapping must be based on compiled expression trees.
- The compiled mapping delegate must not use runtime reflection during mapping execution.
- Reflection may be used only while discovering members and building the mapping expression, not while executing the compiled mapping delegate.
- Recursive type mapping is not supported.
- Cyclic object graphs are not supported.
- If a recursive or cyclic type/object graph is detected, the mapper must throw `MappingException`.
- Identity resolution is not supported.
- There is no `MappingContext`, visited-object tracker, reference tracker, or per-operation identity cache.
- The project must not require the user to define mapping profiles or manual mapping rules in version 1.

---

## Technology Stack

Language:

- C# 14.

Target:

- .NET 10.

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

---

## Namespaces

Primary library namespace:

```csharp
namespace FusionMapper;
```

Test namespace:

```csharp
namespace FusionMapper.Tests;
```

---

## Current Implementation Status

| Area | Status |
|---|---|
| Simple runtime object mapping | Implemented |
| Case-insensitive member matching | Implemented |
| Suffix/prefix flattening | Implemented |
| Constructor mapping | Implemented with known limitations |
| Records | Supported through constructor mapping |
| Required members | Implemented with known limitations |
| Init-only members | Supported for creation |
| Nested acyclic object mapping | Implemented |
| Recursive/cyclic type detection | Implemented through `MappingPath` |
| Collection mapping | Implemented |
| Aggregate/terminal collection conventions | Partially implemented |
| Mapping into existing object | Implemented |
| Read-only collection mutation | Implemented |
| IQueryable projection | Basic implementation exists |
| Expression rewriting | Not implemented |
| Source generator | Not implemented |

---

## Current Public Skeleton

The current public API is:

```csharp
namespace FusionMapper;

public static class FusionMapper
{
    public static FusionSource<TSource> Map<TSource>(this TSource source)
        => new(source);

    public static FusionProjection<TSource> Project<TSource>(this IQueryable<TSource> source)
        => new(source);

    internal static TTarget Map<TSource, TTarget>(TSource source);
    internal static TTarget Map<TSource, TTarget>(TSource source, TTarget target);
    internal static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source);
}

public readonly struct FusionSource<TSource>(TSource Value)
{
    public TTarget To<TTarget>()
        => FusionMapper.Map<TSource, TTarget>(Value);

    public TTarget To<TTarget>(TTarget target)
        => FusionMapper.Map(Value, target);
}

public readonly struct FusionProjection<TSource>(IQueryable<TSource> Value)
{
    public IQueryable<TTarget> To<TTarget>()
        => FusionMapper.Project<TSource, TTarget>(Value);
}
```

The implementation must preserve this public fluent API.

The expression-rewriting overload:

```csharp
public IQueryable<TTarget> To<TTarget>(IQueryable<TTarget> query)
```

is not present in the current implementation and belongs to the planned expression rewriting milestone.

---

## Authoritative API Behavior

### Object Mapping

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

### Query Projection

This call:

```csharp
queryable.Project().To<TTarget>();
```

must build an expression projection from `TSource` to `TTarget` and return:

```csharp
queryable.Select(projectionExpression)
```

where `projectionExpression` is an `Expression<Func<TSource, TTarget>>`.

The current implementation uses the same cached creation expression that is used for runtime object mapping:

```csharp
source.Select(GetCreationLambda<TSource, TTarget>())
```

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

## Expression Tree Rewriting

Expression rewriting is a planned feature.

The intended API is:

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

Correct future rewrite test pattern:

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

Current status:

- Expression rewriting is not implemented.
- `FusionProjection<TSource>` currently only contains the parameterless `To<TTarget>()` method.

---

## Exception Rules

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

---

## Mapping Rules

### General Rules

Mapping is automatic.

The mapper must not require:

- Mapping profiles.
- Manual member configuration.
- Explicit type maps.
- Attributes in version 1.

If mapping is impossible, throw `MappingException`.

Do not silently skip required members.

---

## Source Members

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

Methods are not general source members, except for collection aggregate and terminal operator translation rules described later.

Current implementation details:

- Source properties must be public, instance, readable, non-indexed, and have a public getter.
- Source fields must be public and instance.
- Properties are enumerated before fields.

---

## Target Members

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

For creation mapping, current implementation binds:

- Public instance properties with a public setter, including init-only setters.
- Public instance non-literal mutable fields.

For mapping into an existing target, current implementation updates:

- Public instance writable properties that are not init-only.
- Public instance mutable fields that are not init-only and not literal.
- Read-only collection members if they can be cleared and repopulated.

---

## Member Matching Algorithm

For each target member, the mapper resolves a source expression using recursive suffix-based resolution.

The target member name is treated as a suffix that must be consumed by traversing source members, collection element members, and collection operations.

Resolution is performed in two phases:

1. Exact phase.
2. Case-insensitive phase.

In the exact phase, member names are matched using ordinal exact comparison.

If the exact phase does not produce a completed candidate, the mapper repeats member matching using case-insensitive ordinal comparison.

Collection operation names are always matched exactly, even during the case-insensitive phase.

Current implementation notes:

- The current resolver uses suffix-prefix matching: a source member name is consumed from the beginning of the remaining target suffix.
- If a source member name matches the beginning of the remaining suffix, resolution recurses into that member with the remaining suffix.
- The resolver does not pre-tokenize PascalCase segments.
- The resolver supports backtracking by trying subsequent candidates if a previous candidate cannot consume the full suffix.
- The current implementation selects the first successful candidate and does not yet implement full scoring or ambiguity detection.

---

## Exact Match

Exact ordinal name match:

```csharp
source.Name -> target.Name
```

---

## Case-Insensitive Match

If no exact match exists, use case-insensitive ordinal comparison:

```csharp
source.name -> target.Name
source.NAME -> target.Name
```

Case-insensitive matching applies to ordinary source member names and prefix flattening candidates.

It does not apply to collection operation names such as:

- `Count`
- `Any`
- `Sum`
- `Average`
- `Max`
- `Min`
- `All`
- `First`
- `FirstOrDefault`
- `Last`
- `LastOrDefault`

These operation names must match exactly.

---

## Recursive Suffix Flattening

If no direct match exists, attempt recursive suffix flattening.

Target member names are effectively split by:

- PascalCase boundaries.
- Underscores.

However, the resolver does not need to pre-tokenize the whole name.

Instead, it passes the remaining suffix recursively.

Example target member:

```csharp
CategoryName
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
- Fall back to case-insensitive segment matches if exact resolution fails.
- There is no fixed artificial flattening depth limit.
- Resolution terminates because each successful step consumes part of the target suffix.
- Prefer shorter paths when scores are otherwise equal.
- Throw `MappingException` on ambiguous equal-score candidates.
- If a candidate path cannot consume the full suffix, the resolver must backtrack and try another candidate.

Current implementation note:

- The current implementation does not yet implement formal scoring.
- Ambiguity detection is not fully implemented.
- The first successful candidate is currently selected.

---

## Recursive Suffix Resolution Behavior

At each resolution step, the mapper has:

- current source type;
- current source expression;
- remaining target suffix.

The resolver should behave as follows:

1. If the remaining suffix is empty, the current source expression is resolved.
2. Try to resolve the whole remaining suffix as a direct member of the current source type.
3. If the current source type is `Nullable<T>`, unwrap it and continue resolution against `T` using null-safe access.
4. Try prefix member resolution:
   - find source members whose names match the beginning of the remaining suffix;
   - cut the matched prefix from the suffix;
   - recurse into the member type with the remaining suffix;
   - if recursion fails, backtrack and try another member.
5. If the current source type is a collection type `IEnumerable<T>`, try collection operation resolution:
   - operation as prefix;
   - operation as suffix;
   - element property plus operation candidates.
6. If no exact candidate resolves the full suffix, repeat ordinary member resolution using case-insensitive matching.
7. Collection operation names remain exact in both phases.

Current implementation note:

- The current resolver removes one leading underscore from the remaining suffix before matching.
- Collection resolution is attempted when the source type implements `IEnumerable<T>`.

---

## Collection Operation as Prefix

If the current source type is a collection type and the remaining suffix starts with an operation name, the mapper may apply that operation to the collection.

Example:

```csharp
target.CollectionFirstDateYear
```

Resolution:

```text
CollectionFirstDateYear
```

Find source collection:

```csharp
source.Collection
```

Remaining suffix:

```text
FirstDateYear
```

Operation prefix:

```text
First
```

Remaining suffix after operation:

```text
DateYear
```

Resulting mapping:

```csharp
source.Collection.First().Date.Year
```

The resolver then continues recursively from the result type of the operation.

---

## Collection Operation as Suffix

If the current source type is a collection type and the remaining suffix ends with an operation name, the mapper may treat the preceding suffix as a selector path on the collection element type.

Example:

```csharp
target.CollectionDateFirst
```

Resolution:

```text
CollectionDateFirst
```

Find source collection:

```csharp
source.Collection
```

Remaining suffix:

```text
DateFirst
```

Operation suffix:

```text
First
```

Selector suffix:

```text
Date
```

Resulting mapping:

```csharp
source.Collection.Select(x => x.Date).First()
```

Another example:

```csharp
target.ItemsValueSum
```

Resulting mapping:

```csharp
source.Items.Sum(x => x.Value)
```

---

## Element Property and Operation Candidate Generation

If the source type is `IEnumerable<T>` and the remaining suffix is not resolved by direct members of the collection type, the mapper may generate candidates by combining:

- readable members of the element type `T`;
- supported collection operation names.

This is a Cartesian-style candidate search:

```text
member(T) + operation
```

Example:

```csharp
target.CollectionDateFirst
```

After resolving `source.Collection`, the remaining suffix is:

```text
DateFirst
```

The collection itself has no member named `Date` or `First`.

The element type `T` has a member:

```csharp
Date
```

Supported operation:

```csharp
First
```

Concatenated candidate:

```text
DateFirst
```

It matches the remaining suffix, so the mapping becomes:

```csharp
source.Collection.Select(x => x.Date).First()
```

If there is a remaining suffix after the operation, resolution continues recursively from the operation result type.

---

## Matching Priority

Recommended target scoring model:

| Match Type | Priority |
|---|---:|
| Exact direct member | 1000 |
| Exact constructor parameter match | 950 |
| Exact flattening / prefix path | 900 |
| Exact collection aggregate or terminal convention | 850 |
| Case-insensitive direct member | 800 |
| Case-insensitive flattening / prefix path | 700 |
| Fallback type conversion | 100 |

If multiple candidates have the same score, throw `MappingException`.

Do not randomly select a candidate.

Exact collection conventions are considered before falling back to case-insensitive ordinary member matching.

Current implementation note:

- This scoring model is not fully implemented.
- The current resolver uses candidate enumeration order and selects the first successful candidate.
- Ambiguity detection must be added.

---

## Constructor Mapping

### Constructor Selection

The mapper must choose a constructor automatically.

Constructor evaluation is performed as a single plan-building loop per candidate constructor.

For each candidate constructor:

1. Attempt to map all constructor parameters from the source.
2. If all constructor parameters can be mapped, attempt to initialize settable and init-only target members.
3. Skip target members whose names match already bound constructor parameter names.
4. If the constructor does not have `SetsRequiredMembersAttribute`, verify that all required members are satisfied.

A constructor is usable only if the whole plan succeeds.

Rules:

- If the target has a public parameterless constructor and all required members can be initialized through member bindings, it may be used.
- For records, prefer the primary constructor.
- Otherwise, choose the public constructor with the highest number of bindable parameters.
- All required constructor parameters must be bindable.
- If multiple usable constructors have identical bindability scores, throw `MappingException`.

Current implementation details:

- Public constructors are currently ordered by descending parameter count.
- A constructor parameter that cannot be mapped rejects the constructor unless the parameter type is nullable.
- If a nullable constructor parameter cannot be mapped, the current implementation may bind `null`.
- Required member validation is performed after member binding and constructor argument resolution.
- Constructors marked with `SetsRequiredMembersAttribute` are accepted without additional required-member validation.

Current known limitations:

- Target members whose names match constructor parameters are not yet skipped from member initialization.
- Required-member satisfaction by constructor arguments currently uses ordinal comparison.
- Constructor ambiguity detection is not fully implemented.

---

## Constructor Parameter Matching

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

## Members Already Satisfied by Constructor Parameters

When a constructor parameter has been bound, target members with the same name must not be initialized again.

Name comparison for this purpose should be case-insensitive.

Example:

```csharp
public class Target
{
    public string Name { get; set; }

    public Target(string name)
    {
        Name = name;
    }
}
```

If constructor parameter `name` is bound from `source.Name`, the member `Name` must not be assigned again during object initialization.

Current implementation note:

- This behavior is not fully implemented yet.
- The current implementation may assign such members again through member initialization.

---

## Required Members

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

Current implementation note:

- Required members are detected using `RequiredMemberAttribute`.
- Required members not satisfied by member bindings or constructor arguments cause `MappingException`.
- Constructor argument satisfaction of required members currently uses ordinal name comparison and should be improved.

---

## Init-Only Members

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

Current implementation:

- Init-only properties are supported during creation mapping.
- Init-only properties are excluded from mapping into an existing target.

---

## Mapping Into Existing Target

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

Current implementation details:

- Writable properties and mutable fields are mapped and assigned.
- Init-only members are skipped.
- Read-only collection members are mutated when possible.
- Read-only arrays are rejected.
- Read-only collections must expose a public `Clear` method and either `AddRange` or `Add`.
- If the existing read-only collection instance is null, runtime mapping throws `MappingException`.
- If the source collection is null during read-only collection mutation, the current implementation maps it to an empty list and clears the target collection.

Current known limitation:

- The current implementation may return `target` when `source == null` before validating that `target` is not null.
- This should be hardened to throw `ArgumentNullException` when `target` is null.

---

## Null Handling

### Null Source

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

For mapping into an existing target:

- If `source == null`, the current implementation returns the target unchanged.
- If `target == null`, the API should throw `ArgumentNullException`.

---

### Null Nested Members

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

Current implementation:

- The resolver wraps member access with conditional null checks when the source expression may be null.
- Value-type results are lifted to nullable types when needed to preserve null semantics.

---

### Null-Safe Flattening and Nullable Value Types

If a flattened source path contains a null reference before reaching a non-nullable value-type member, and the target member is nullable, the mapper must produce `null`.

It must not coerce the missing value into `0`, `false`, or another default value of the underlying type.

Example:

```csharp
public class Source
{
    public Level2Source? Level2 { get; set; }
}

public class Level2Source
{
    public Level3Source? Level3 { get; set; }
}

public class Level3Source
{
    public Level4Source? Level4 { get; set; }
}

public class Level4Source
{
    public int Value { get; set; }
}

public class Target
{
    public int? Level2Level3Level4Value { get; set; }
}
```

If:

```csharp
source.Level2 == null
```

or:

```csharp
source.Level2.Level3.Level4 == null
```

then:

```csharp
target.Level2Level3Level4Value == null
```

If the target member is non-nullable, runtime mapping should throw `MappingException` when the resolved source value is null.

Current implementation:

- Null-safe flattening to nullable value types is supported through conditional expressions and nullable lifting.
- Non-nullable targets may produce runtime `MappingException` through generated conditional throws.
- For query projections, generated throws may not be translatable by all LINQ providers and should be reviewed in future milestones.

---

## Nested Object Mapping and Recursive Types

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

Current implementation:

- Recursion detection is implemented through `MappingPath`.
- `MappingPath` stores a stack of `(Target, Source)` pairs.
- If the same pair is pushed while already present in the current path, `MappingException` is thrown.

---

## Cycles, Identity Resolution, and Mapping Context

### Cyclic and Recursive Type Graphs

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

---

### Runtime Cyclic Object Graphs

Cyclic object graphs are not supported.

The mapper does not attempt to preserve cycles.

If a cyclic object graph cannot be represented without cycle tracking or identity resolution, the mapper must fail with `MappingException` rather than entering infinite recursion.

Because recursive type graphs are rejected during mapping construction, many cyclic scenarios are rejected before runtime mapping execution.

---

### No Identity Resolution

Identity resolution is not supported.

The mapper does not track already mapped source objects.

The mapper does not guarantee that:

- The same source instance maps to the same target instance.
- Multiple references to the same source object preserve reference equality after mapping.
- Object identity is preserved across the mapped graph.

If the same source object is referenced multiple times in an acyclic graph, it may be mapped independently each time.

---

### No MappingContext

There must be no `MappingContext`.

The mapper must not use:

- A per-operation visited-object dictionary.
- A source-to-target identity map.
- A cycle-tracking context.
- A recursive mapping stack stored in a runtime mapping context.

Cached compiled delegates and projection expressions must be stateless or safe for concurrent use without per-operation state.

Current implementation:

- `MappingPath` is used only during expression construction.
- It is not a runtime mapping context.
- It does not track object instances.
- It does not preserve identity.

---

## Collection Mapping

The mapper must support mapping between collection types.

Supported source collection types include:

- Arrays.
- `List<T>`.
- `IEnumerable<T>`.
- `ICollection<T>`.
- `IList<T>`.
- `IReadOnlyList<T>`.
- `IReadOnlyCollection<T>`.
- Any type implementing `IEnumerable<T>`.

Supported target collection types include:

- Arrays.
- `List<T>`.
- `IEnumerable<T>`.
- `ICollection<T>`.
- `IList<T>`.
- `IReadOnlyList<T>`.
- `IReadOnlyCollection<T>`.
- Custom collection types with a suitable constructor or mutation methods.

Materialization rules:

- For arrays, use `ToArray()`.
- For `List<T>`, use `ToList()`.
- For interface-like target collections, use `ToList()` unless a better target-specific materialization exists.
- For custom collection types:
  - prefer a constructor accepting `IEnumerable<TElement>`;
  - otherwise, use a parameterless constructor plus `AddRange` if available;
  - otherwise, throw `MappingException`.

Element mapping must follow the same acyclic mapping rules as object mapping.

Example:

```csharp
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

Current implementation details:

- Collection detection is based on arrays, generic collection interfaces, and `IEnumerable<T>` implementation.
- Collection projection uses `Enumerable.Select`.
- Arrays are materialized using `ToArray`.
- Interfaces and `List<T>` are materialized using `ToList`.
- Read-only collection mutation uses `Clear` plus `AddRange` or `Add`.

---

## Aggregate and Terminal Collection Mapping

The mapper supports convention-based aggregate and terminal collection mapping.

Supported operation names:

- `Count`
- `Sum`
- `Average`
- `Max`
- `Min`
- `Any`
- `All`
- `First`
- `FirstOrDefault`
- `Last`
- `LastOrDefault`

Operation names are matched exactly and are case-sensitive.

Current implementation note:

- The current `CollectionOperations` array in `MappingBuilder` contains trailing whitespace in operation names.
- This is an implementation defect and must be fixed.
- The specification requires operation names without whitespace.

---

### Count

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

---

### Any

Target:

```csharp
public bool ItemsAny { get; set; }
```

Mapping:

```csharp
ItemsAny = source.Items.Any()
```

---

### Sum / Average / Max / Min

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

---

### All

`All` is supported only when a boolean predicate can be inferred automatically.

Target supported form:

```csharp
target.ItemsActiveAll <- source.Items.All(x => x.Active)
```

when `Active` resolves to `bool`.

For `bool?`, the mapper may normalize the predicate as:

```csharp
x.Active == true
```

Arbitrary predicate expressions for `All` are not supported in version 1.

If `All` cannot infer a boolean predicate, the candidate is invalid.

Current implementation note:

- The current implementation supports `All` only when a boolean element member selector can be resolved.
- Parameterless `All` over a collection of `bool`, equivalent to `source.Flags.All(x => x)`, is not implemented yet.
- `bool?` normalization is not implemented yet.

---

### First / FirstOrDefault / Last / LastOrDefault

The mapper supports terminal collection conventions for:

- `First`
- `FirstOrDefault`
- `Last`
- `LastOrDefault`

Examples:

```csharp
target.ItemsFirst <- source.Items.First()
target.ItemsFirstOrDefault <- source.Items.FirstOrDefault()
target.ItemsLast <- source.Items.Last()
target.ItemsLastOrDefault <- source.Items.LastOrDefault()
```

With selector:

```csharp
target.ItemsNameFirst <- source.Items.Select(x => x.Name).First()
target.ItemsNameFirstOrDefault <- source.Items.Select(x => x.Name).FirstOrDefault()
target.ItemsValueLast <- source.Items.Select(x => x.Value).Last()
```

With deep flattening:

```csharp
target.OrderItemsFirst <- source.Order.Items.First()
target.OrderItemsFirstName <- source.Order.Items.First().Name
target.OrderItemsNameFirst <- source.Order.Items.Select(x => x.Name).First()
```

With nested member after terminal operator:

```csharp
target.CollectionFirstDateYear <- source.Collection.First().Date.Year
```

With selector before terminal operator:

```csharp
target.CollectionDateFirst <- source.Collection.Select(x => x.Date).First()
```

---

## Aggregate and Terminal Parsing Rules

Aggregate and terminal parsing follows the recursive suffix model.

Rules:

- Exact direct members win over collection conventions.
- Collection operation names are matched exactly.
- An operation may appear:
  - as a suffix;
  - as a prefix before a remaining member path;
  - as part of an element member plus operation candidate.
- Selector paths may appear before or after terminal operators depending on the target name shape.
- If a candidate cannot fully resolve the remaining suffix, the resolver must backtrack.
- Prefer exact matches.
- Fall back to case-insensitive ordinary member matches.
- Do not apply case-insensitive matching to operation names.
- Throw `MappingException` when the aggregate or terminal target cannot be resolved unambiguously.

Current implementation note:

- The current resolver supports operation prefix resolution.
- The current resolver supports element-member-plus-operation suffix resolution.
- Ambiguity detection is not fully implemented.

---

## Null and Empty Collection Behavior

Target recommended runtime behavior:

| Operation | Source collection is null | Source collection is empty |
|---|---|---|
| Count | 0 | 0 |
| Any | false | false |
| All | false | true |
| Sum | default | default |
| Average | default | default |
| Max | default | default |
| Min | default | default |
| First | default | default |
| FirstOrDefault | default | default |
| Last | default | default |
| LastOrDefault | default | default |

For reference types, `default` means `null`.

For non-nullable value types, `default` means the CLR default, for example `0` for `decimal` or `int`.

If the target member is nullable and the resolved source path is null, the mapper should produce `null` where possible.

Current implementation note:

- The current implementation relies on generated null conditional expressions and standard `System.Linq.Enumerable` behavior.
- The recommended null/empty table is not fully implemented.
- In particular, operations such as `First`, `Last`, `Sum`, `Average`, `Max`, and `Min` may throw standard LINQ exceptions on empty collections.
- This area needs hardening to match predictable mapper behavior.

---

## Type Conversion Rules

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

Current implementation note:

- The current implementation uses `Expression.Convert` where conversion is possible.
- This may allow numeric conversions that can be lossy.
- Conversion policy should be hardened to avoid silent lossy conversions.

---

## IQueryable Projection Rules

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
- Use standard LINQ methods where appropriate:
  - `Count()`
  - `Sum(...)`
  - `Average(...)`
  - `Max(...)`
  - `Min(...)`
  - `Any()`
  - `All(...)` where predicate inference is supported
  - `First()`
  - `FirstOrDefault()`
  - `Last()`
  - `LastOrDefault()`

Example:

```csharp
source => new ProductDto
{
    Name = source.Name,
    Price = source.Price,
    CategoryName = source.Category != null ? source.Category.Name : null,
    ItemsCount = source.Items.Count(),
    ItemsPriceSum = source.Items.Sum(x => x.Price),
    FirstItemName = source.Items.Select(x => x.Name).FirstOrDefault()
}
```

This expression must be suitable for providers such as EF Core.

Current implementation:

- Projection currently reuses the cached creation lambda used for runtime object mapping.
- This satisfies the basic requirement of returning `source.Select(...)`.
- However, some generated expressions, such as conditional throws for null invalid values, may not be translatable by all LINQ providers.
- A dedicated projection builder may be introduced later to improve provider compatibility.

---

## Expression Rewriting Rules

The expression rewriter must visit query expression trees and replace FusionMapper fluent calls with translatable LINQ equivalents.

Current status:

- Not implemented.

Target behavior is described below.

---

### Rewrite Map().To<T>()

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

### Rewrite Project().To<T>()

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

### Unsupported Calls

The following call is not supported inside query projection expression trees:

```csharp
x.Map().To(existingTarget)
```

Reason:

Mapping into an existing object is a runtime side-effect operation.

It cannot generally be translated by LINQ providers.

If encountered inside an expression tree, throw `MappingException`.

---

### Rewriter Requirements

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

## Runtime Engine Responsibilities

The internal engine is responsible for:

- Building object mapping expressions.
- Compiling object mapping delegates.
- Building projection expressions.
- Caching mapping artifacts.
- Validating mapping plans.
- Detecting recursive and cyclic type graphs.
- Rewriting query expressions.
- Throwing `MappingException` with useful diagnostics.

Current implementation structure:

- `FusionMapper` owns public fluent API and caches.
- `MappingBuilder` builds creation and assignment expressions.
- `MappingPath` detects recursive mapping paths during expression construction.
- `MappingException` represents mapping failures.

Recommended future internal structure:

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

---

## Caching

The mapper must cache:

- Compiled object mapping delegates.
- Projection expressions.
- Mapping plans.

Current caches:

```csharp
static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapDelegates = new();
static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapToExistingDelegates = new();
static readonly ConcurrentDictionary<(Type Source, Type Target), Expression> MapLambdaExpressions = new();
```

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

---

## Thread Safety

Mapping caches must be thread-safe.

Shared cached delegates and expressions must be stateless or safe for concurrent use.

There is no per-operation `MappingContext`.

The mapper must not require per-call mutable state for identity resolution or cycle tracking.

Current implementation details:

- Caches use `ConcurrentDictionary`.
- `NullabilityInfoContext` access is protected by a lock because `NullabilityInfoContext` is not guaranteed to be thread-safe.

---

## Source Generator Direction

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

---

## Source Generator Diagnostics

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

---

## Testing Requirements

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
- Deep flattening without artificial depth limitation.
- Null nested member handling.
- Null-safe flattening to nullable value types.
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
- Terminal collection mapping:
  - `First`
  - `FirstOrDefault`
  - `Last`
  - `LastOrDefault`
- Collection operation before remaining member path.
- Collection selector before terminal operation.
- Element property plus operation candidate resolution.
- Restricted `All` support for bool selectors.
- IQueryable projection.
- Expression rewriting.
- Preservation of `Where`, `OrderBy`, and other query operators.
- Non-mutation of original expression trees.
- Compiled mapping execution does not rely on reflection-based member setting or dynamic invocation.
- No identity resolution behavior is expected or required.

---

## Expression Rewrite Test Pattern

This pattern applies after expression rewriting is implemented.

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

---

## Implementation Milestones

### Milestone 1: Runtime Object Mapping

Status: mostly implemented.

Implement:

```csharp
FusionEngine.Map<TSource, TTarget>(TSource source)
```

Support:

- Simple properties.
- Case-insensitive matching.
- Recursive suffix flattening without fixed depth limit.
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

Remaining hardening:

- Candidate scoring.
- Ambiguity detection.
- Constructor-bound member skipping.
- Lossy conversion protection.

---

### Milestone 2: Mapping Into Existing Object

Status: implemented.

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

Remaining hardening:

- Consistent `ArgumentNullException` validation for null target.
- More predictable null-source collection behavior.

---

### Milestone 3: Projection Building

Status: basic implementation exists.

Implement:

```csharp
FusionEngine.Project<TSource, TTarget>(IQueryable<TSource> source)
```

Support:

- Expression projections.
- Constructor mapping inside expressions.
- Nested projections for acyclic graphs.
- Collection projections.
- Aggregate and terminal conventions.
- Null-safe conditional expressions.
- Projection caching.
- Detection and rejection of recursive/cyclic projection graphs.

Current implementation:

- Projection currently reuses the runtime creation expression.
- This provides basic `IQueryable` projection.
- Provider compatibility needs improvement.

---

### Milestone 4: Expression Rewriting

Status: not implemented.

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

---

### Milestone 5: Source Generator

Status: not implemented.

Implement compile-time generation for known calls.

Support:

- Generated object mappers.
- Generated projection expressions.
- Diagnostics.
- Optional interceptors.
- Runtime fallback compatibility.
- Compile-time detection of recursive/cyclic type graphs where possible.

---

## Known Implementation Gaps

The following gaps are known and should be addressed in future iterations:

1. Expression rewriting is not implemented.
   - `FusionProjection<TSource>` does not yet contain `To<TTarget>(IQueryable<TTarget> query)`.

2. Collection operation names in `MappingBuilder.CollectionOperations` currently contain trailing whitespace.
   - This prevents aggregate and terminal conventions from matching correctly.
   - Operation names must be exact and whitespace-free.

3. Candidate scoring is not implemented.
   - The resolver currently selects the first successful candidate.
   - Ambiguous mappings may not throw yet.

4. Constructor-bound members are not skipped during member initialization.
   - A property bound through a constructor parameter may be assigned again in `MemberInit`.

5. Required member satisfaction by constructor parameters uses ordinal comparison.
   - Case-insensitive matching should be supported where appropriate.

6. `All` support is limited.
   - `source.Flags.All(x => x)` for a collection of `bool` is not implemented.
   - `bool?` predicate normalization is not implemented.

7. Null and empty collection behavior is not fully normalized.
   - The recommended table for null/empty aggregates is not fully implemented.
   - Some operations may throw standard LINQ exceptions on empty collections.

8. Mapping into an existing target may not validate null target when source is null.
   - The API should throw `ArgumentNullException` when `target` is null.

9. Projection expressions may include `Expression.Throw`.
   - This can reduce translatability for providers such as EF Core.
   - A dedicated projection builder may be required.

10. Conversion policy is not fully hardened.
   - `Expression.Convert` may permit lossy numeric conversions.
   - The mapper should avoid silent lossy conversions.

---

## Rules for LLM Code Generation

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
- Do not impose a fixed artificial flattening depth limit unless explicitly requested.
- Match collection operation names exactly.
- Support recursive suffix passing during member resolution.
- Support constructor evaluation as a single plan-building loop.
- Support `All` only where a boolean predicate can be inferred automatically.

---

## Definition of Done

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
- Suffix-based member resolution supports backtracking.
- Flattening is not limited by a fixed artificial depth.
- Constructor mapping evaluates constructor parameters, member bindings, and required members as one plan.
