# FusionMapper Specification — Current Implementation State

## 1. Project Overview

FusionMapper is a .NET automatic object-to-object mapper with support for:

- Runtime object mapping.
- Mapping into an existing target object.
- `IQueryable<T>` projection.
- Rewriting FusionMapper calls inside LINQ expression trees.
- Constructor mapping.
- Records.
- `required` members.
- `init`-only members.
- Collections.
- Read-only collection mutation.
- Aggregate member conventions such as `Count`, `Sum`, `Any`, etc.
- Nullability-aware mapping using `NullabilityInfoContext`.
- Recursive mapping detection.

The source generator is not implemented yet.

The library must remain fully automatic. Version 1 does not use mapping profiles, manual maps, or user-defined mapping configuration.

---

## 2. Current Implementation Status

Implemented:

- Public fluent API:
  - `source.Map().To<TTarget>()`
  - `source.Map().To<TTarget>(existingTarget)`
  - `queryable.Project().To<TTarget>()`

- Runtime expression building:
  - Creation mapping: `TSource -> TTarget`
  - Assignment mapping: `(TSource, TTarget) -> void`

- Projection building:
  - `IQueryable<TSource> -> IQueryable<TTarget>`

- Expression rewriting:
  - Rewrites `Map().To<T>()`
  - Rewrites `Project().To<T>()`
  - Rewriting is triggered automatically by `Project<TSource, TTarget>()`

- Error reporting:
  - Uses `MappingException`

Not implemented:

- Roslyn source generator.
- Compile-time diagnostics.
- Interceptors.
- Generated compile-time mappers.

---

## 3. Namespace

Primary namespace:

```csharp
namespace FusionMapper;
```

Test namespace:

```csharp
namespace FusionMapper.Tests;
```

---

## 4. Public API

The current public API is:

```csharp
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

Important:

- The public entry point is the static class `FusionMapper`.
- The old `FusionEngine` stub is obsolete if present.
- Current runtime engine methods are internal methods inside `FusionMapper`.

---

## 5. Internal Engine Responsibilities

The internal engine currently lives in:

```csharp
internal static partial class FusionMapper
```

or equivalently inside the same `FusionMapper` static class.

It is responsible for:

1. Compiling and caching object mapping delegates.
2. Building creation expressions:

```csharp
Expression<Func<TSource, TTarget>>
```

3. Building assignment expressions:

```csharp
Expression<Action<TSource, TTarget>>
```

4. Building projections.
5. Rewriting LINQ expression trees.
6. Throwing `MappingException` on mapping failures.

Current internal methods:

```csharp
internal static TTarget Map<TSource, TTarget>(TSource source);

internal static TTarget Map<TSource, TTarget>(TSource source, TTarget target);

internal static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source);

internal static IQueryable<TTarget> Rewrite<TSource, TTarget>(IQueryable<TTarget> query);

