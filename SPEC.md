# FusionMapper Specification

## Project Goal

FusionMapper is a .NET mapping library that performs fully automatic object-to-object mapping and LINQ projection generation without manual mapping profiles, manual maps, or explicit configuration.

The library must:

1. Map objects automatically by convention.
2. Build `IQueryable<T>` projections automatically.
3. Support constructors, records, `required` members, and `init`-only members.
4. Map nested objects in acyclic object graphs by composing compiled expression trees.
5. Support flattening, for example:

   ```csharp
   source.Category.Name -> target.CategoryName
   ```

6. Support automatic aggregate and terminal collection mapping, for example:

   ```csharp
   source.Items.Count() -> target.ItemsCount
   source.Items.Sum(x => x.Price) -> target.ItemsPriceSum
   source.Items.First() -> target.ItemsFirst
   source.Items.Select(x => x.Name).First() -> target.ItemsNameFirst
   source.Order.Items.First().Name -> target.OrderItemsFirstName
   ```

7. Rewrite calls to `Map().To<T>()` and `Project().To<T>()` inside expression trees into provider-translatable `.Select(...)` calls.
8. Use compiled expression-tree mapping for runtime object mapping.
9. Later, use a Roslyn source generator and interceptors to generate compile-time implementations.

The runtime mapping execution model must satisfy the following constraints:

1. Mapping must be based on compiled expression trees.
2. The compiled mapping delegate must not use runtime reflection during mapping execution.
3. Reflection may be used only while discovering members and building the mapping expression, not while executing the compiled mapping delegate.
4. Recursive type mapping is not supported.
5. Cyclic object graphs are not supported.
6. If a recursive or cyclic type/object graph is detected, the mapper must throw `MappingException`.
7. Identity resolution is not supported.
8. There is no `MappingContext`, visited-object tracker, reference tracker, or per-operation identity cache.
9. The project must not require the user to define mapping profiles or manual mapping rules in version 1.

---

## Technology Stack

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

## Current Public Skeleton

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
public static class FusionMapper
{
    public static FusionSource<TSource> Map<TSource>(this TSource source)
        => new(source);

    public static FusionProjection<TSource> Project<TSource>(this IQueryable<TSource> source)
        => new(source);

    static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapDelegates = new();
    static readonly ConcurrentDictionary<(Type Source, Type Target), Delegate> MapToExistingDelegates = new();

    internal static TTarget Map<TSource, TTarget>(TSource source)
    {
        var del = MapDelegates.GetOrAdd((typeof(TSource), typeof(TTarget)), _ => CompileMapping<TSource, TTarget>());
        var func = (Func<TSource, TTarget>)del;
        return func(source);
    }

    internal static TTarget Map<TSource, TTarget>(TSource source, TTarget target)
    {
        if (source == null)
        {
            ArgumentNullException.ThrowIfNull(target);
            return target;
        }
        ArgumentNullException.ThrowIfNull(target);

        var del = MapToExistingDelegates.GetOrAdd((typeof(TSource), typeof(TTarget)), _ => CompileMappingToExisting<TSource, TTarget>());
        var action = (Action<TSource, TTarget>)del;
        action(source, target);
        return target;
    }

    internal static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source)
    {
        // Проекции будут реализованы в Milestone 3
        throw new NotImplementedException("FusionMapper runtime projection engine is not implemented yet.");
    }


    static Delegate CompileMapping<TSource, TTarget>()
    {
        var sourceParam = Expression.Parameter(typeof(TSource), "source");
        var body = MappingBuilder.BuildCreationExpression<TSource, TTarget>(sourceParam);
        var lambda = Expression.Lambda<Func<TSource, TTarget>>(body, sourceParam);
        return lambda.Compile();
    }

    static Delegate CompileMappingToExisting<TSource, TTarget>()
    {
        var sourceParam = Expression.Parameter(typeof(TSource), "source");
        var targetParam = Expression.Parameter(typeof(TTarget), "target");
        var body = MappingBuilder.BuildAssignmentExpression<TSource, TTarget>(sourceParam, targetParam);
        var lambda = Expression.Lambda<Action<TSource, TTarget>>(body, sourceParam, targetParam);
        return lambda.Compile();
    }

}