static Expression<Func<TSource, TTarget>> GetCreationLambda<TSource, TTarget>();
```

---

## 6. Caching

The current implementation caches:

```csharp
ConcurrentDictionary<(Type Source, Type Target), Delegate> MapDelegates;
ConcurrentDictionary<(Type Source, Type Target), Delegate> MapToExistingDelegates;
ConcurrentDictionary<(Type Source, Type Target), LambdaExpression> MapLambdaExpressions;
```

Behavior:

- Compiled object mapping delegates are cached.
- Compiled assignment delegates are cached.
- Creation lambda expressions are cached for projection and mapping.

Known implementation detail:

- `ExpressionRewriter.InlineProjection` currently calls `MappingBuilder.BuildCreationLambda(...)` directly and does not use `MapLambdaExpressions` cache.
- This is acceptable functionally, but can be optimized later.

---

## 7. Exception Rules

All mapping failures must throw:

```csharp
FusionMapper.MappingException
```

Current `MappingException`:

```csharp
public sealed class MappingException : Exception
{
    public MappingException();
    public MappingException(string message);
    public MappingException(string message, Exception innerException);
}
```

Use `MappingException` for:

- Impossible type mapping.
- Missing required members.
- Missing required constructor parameters.
- No suitable constructor.
- Unsupported recursive/cyclic type graphs.
- Unsupported expression rewrite scenarios.
- Read-only collection mutation failures.
- Invalid rewritten expression type.

Standard .NET exceptions are acceptable for argument null checks, for example:

```csharp
ArgumentNullException.ThrowIfNull(source);
ArgumentNullException.ThrowIfNull(target);
```

But mapping-resolution failures must use `MappingException`.

---

## 8. Object Mapping: New Target Creation

API:

```csharp
source.Map().To<TTarget>();
```

Internal flow:

1. If `source == null` and `TTarget` can accept null, return `default`.
2. Otherwise get or build creation lambda:

```csharp
Expression<Func<TSource, TTarget>>
```

3. Compile and cache delegate.
4. Invoke delegate.

Null-source behavior:

```csharp
if (source == null && (targetType.IsClass || Nullable.GetUnderlyingType(targetType) != null))
{
    return default!;
}
```

So:

- Reference target types return `null`.
- Nullable value target types return `null`.
- Non-nullable value target types are handled by the generated mapping expression and may throw `MappingException` at runtime.

---

## 9. Object Mapping: Existing Target

API:

```csharp
source.Map().To(existingTarget);
```

Internal flow:

1. If `source == null`, return `existingTarget` unchanged.
2. Throw `ArgumentNullException` if `existingTarget == null`.
3. Build or get cached assignment expression:

```csharp
Expression<Action<TSource, TTarget>>
```

4. Invoke it with `(source, target)`.
5. Return the same target instance.

Current assignment behavior:

- Writable public properties are assigned.
- Writable public fields are assigned.
- `init`-only properties are not assigned.
- Read-only non-collection members are not assigned.
- Read-only collection members may be mutated if supported.
- If no members can be mapped, throw `MappingException`.

---

## 10. Creation Mapping Builder

Creation mapping is built by:

```csharp
MappingBuilder.BuildCreationLambda(Type sourceType, Type targetType)
```

It produces:

```csharp
Expression<Func<TSource, TTarget>>
```

The builder:

1. Creates a parameter expression for source.
2. Pushes root mapping pair into `MappingPath`.
3. Builds mapping body.
4. Throws `MappingException` if mapping cannot be built.

Root recursion guard:

```csharp
using var guard = path.Push(targetType, sourceType);
```

---

## 11. Member Resolution

Member resolution is implemented in:

```csharp
GetSourceMemberAccess(...)
```

Current algorithm:

1. If target member suffix is empty, return current source expression.
2. If suffix starts with `_`, remove one leading underscore.
3. Get readable source members:
   - Public instance properties with getter.
   - Public instance fields.
4. Find exact prefix matches:

```csharp
suffix.StartsWith(member.Name, StringComparison.Ordinal)
```

5. Then find case-insensitive prefix matches:

```csharp
suffix.StartsWith(member.Name, StringComparison.OrdinalIgnoreCase)
```

6. For each matched member, recursively consume the matched prefix:

```csharp
suffix[member.Name.Length..]
```

7. If remaining suffix is empty, the member path is complete.
8. If remaining suffix is not empty, continue resolving inside the matched member type.

This enables flattened mapping such as:

```csharp
source.Category.Name -> target.CategoryName
```

because:

```text
CategoryName
^-------^   ^
Category    Name
```

Important current behavior:

- The algorithm is prefix-based.
- It does not split target names by PascalCase independently of source member names.
- It does not currently use explicit scoring.
- The first successful candidate is used.
- There is no ambiguity exception.

Known limitation:

If source has both:

```csharp
Product
ProductName
```

and target is:

```csharp
ProductName
```

the final selected path depends on candidate enumeration order. A future version may introduce scoring where full direct member match has higher priority than prefix flattening.

---

## 12. Nullability Handling

The implementation uses:

```csharp
NullabilityInfoContext
```

with a global lock:

```csharp
private static readonly NullabilityInfoContext NullabilityContext = new();
private static readonly Lock NullabilityLock = new();
```

Nullability is read for:

- Properties.
- Fields.
- Constructor parameters.

The mapper distinguishes:

- `NullabilityState.NotNull`
- `NullabilityState.Nullable`
- `NullabilityState.Unknown`

Current behavior:

- If source can be null, member access is wrapped into a null-checking conditional expression.
- If target accepts null, null source produces default/null.
- If target is a non-nullable value type, null source produces a runtime `MappingException` inside the generated expression.

Example shape:

```csharp
source.Member == null
    ? default
    : source.Member.Nested
```

For value types that do not accept null, the conditional may throw:

```csharp
source == null
    ? throw new MappingException("Cannot map null source to non-nullable value type ...")
    : mappedValue
```

---

## 13. Type Conversion Rules

Current conversion behavior:

1. If target type is assignable from source type, use source expression directly.
2. If both types are value types, attempt:

```csharp
Expression.Convert(sourceExpression, targetType)
```

3. General conversion attempts are cached in:

```csharp
TryConvertCache
```

4. If `Expression.Convert` throws `InvalidOperationException`, the conversion is considered unsupported.

Supported conversions include conversions expressible by `Expression.Convert`, such as many numeric conversions.

Unsupported conversions are skipped and may eventually cause `MappingException` if no other mapping candidate exists.

Strings are not treated as collections.

---

## 14. Object Mapping Rules

Object mapping is implemented in:

```csharp
BuildObjectMapping(...)
```

The current process:

1. Build member assignments for:
   - Public properties with public setter, including `init`.
   - Public non-literal, non-init fields.

2. Collect required member names using `RequiredMemberAttribute`.

3. Get public constructors.

4. Order constructors by descending parameter count.

5. Try to build constructor call for each constructor.

6. Choose the first constructor where:
   - All non-nullable constructor parameters can be mapped.
   - Remaining required members are covered by constructor parameter names.
   - Or constructor has `SetsRequiredMembersAttribute`.

7. Return:

```csharp
Expression.MemberInit(newExpression, memberBindings)
```

Important current behavior:

- Constructor selection prefers greedier constructors first.
- Nullable constructor parameters that cannot be mapped may be filled with `null`.
- There is no explicit constructor ambiguity detection.
- Member bindings may include members also supplied through constructor arguments.

---

## 15. Constructor Mapping Details

Constructor construction is implemented in:

```csharp
BuildConstructorCall(...)
```

For each constructor parameter:

1. Try to resolve source member by parameter name.
2. If found, build mapped expression.
3. If not found:
   - If parameter is nullable, use `Expression.Constant(null, parameter.ParameterType)`.
   - Otherwise constructor is rejected.

Constructor parameter names are matched using the same source member resolution mechanism.

Example:

```csharp
public record ProductDto(string Name, decimal Price);
```

maps from:

```csharp
public class Product
{
    public string Name { get; set; }
    public decimal Price { get; set; }
}
```

to:

```csharp
new ProductDto(source.Name, source.Price)
```

---

## 16. Required Members

Required members are detected using:

```csharp
RequiredMemberAttribute
```

Current detection includes:

- Properties.
- Fields.

Rules:

- Required members must be assigned either by member binding or constructor argument name.
- If a required member remains unassigned, mapping throws `MappingException`.
- Constructor with `SetsRequiredMembersAttribute` satisfies required member validation.

Example error:

```text
Required members of type 'ProductDto' is not mapped: 'Name'
```

---

## 17. Init-Only Members

Init-only properties are supported during creation mapping.

They are included in creation member bindings because they have a public setter with `modreq(IsExternalInit)`.

They are excluded from mapping into an existing target.

Detection:

```csharp
setMethod.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit))
```

---

## 18. Collection Mapping

Collection mapping is implemented in:

```csharp
BuildCollectionMapping(...)
```

A type is considered a collection if:

- It is an array.
- It is generic `IEnumerable<T>`, `ICollection<T>`, `IList<T>`, or `List<T>`.
- It implements `IEnumerable<T>`.

Strings are explicitly excluded.

Collection mapping behavior:

1. Build element mapping expression:

```csharp
sourceItem -> targetItem
```

2. Build:

```csharp
Enumerable.Select(source, itemSelector)
```

3. Materialize target collection.

Current materialization rules:

- If target is array:

```csharp
Enumerable.ToArray(selectCall)
```

- If target is `List<T>`:

```csharp
Enumerable.ToList(selectCall)
```

- If target is an interface:

```csharp
Enumerable.ToList(selectCall)
```

- Else if target has constructor accepting `IEnumerable<T>`:

```csharp
new TargetCollection(selectCall)
```

- Else if target has parameterless constructor and `AddRange(IEnumerable<T>)`:

```csharp
new TargetCollection { AddRange(selectCall) }
```

- Else throw `MappingException`.

---

## 19. Read-Only Collection Mutation

For mapping into an existing target, read-only collection members can be mutated if they expose mutation methods.

Supported requirements:

- Member must be a collection.
- Collection must expose public `Clear()`.
- Collection must expose either:
  - `AddRange(IEnumerable<T>)`
  - or `Add(T)`

Unsupported:

- Read-only arrays.
- Read-only collection members whose current value is null.
- Collections without `Clear`.
- Collections without `Add` or `AddRange`.

If only `Add(T)` exists, the builder emits a manual enumerator loop:

```text
enumerator = mappedSequence.GetEnumerator()
try
{
    while (enumerator.MoveNext())
    {
        targetCollection.Add(enumerator.Current);
    }
}
finally
{
    enumerator.Dispose();
}
```

If source collection is nullable and null, the generated code maps it as:

```csharp
Enumerable.Empty<TTargetElement>()
```

for read-only collection mutation.

---

## 20. Aggregate Collection Conventions

The mapper supports collection aggregate conventions.

Intended operation names:

```text
FirstOrDefault
LastOrDefault
First
Last
Count
Average
Sum
Max
Min
Any
All
```

These operations are recognized when resolving source members for a target member suffix.

Examples:

```csharp
source.Items.Count() -> target.ItemsCount
source.Items.Any() -> target.ItemsAny
source.Items.Sum(x => x.Value) -> target.ItemsValueSum
source.Items.Max(x => x.Price) -> target.ItemsPriceMax
source.Items.All(x => x.IsActive) -> target.ItemsActiveAll
```

Current implementation details:

- Aggregate resolution is implemented in:

```csharp
GetSourceMemberCollection(...)
GetSourceMemberCollectionAggregates(...)
GetEnumerableMethods(...)
```

- It uses `System.Linq.Enumerable` methods.
- Selector-based aggregates first try direct overload with selector lambda.
- If not found, they fall back to:

```csharp
source.Select(selector).Operation()
```

Known implementation defect:

The current `CollectionOperations` array contains trailing spaces:

```csharp
private static readonly string[] CollectionOperations =
[
    "FirstOrDefault ",
    "LastOrDefault ",
    ...
];
```

This prevents matching normal member names such as:

```csharp
ItemsCount
```

because the suffix does not contain a trailing space.

Future fixes should trim operation names or remove trailing spaces.

---

## 21. Recursion and Cycle Handling

Recursion detection is implemented by:

```csharp
MappingPath
```

It maintains a stack of mapping pairs:

```csharp
(Type Target, Type Source)
```

Before entering a nested mapping, the builder pushes a pair:

```csharp
path.Push(targetType, sourceType)
```

If the same pair already exists in the current mapping path, the mapper throws:

```csharp
MappingException
```

Current behavior:

- Recursive type graphs are detected.
- Infinite recursion is prevented.
- Cyclic object graphs are not materialized.
- Object identity preservation is not implemented.
- The current default behavior is to throw.

Example error:

```text
Recursive mapping detected between 'NodeSource' and 'NodeTarget'.
Recursive and cyclic type graphs are not supported.
```

This applies to:

- Creation mapping.
- Assignment mapping.
- Collection element mapping.
- Nested object mapping.

---

## 22. Projection API

Public API:

```csharp
queryable.Project().To<TTarget>();
```

Internal implementation:

```csharp
internal static IQueryable<TTarget> Project<TSource, TTarget>(IQueryable<TSource> source)
{
    ArgumentNullException.ThrowIfNull(source);

    var rewrittenSource = Rewrite<TSource, TSource>(source);

    return rewrittenSource.Select(GetCreationLambda<TSource, TTarget>());
}
```

Projection flow:

1. Take source query.
2. Rewrite its expression tree using `ExpressionRewriter`.
3. Replace embedded FusionMapper calls.
4. Apply final projection:

```csharp
rewrittenSource.Select(source => target)
```

Important consequence:

`Project<TSource, TTarget>` rewrites the query passed to `Project`.

Therefore, to rewrite an existing query containing FusionMapper calls, call `Project()` on that query itself.

Correct rewrite usage:

```csharp
var query = source.Select(x => x.Map().To<SimpleTarget>());