public readonly struct FusionSource<TSource>(TSource Value)
{
    public TTarget To<TTarget>() => FusionMapper.Map<TSource, TTarget>(Value);
    public TTarget To<TTarget>(TTarget target) => FusionMapper.Map(Value, target);
}

public readonly struct FusionProjection<TSource>(IQueryable<TSource> Value)
{
    public IQueryable<TTarget> To<TTarget>() => FusionMapper.Project<TSource, TTarget>(Value);
}
}
```

The implementation must preserve this public fluent API.

The internal engine may be implemented using compiled expression trees and cached mapping delegates. It must not rely on runtime reflection during mapping execution.

---

## Authoritative API Behavior

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

Object mapping must be executed through compiled expression-tree delegates.

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

---

## Exception Rules

Use only:

```csharp
FusionMapper.MappingException
```

for mapping failures.

Do not introduce another default exception type for mapping errors.

Throw `MappingException` when:

1. A required target member cannot be mapped.
2. A constructor parameter cannot be mapped.
3. Member resolution is ambiguous.
4. A recursive type graph is detected.
5. A cyclic object graph is detected or cannot be safely represented.
6. A recursive projection cannot be safely built.
7. A target type has no usable constructor.
8. A collection mapping is unsupported.
9. An expression tree contains an unsupported FusionMapper call.
10. Mapping into an immutable target is impossible.
11. A mapping would require identity resolution or cycle tracking to complete safely.

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

### 7.1. General Rules

1. Mapping is automatic.
2. The mapper must not require:
   - Mapping profiles.
   - Manual member configuration.
   - Explicit type maps.
   - Attributes in version 1.
3. If mapping is impossible, throw `MappingException`.
4. Do not silently skip required members.

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

Methods are not general source members, except for collection aggregate and terminal operator translation rules described later.

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

## Member Matching Algorithm

For each target member, the mapper resolves a source expression using recursive suffix-based resolution.

The target member name is treated as a suffix that must be consumed by traversing source members, collection element members, and collection operations.

Resolution is performed in two phases:

1. Exact phase.
2. Case-insensitive phase.

In the exact phase, member names are matched using ordinal exact comparison.

If the exact phase does not produce a completed candidate, the mapper repeats member matching using case-insensitive ordinal comparison.

Collection operation names are always matched exactly, even during the case-insensitive phase.

---

### 8.1. Exact Match

Exact ordinal name match:

```csharp
source.Name -> target.Name
```

---

### 8.2. Case-Insensitive Match

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

### 8.3. Recursive Suffix Flattening

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

1. Prefer exact segment matches.
2. Fall back to case-insensitive segment matches if exact resolution fails.
3. There is no fixed artificial flattening depth limit.
4. Resolution terminates because each successful step consumes part of the target suffix.
5. Prefer shorter paths when scores are otherwise equal.
6. Throw `MappingException` on ambiguous equal-score candidates.
7. If a candidate path cannot consume the full suffix, the resolver must backtrack and try another candidate.

The previous recommendation to limit flattening depth to 4 is removed.

---

### 8.4. Recursive Suffix Resolution Behavior

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

---

### 8.5. Collection Operation as Prefix

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

### 8.6. Collection Operation as Suffix

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

### 8.7. Element Property and Operation Candidate Generation

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

Recommended scoring model:

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

---

## Constructor Mapping

### 10.1. Constructor Selection

The mapper must choose a constructor automatically.

Constructor evaluation is performed as a single plan-building loop per candidate constructor.

For each candidate constructor:

1. Attempt to map all constructor parameters from the source.
2. If all constructor parameters can be mapped, attempt to initialize settable and init-only target members.
3. Skip target members whose names match already bound constructor parameter names.
4. If the constructor does not have `SetsRequiredMembersAttribute`, verify that all required members are satisfied.
5. A constructor is usable only if the whole plan succeeds.

Rules:

1. If the target has a public parameterless constructor and all required members can be initialized through member bindings, it may be used.
2. For records, prefer the primary constructor.
3. Otherwise, choose the public constructor with the highest number of bindable parameters.
4. All required constructor parameters must be bindable.
5. If multiple usable constructors have identical bindability scores, throw `MappingException`.

---

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

### 10.3. Members Already Satisfied by Constructor Parameters

When a constructor parameter has been bound, target members with the same name must not be initialized again.

Name comparison for this purpose is case-insensitive.

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

---

## Required Members

The mapper must respect C# `required` members.

Runtime detection may use:

- `RequiredMemberAttribute`.

Source generation should use Roslyn symbol information, such as property required metadata.

Rules:

1. Every required target member must be mapped.
2. A required member may be satisfied by:
   - A constructor parameter.
   - A member initialization.
3. If a required member cannot be satisfied, throw `MappingException`.

Example failure:

```csharp
public class RequiredTarget
{
    public required string Name { get; set; }
}
```

If source has no member that can map to `Name`, throw.

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

---

## Mapping Into Existing Target

This API:

```csharp
source.Map().To(target);
```

must update an existing target instance.

Rules:

1. The target constructor is not invoked.
2. Writable properties and fields may be updated.
3. Init-only members must not be updated.
4. Read-only non-collection members must not be updated.
5. Required members are not revalidated because the target already exists.
6. If `target` is null, throw `ArgumentNullException`.
7. No `MappingContext`, identity tracker, or visited-object tracker is used.

Collection behavior:

1. If the target collection property is writable, assign a new mapped collection when appropriate.
2. If the target collection property is read-only but the collection instance is mutable, clear and repopulate it.
3. If the collection cannot be assigned or mutated, throw `MappingException`.

Example:

```csharp
public class OrderTarget
{
    public List<ItemTarget> Items { get; } = new();
}
```

The mapper should clear and repopulate `Items`.

---

## Null Handling

### 14.1. Null Source

If the source object is null:

1. If the target type is a reference type or nullable value type, return `default`.
2. If the target type is a non-nullable value type, throw `MappingException`.

Example:

```csharp
Product? source = null;
var result = source.Map().To<ProductDto>();
```

Expected:

```csharp
result == null
```

---

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

### 14.3. Null-Safe Flattening and Nullable Value Types

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

---

## Cycles, Identity Resolution, and Mapping Context

### 16.1. Cyclic and Recursive Type Graphs

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

### 16.2. Runtime Cyclic Object Graphs

Cyclic object graphs are not supported.

The mapper does not attempt to preserve cycles.

If a cyclic object graph cannot be represented without cycle tracking or identity resolution, the mapper must fail with `MappingException` rather than entering infinite recursion.

Because recursive type graphs are rejected during mapping construction, many cyclic scenarios are rejected before runtime mapping execution.

---

### 16.3. No Identity Resolution

Identity resolution is not supported.

The mapper does not track already mapped source objects.

The mapper does not guarantee that:

1. The same source instance maps to the same target instance.
2. Multiple references to the same source object preserve reference equality after mapping.
3. Object identity is preserved across the mapped graph.

If the same source object is referenced multiple times in an acyclic graph, it may be mapped independently each time.

---

### 16.4. No MappingContext

There must be no `MappingContext`.

The mapper must not use:

- A per-operation visited-object dictionary.
- A source-to-target identity map.
- A cycle-tracking context.
- A recursive mapping stack stored in a runtime mapping context.

Cached compiled delegates and projection expressions must be stateless or safe for concurrent use without per-operation state.

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

Supported target collection types include:

- Arrays.
- `List<T>`.
- `IEnumerable<T>`.
- `ICollection<T>`.
- `IList<T>`.
- `IReadOnlyList<T>`.
- `IReadOnlyCollection<T>`.

Materialization rules:

1. For arrays, use `ToArray()`.
2. For list-like and read-only collection interfaces, use `ToList()` unless a better target-specific materialization exists.
3. For `IEnumerable<T>`, the mapper may leave the projection as `Select(...)` or materialize if required by the target.

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

1. If source collection is null and target collection is nullable, map to null.
2. If target collection is non-nullable, behavior may be target creation or exception depending on construction requirements.
3. Prefer predictable behavior and throw if impossible.

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

---

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

---

### 18.2. Any

Target:

```csharp
public bool ItemsAny { get; set; }
```

Mapping:

```csharp
ItemsAny = source.Items.Any()
```

---

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

---

### 18.4. All

`All` is supported only when a boolean predicate can be inferred automatically.

Supported forms:

```csharp
target.FlagsAll <- source.Flags.All(x => x)
```

when `source.Flags` is a collection of `bool`.

And:

```csharp
target.ItemsActiveAll <- source.Items.All(x => x.Active)
```

when `Active` resolves to `bool` or `bool?`.

For `bool?`, the mapper may normalize the predicate as:

```csharp
x.Active == true
```

Arbitrary predicate expressions for `All` are not supported in version 1.

If `All` cannot infer a boolean predicate, the candidate is invalid.

---

### 18.5. First / FirstOrDefault / Last / LastOrDefault

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

### 18.6. Aggregate and Terminal Parsing Rules

Aggregate and terminal parsing follows the recursive suffix model.

Rules:

1. Exact direct members win over collection conventions.
2. Collection operation names are matched exactly.
3. An operation may appear:
   - as a suffix;
   - as a prefix before a remaining member path;
   - as part of an element member plus operation candidate.
4. Selector paths may appear before or after terminal operators depending on the target name shape.
5. If a candidate cannot fully resolve the remaining suffix, the resolver must backtrack.
6. Prefer exact matches.
7. Fall back to case-insensitive ordinary member matches.
8. Do not apply case-insensitive matching to operation names.
9. Throw `MappingException` when the aggregate or terminal target cannot be resolved unambiguously.

---

### 18.7. Null and Empty Collection Behavior

Automatic collection conventions should behave predictably.

Recommended runtime behavior:

| Operation | Source collection is null | Source collection is empty |
|---|---:|---:|
| `Count` | `0` | `0` |
| `Any` | `false` | `false` |
| `All` | `false` | `true` |
| `Sum` | default | default |
| `Average` | default | default |
| `Max` | default | default |
| `Min` | default | default |
| `First` | default | default |
| `FirstOrDefault` | default | default |
| `Last` | default | default |
| `LastOrDefault` | default | default |

For reference types, `default` means `null`.

For non-nullable value types, `default` means the CLR default, for example `0` for `decimal` or `int`.

If the target member is nullable and the resolved source path is null, the mapper should produce `null` where possible.

---

## Type Conversion Rules

Version 1 should support at least:

1. Same type assignment.
2. Assignable reference conversions.
3. Nullable wrapping:

   ```csharp
   int -> int?
   ```

4. Nullable unwrapping where safe:

   ```csharp
   int? -> int
   ```

For nullable unwrapping, use predictable behavior:

- If runtime value is null, throw `MappingException`, unless a default convention is explicitly implemented.

For projections, generate provider-compatible null handling.

Additional conversions, such as numeric widening or enum-string conversion, may be added later but are not mandatory for the first implementation.

Do not introduce lossy conversions silently.

---

## IQueryable Projection Rules

When building projections:

1. Build an `Expression<Func<TSource, TTarget>>`.
2. Cache the projection expression by source/target type pair.
3. Inline nested object projections.
4. Avoid `Expression.Invoke`.
5. Avoid compiled delegates inside expression trees.
6. Avoid custom methods unless they are known to be translatable by LINQ providers.
7. Use null-safe conditional expressions for nested references.
8. Detect recursive type-pair projection cycles and throw `MappingException`.
9. Do not rely on identity resolution or a `MappingContext`.
10. Use standard LINQ methods where appropriate:
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

---

## Expression Rewriting Rules

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

Mapping into an existing object is a runtime side-effect operation.

It cannot generally be translated by LINQ providers.

If encountered inside an expression tree, throw `MappingException`.

---

### 21.4. Rewriter Requirements

The rewriter must:

1. Visit all lambda expressions.
2. Visit member initialization expressions.
3. Visit nested method calls.
4. Preserve `Where`, `OrderBy`, `Skip`, `Take`, and other query operators.
5. Not mutate the original expression tree.
6. Return the original query if no rewrite is needed.
7. Use the original query provider to create the rewritten query.

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

1. Building object mapping expressions.
2. Compiling object mapping delegates.
3. Building projection expressions.
4. Caching mapping artifacts.
5. Validating mapping plans.
6. Detecting recursive and cyclic type graphs.
7. Rewriting query expressions.
8. Throwing `MappingException` with useful diagnostics.

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

---

## Caching

The mapper must cache:

1. Compiled object mapping delegates.
2. Projection expressions.
3. Mapping plans.

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

---

## Source Generator Direction

Source generation is a later phase but must remain compatible with the runtime design.

The source generator should:

1. Detect calls to:
   - `Map().To<T>()`
   - `Map().To(target)`
   - `Project().To<T>()`

2. Resolve `TSource` and `TTarget` at compile time.
3. Generate mapping implementations.
4. Emit compile-time diagnostics for mapping failures.
5. Optionally use interceptors to replace calls with generated implementations.
6. Keep runtime fallback for cases that cannot be resolved at compile time.

Generated runtime mapping should follow the same rules:

1. No reflection-based member access during mapping execution.
2. Direct member access and assignment.
3. No identity resolution.
4. No `MappingContext`.
5. Recursive and cyclic type graphs should produce compile-time diagnostics where detectable.

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

1. Missing required members.
2. Ambiguous member matches.
3. Ambiguous constructors.
4. Recursive or cyclic type graphs.
5. Unsupported recursive projections.
6. Open generic mappings that cannot be generated.
7. Inaccessible source or target types.
8. Unsupported expression rewrite scenarios.

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

1. Simple property mapping.
2. Case-insensitive mapping.
3. Exact match priority.
4. Ambiguous mapping failure.
5. Flattening.
6. Deep flattening without artificial depth limitation.
7. Null nested member handling.
8. Null-safe flattening to nullable value types.
9. Constructor mapping.
10. Record mapping.
11. Required member validation.
12. Init-only member mapping.
13. Nested object mapping for acyclic graphs.
14. Recursive type mapping throws `MappingException`.
15. Cyclic type graph detection throws `MappingException`.
16. Collection mapping.
17. Recursive collection element mapping throws `MappingException`.
18. Mapping into existing object.
19. Mapping into existing mutable collection.
20. Collection aggregate mapping.
21. Terminal collection mapping:
    - `First`
    - `FirstOrDefault`
    - `Last`
    - `LastOrDefault`
22. Collection operation before remaining member path.
23. Collection selector before terminal operation.
24. Element property plus operation candidate resolution.
25. Restricted `All` support for bool selectors.
26. IQueryable projection.
27. Expression rewriting.
28. Preservation of `Where`, `OrderBy`, and other query operators.
29. Non-mutation of original expression trees.
30. Compiled mapping execution does not rely on reflection-based member setting or dynamic invocation.
31. No identity resolution behavior is expected or required.

---

## Expression Rewrite Test Pattern

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

## Implementation Milestones

### Milestone 1: Runtime Object Mapping

Implement:

```csharp
FusionEngine.Map<TSource, TTarget>(TSource source)
```

Support:

1. Simple properties.
2. Case-insensitive matching.
3. Recursive suffix flattening without fixed depth limit.
4. Constructor mapping.
5. Records.
6. Required members.
7. Init-only members.
8. Nested objects in acyclic graphs.
9. Collections.
10. Null handling.
11. Detection and rejection of recursive/cyclic type graphs.
12. `MappingException` diagnostics.
13. Compiled expression-tree mapping execution without runtime reflection.

---

### Milestone 2: Mapping Into Existing Object

Implement:

```csharp
FusionEngine.Map<TSource, TTarget>(TSource source, TTarget target)
```

Support:

1. Writable members.
2. Mutable collections.
3. Read-only collection mutation.
4. No required revalidation.
5. No constructor invocation.
6. No `MappingContext`.
7. No identity tracking.

---

### Milestone 3: Projection Building

Implement:

```csharp
FusionEngine.Project<TSource, TTarget>(IQueryable<TSource> source)
```

Support:

1. Expression projections.
2. Constructor mapping inside expressions.
3. Nested projections for acyclic graphs.
4. Collection projections.
5. Aggregate and terminal conventions.
6. Null-safe conditional expressions.
7. Projection caching.
8. Detection and rejection of recursive/cyclic projection graphs.

---

### Milestone 4: Expression Rewriting

Implement:

```csharp
FusionEngine.Rewrite<TSource, TTarget>(IQueryable<TTarget> query)
```

Support:

1. Rewriting `Map().To<T>()`.
2. Rewriting `Project().To<T>()`.
3. Preserving other query operators.
4. Returning new query via provider.
5. Not mutating original expression.

---

### Milestone 5: Source Generator

Implement compile-time generation for known calls.

Support:

1. Generated object mappers.
2. Generated projection expressions.
3. Diagnostics.
4. Optional interceptors.
5. Runtime fallback compatibility.
6. Compile-time detection of recursive/cyclic type graphs where possible.

---

## Rules for LLM Code Generation

When writing code for FusionMapper, an LLM must:

1. Preserve the public fluent API.
2. Keep the internal mapping engine internal.
3. Use `MappingException` for mapping errors.
4. Not introduce external mapping libraries.
5. Not require manual mapping profiles.
6. Build runtime object mappings as compiled expression trees.
7. Avoid runtime reflection inside compiled mapping delegates.
8. Prefer expression trees for projections.
9. Avoid custom method calls inside generated projection expressions.
10. Ensure expression rewriting does not mutate original expressions.
11. Write TUnit-compatible tests when tests are requested.
12. Update expression rewrite tests to pass the actual query into the rewriting API.
13. Throw clear `MappingException` errors instead of silently ignoring impossible mappings.
14. Keep implementation incremental and compatible with the milestone plan.
15. Avoid `NotImplementedException` in completed features.
16. Do not change the intended architecture unless explicitly requested.
17. Do not implement `MappingContext`.
18. Do not implement identity resolution.
19. Do not implement cycle preservation.
20. Throw `MappingException` for recursive or cyclic type/object graphs.
21. Do not impose a fixed artificial flattening depth limit unless explicitly requested.
22. Match collection operation names exactly.
23. Support recursive suffix passing during member resolution.
24. Support constructor evaluation as a single plan-building loop.
25. Support `All` only where a boolean predicate can be inferred automatically.

---

## Definition of Done

A feature is done when:

1. It works for the specified scenario.
2. It throws `MappingException` for invalid scenarios.
3. It does not break existing public API.
4. It has tests where applicable.
5. Runtime mapping execution is based on compiled expression trees.
6. Runtime mapping execution does not use reflection-based member access or invocation.
7. Projection output is expression-tree based.
8. Expression rewriting produces provider-friendly `.Select(...)` calls.
9. Recursive and cyclic mappings throw `MappingException`.
10. No identity resolution is introduced.
11. No `MappingContext` is introduced.
12. No manual mapping configuration is required.
13. No external mapping dependency is introduced.
14. Code follows the namespace and architecture rules in this specification.
15. Suffix-based member resolution supports backtracking.
16. Flattening is not limited by a fixed artificial depth.
17. Constructor mapping evaluates constructor parameters, member bindings, and required members as one plan.