var rewritten = query
    .Project()
    .To<SimpleTarget>();
```

For a query whose element type is `string`:

```csharp
var query = source.Select(x => x.Map().To<SimpleTarget>().Name);

var rewritten = query
    .Project()
    .To<string>();
```

If you instead call:

```csharp
source.Project().To<SimpleTarget>();
```

then the expression tree of `source` is rewritten, not the separate `query` variable.

---

## 23. Expression Rewriting

Expression rewriting is implemented by:

```csharp
ExpressionRewriter : ExpressionVisitor
```

It is invoked by:

```csharp
FusionMapper.Rewrite<TSource, TTarget>(IQueryable<TTarget> query)
```

Current rewrite flow:

1. Visit `query.Expression`.
2. Replace supported FusionMapper calls.
3. Validate that resulting expression type is assignable to `IQueryable<TTarget>`.
4. Create new query:

```csharp
query.Provider.CreateQuery<TTarget>(newExpression)
```

Current implementation creates a new query whenever `Visit(...)` returns a non-null expression.

It does not currently compare:

```csharp
newExpression == query.Expression
```

and therefore may recreate the query even when no semantic rewrite occurred.

---

## 24. Supported Expression Rewrite Scenarios

### 24.1. Map().To<T>()

Before:

```csharp
source.Select(x => x.Map().To<ProductDto>())
```

After rewriting:

```csharp
source.Select(x => new ProductDto
{
    Name = x.Name,
    Price = x.Price
})
```

The rewriter inlines the projection body.

It does not generate a helper method call like:

```csharp
x => MapHelper.Map(x)
```

because such calls are not generally translatable by LINQ providers.

---

### 24.2. Project().To<T>()

Before:

```csharp
someQueryable.Project().To<ProductDto>()
```

After rewriting:

```csharp
someQueryable.Select(x => new ProductDto
{
    Name = x.Name,
    Price = x.Price
})
```

The rewriter builds:

```csharp
Queryable.Select(source, quotedProjectionLambda)
```

---

### 24.3. Unsupported Existing-Target Mapping

This form is not supported inside expression trees:

```csharp
x.Map().To(existingTarget)
```

Reason:

- It mutates an existing object.
- It cannot be reliably translated by LINQ providers.

Current behavior:

```csharp
throw new MappingException(
    "Mapping into an existing object using 'Map().To(target)' is not supported inside query expression trees."
);
```

---

### 24.4. Unsupported Orphan Calls

The rewriter throws if it sees:

```csharp
Map()
```

without immediate `.To<T>()`.

It also throws if it sees:

```csharp
Project()
```

without immediate `.To<T>()`.

Any unsupported method from `FusionMapper` inside a query expression tree throws `MappingException`.

---

## 25. Expression Rewriter Detection Rules

The rewriter recognizes:

### FusionSource.To

```csharp
node.Method.Name == "To"
node.Method.DeclaringType is generic FusionSource<>
node.Object != null
```

### FusionProjection.To

```csharp
node.Method.Name == "To"
node.Method.DeclaringType is generic FusionProjection<>
node.Object != null
```

### FusionMapper.Map

```csharp
node.Method.DeclaringType == typeof(FusionMapper)
node.Method.Name == "Map"
node.Method.IsGenericMethod
node.Method.GetGenericArguments().Length == 1
node.Method.GetParameters().Length == 1
node.Object == null
```

### FusionMapper.Project

```csharp
node.Method.DeclaringType == typeof(FusionMapper)
node.Method.Name == "Project"
node.Method.IsGenericMethod
node.Method.GetGenericArguments().Length == 1
node.Method.GetParameters().Length == 1
node.Object == null
```

The rewriter also unwraps conversion expressions when inspecting call instances.

---

## 26. Projection Expression Requirements

Projection expressions must be suitable for LINQ providers.

Generated projection bodies should contain:

- Member accesses.
- Object initialization.
- Constructor calls.
- Conditional null checks.
- Standard LINQ methods such as:
  - `Select`
  - `Count`
  - `Sum`
  - `Average`
  - `Max`
  - `Min`
  - `Any`
  - `All`
  - `FirstOrDefault`
  - `LastOrDefault`
  - `First`
  - `Last`

Generated projection bodies should avoid:

- Custom mapper methods.
- Compiled delegates.
- Client-side callbacks.
- Non-translatable method calls.

---

## 27. Thread Safety

Current thread-safety measures:

- Mapping delegate caches use `ConcurrentDictionary`.
- Creation lambda cache uses `ConcurrentDictionary`.
- `NullabilityInfoContext` access is protected by a lock.
- `MappingPath` is created per mapping operation.
- Compiled delegates are stateless.

---

## 28. Testing Requirements

Tests use TUnit.

Recommended style:

```csharp
[Test]
public async Task Some_Test()
{
    var result = ...;

    await Assert.That(result).IsEqualTo(expected);
}
```

Expected exception type for mapping failures:

```csharp
MappingException
```

Expression rewrite tests must call `Project()` on the query whose expression tree should be rewritten.

Correct pattern:

```csharp
var query = source
    .Select(x => x.Map().To<SimpleTarget>());

var rewritten = query
    .Project()
    .To<SimpleTarget>();
```

For nested member access:

```csharp
var query = source
    .Select(x => x.Map().To<SimpleTarget>().Name);

var rewritten = query
    .Project()
    .To<string>();
```

Incorrect pattern:

```csharp
var query = source
    .Select(x => x.Map().To<SimpleTarget>());

var rewritten = source
    .Project()
    .To<SimpleTarget>();
```

This does not rewrite `query`; it projects `source`.

---

## 29. Current Known Limitations and Technical Debt

### 29.1. Source Generator Missing

The source generator is not implemented.

All mapping is currently done at runtime using reflection and expression trees.

---

### 29.2. Collection Operation Names Have Trailing Spaces

Current code:

```csharp
private static readonly string[] CollectionOperations =
[
    "FirstOrDefault ",
    "LastOrDefault ",
    ...
];
```

The trailing spaces are likely a defect.

They prevent normal aggregate suffix matching such as:

```csharp
ItemsCount
ItemsAny
ItemsValueSum
```

Future changes should use trimmed operation names.

---

### 29.3. Member Resolution Has No Scoring

Current member resolution uses prefix matching and returns the first successful candidate.

It does not currently prefer:

- Full direct member match over partial prefix match.
- Shorter paths over longer paths.
- Exact case over case-insensitive match with explicit scoring.

This can produce unexpected mappings in ambiguous scenarios.

---

### 29.4. No Ambiguity Errors

The current implementation does not throw when multiple candidate source paths exist.

It chooses the first successful candidate.

Future versions may introduce ambiguity detection and diagnostics.

---

### 29.5. Recursive Graphs Throw

Cyclic object graphs are not materialized.

Current behavior is to throw:

```csharp
MappingException
```

Future versions may support policies such as:

- Throw.
- Preserve references.
- Nullify back references.
- Limit depth.

---

### 29.6. ExpressionRewriter Does Not Use Creation Lambda Cache

`ExpressionRewriter.InlineProjection` currently calls:

```csharp
MappingBuilder.BuildCreationLambda(...)
```

directly.

It could use the shared creation lambda cache for performance.

---

### 29.7. Rewrite May Recreate Query Even If Unchanged

Current `Rewrite` does not check:

```csharp
newExpression == query.Expression
```

It creates a new query whenever `Visit` returns a non-null expression.

This is functionally acceptable but can be optimized.

---

### 29.8. Obsolete FusionEngine Stub

If a file containing:

```csharp
internal static class FusionEngine
```

still exists, it is obsolete.

Current implementation uses internal engine methods inside `FusionMapper`.

The old stub should be removed or ignored.

---

## 30. Future Source Generator Direction

The source generator is planned but not implemented.

When implemented, it should:

1. Detect calls to:

```csharp
Map().To<T>()
Map().To(target)
Project().To<T>()
```

2. Resolve `TSource` and `TTarget` at compile time.
3. Generate mapping implementations matching runtime conventions.
4. Emit compile-time diagnostics for:
   - Missing required members.
   - No suitable constructor.
   - Ambiguous mappings.
   - Unsupported recursive mappings.
   - Unsupported expression rewrite scenarios.

5. Generate projection expressions that are translatable by LINQ providers.
6. Avoid emitting helper method calls inside projection expressions.
7. Remain compatible with runtime fallback.

Good generated projection example:

```csharp
public static Expression<Func<Product, ProductDto>> Projection { get; } =
    source => new ProductDto
    {
        Name = source.Name,
        Price = source.Price
    };
```

Bad generated projection example:

```csharp
public static Expression<Func<Product, ProductDto>> Projection { get; } =
    source => Map_Product_ProductDto(source);
```

The bad form is not generally provider-translatable.

---

## 31. Rules for LLM Code Generation

When modifying or extending FusionMapper, an LLM must:

1. Preserve the current public fluent API.
2. Keep engine implementation internal.
3. Use `MappingException` for mapping errors.
4. Not reintroduce the obsolete `FusionEngine` stub.
5. Not introduce external mapping libraries.
6. Not require manual mapping profiles.
7. Preserve current expression rewriting behavior unless explicitly changing the design.
8. Remember that `Project<TSource, TTarget>` rewrites the query passed into `Project`.
9. Update expression rewrite tests to call `Project()` on the actual query being rewritten.
10. Avoid custom method calls inside generated projection expressions.
11. Keep projections provider-translatable.
12. Respect current recursion behavior: throw `MappingException` on recursive type pairs.
13. Use TUnit for tests.
14. If touching aggregate mapping, fix or account for the trailing-space defect in `CollectionOperations`.
15. Do not implement the source generator unless explicitly requested.

---

## 32. Definition of Done

A change is done when:

- It preserves the current public API.
- It uses `MappingException` for mapping failures.
- It does not break existing runtime mapping behavior.
- It does not break projection behavior.
- It does not break expression rewriting behavior.
- Tests are written or updated where applicable.
- Expression rewrite tests call `Project()` on the query being rewritten.
- Generated projection expressions remain provider-translatable.
- No manual mapping configuration is introduced.
- No external mapping dependency is introduced.
- Source generator work is not started unless explicitly requested.
