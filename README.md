# FusionMapper
<!-- Begin exclude from NuGet readme. -->
<p align="center">
<a href="https://www.nuget.org/packages/FusionMapper"><img src="https://img.shields.io/nuget/v/FusionMapper.svg?label=NuGet&color=informational" alt="NuGet"></a>
<a href="https://www.nuget.org/packages/FusionMapper"><img src="https://img.shields.io/nuget/vpre/FusionMapper.svg?label=NuGet%20preview&color=orange" alt="NuGet Pre-release"></a>
<img src="https://img.shields.io/badge/.NET-8%20%7C%209%20%7C%2010-512BD4" alt=".NET">
<img src="https://img.shields.io/badge/C%23-12%20%7C%2013%20%7C%2014-239120" alt="C#">
</p>
<!-- End exclude from NuGet readme. -->

**FusionMapper** is a modern, high-performance object mapping library for .NET. It combines the developer-friendly, zero-configuration convention-based approach of **AutoMapper** with the compile-time code generation and zero-overhead performance of **Mapperly** and **Mapster**.

Built for .NET 8/9/10 and C# 12/13/14, it leverages **Source Generators** and **C# Interceptors** to eliminate runtime reflection, boilerplate configuration, and mapping overhead.

---

## 📦 1. Installation

Install FusionMapper via NuGet:

**Package Manager:**

```powershell
Install-Package FusionMapper -AllowPrereleaseVersions
```

**.NET CLI:**

```bash
dotnet add package FusionMapper --prerelease 
```

> **Prerequisites:** FusionMapper uses Source Generators and C# Interceptors. Ensure your project targets at least **.NET 8.0** and uses **C# 12** or higher. Interceptors are fully utilized on .NET 9+.

---

## 🚀 2. Basic Usage

FusionMapper requires **zero configuration**. No profiles, no `CreateMap`, no manual setup. Just use the fluent extension methods.

### Create a new object

```csharp
var source = new User { FirstName = "Alice", LastName = "Smith", Age = 30 };

// Maps to a new instance of UserDto
var dto = source.Map().To<UserDto>(); 
```

### Map to an existing object

```csharp
var existingDto = new UserDto { Id = 1 };

// Updates the existing instance in-place
source.Map().To(existingDto); 
```

### Project `IQueryable` (EF Core / LINQ to SQL)

```csharp
// Translates directly to SQL via Expression Trees
var dtos = dbContext.Users
    .Project()
    .To<UserDto>()
    .ToList();
```

---

## ✅ 3. Supported Scenarios & Code Examples

FusionMapper relies on **Convention over Configuration**. It automatically maps properties and fields by name, handles flattening, respects nullability annotations, and supports complex scenarios out of the box.

### 3.1. Properties & Fields

Maps public instance properties and fields by exact name, case-insensitive match, or ignoring leading underscores.

```csharp
public class Source { public string Name { get; set; } = ""; public int _Value { get; set; } }
public class Target { public string name { get; set; } = ""; public int Value { get; set; } }

var target = source.Map().To<Target>(); 
// Maps Name -> name, _Value -> Value
```

### 3.2. Flattening & Deep Flattening

Automatically flattens nested objects by concatenating property names.

```csharp
public class Order { public Customer Customer { get; set; } = new(); }
public class Customer { public Address Address { get; set; } = new(); }
public class Address { public string City { get; set; } = ""; }

public class OrderDto { public string CustomerAddressCity { get; set; } = ""; }

var dto = order.Map().To<OrderDto>();
// dto.CustomerAddressCity == order.Customer.Address.City
```

### 3.3. Nullable Reference Types (NRT) & Null-Safety 🌟

FusionMapper fully understands C# Nullable Reference Types. It analyzes nullability annotations at compile-time and generates safe null-checks, preventing unexpected `NullReferenceException`s during flattening.

**Flattening through nullable intermediate objects:**

```csharp
public class Order { public Customer? Customer { get; set; } } // Nullable intermediate
public class Customer { public string Name { get; set; } = ""; }

public class OrderDto { public string? CustomerName { get; set; } } // Nullable target

// Generated code safely handles the null intermediate:
// dto.CustomerName = source.Customer == null ? null : source.Customer.Name;
```

**Nullable to Non-Nullable (and vice versa):**

```csharp
public class Source {
    public string? Name1 { get; set; }
    public string Name2 { get; set; } = "";
}

public class Target {
    public string Name1 { get; set; } = ""; // Throws InvalidOperationException if source is null
    public string? Name2 { get; set; }         // Safely accepts null
}
```
  
### 3.4. Collections & Materialization 📦

FusionMapper supports a wide variety of collection types. Unlike runtime mappers that use reflection to find `Add` methods, FusionMapper's **Source Generator analyzes your target collection at compile time** and emits the most efficient, direct initialization code.

**Supported Target Types:**

* **Arrays:** `T[]`
* **Lists:** `List<T>`
* **Interfaces:** `IEnumerable<T>`, `IList<T>`, `IReadOnlyList<T>`, `ICollection<T>`
* **Custom Collections:** Any type implementing `IEnumerable<T>`.  
  
#### 🏗 How the Source Generator Optimizes Collections

The generator inspects the target type and selects the optimal creation strategy:

1. **Arrays & `List<T>`:** Generates standard `Enumerable.ToArray()` or `Enumerable.ToList()`.
2. **Interfaces (`IEnumerable<T>`, `IReadOnlyList<T>`, etc.):** Generates modern C# **Collection Expressions** (`[.. items]`) for zero-overhead materialization and minimal allocations.
3. **Custom Collections:**
    * *Constructor Injection:* If it accepts `IEnumerable<T>`, generates `new CustomCollection(items)`.
    * *AddRange:* If it has a parameterless constructor and `AddRange`, generates an optimized initialization block.
    * *Add Loop:* Falls back to a highly optimized `foreach` loop with `Add()`.

#### Example: Mapping to Interfaces & Custom Collections

```csharp
public class Source { public List<Item> Items { get; set; } = []; }

public class Target { 
    public IReadOnlyList<ItemDto> ReadOnlyItems { get; set; } = [];
    public CustomItemCollection CustomItems { get; set; } = new();
}
```

#### What the Source Generator actually emits

```csharp
// For IReadOnlyList<T> (Uses C# 12 Collection Expressions for max performance)
target.ReadOnlyItems = [.. global::System.Linq.Enumerable.Select(source.Items, static __item => new ItemDto { Name = __item.Name })];

// For Custom Collections with IEnumerable<T> constructor
target.CustomItems = new CustomItemCollection(global::System.Linq.Enumerable.Select(source.Items, static __item => new ItemDto { Name = __item.Name }));

// For Custom Collections with AddRange
var __mapped = global::System.Linq.Enumerable.ToList(source.Items.Select(...));
var __result = new CustomCollection();
__result.AddRange(__mapped);
target.CustomItems = __result;
```

### 3.5. In-Place Collection Mutation (Existing Objects) 🔄

When mapping to an **existing** object, FusionMapper does not just replace the collection reference. It intelligently mutates the existing collection in-place to preserve object identity (crucial for UI frameworks like WPF/MAUI or EF Core tracking).

**Behavior:**

1. Calls `Clear()` on the existing collection.
2. Adds the newly mapped items using `AddRange()` or a `foreach` loop with `Add()`.
3. **Arrays:** Because arrays cannot be resized, array properties are skipped or replaced during in-place mutation.
4. **Identity Optimization:** If the source and target element types are identical, it checks `ReferenceEquals` to skip unnecessary clearing and adding.

```csharp
public class Source { public List<Item> Items { get; set; } = []; }

public class Target { 
    // Read-only collection property
    public List<ItemDto> Items { get; } = []; 
}

var target = new Target();
target.Items.Add(new ItemDto { Name = "Old" });

var source = new Source { Items = [new() { Name = "New" }] };
source.Map().To(target);

// Generated code under the hood:
// var __mappedItems = source.Items.Select(i => new ItemDto { Name = i.Name }).ToList();
// target.Items.Clear();
// target.Items.AddRange(__mappedItems);
// (The reference to target.Items remains exactly the same!)
```

### 3.6. Aggregates (Killer Feature) 📊

Performs collection operations purely through target property naming conventions. Supports `Count`, `Any`, `All`, `Sum`, `Average`, `Max`, `Min`, `First`, `Last`, `FirstOrDefault`, `LastOrDefault`.

```csharp
public class Order { public List<OrderLine> Lines { get; set; } = []; }
public class OrderLine { public decimal Amount { get; set; } public bool IsActive { get; set; } }

public class OrderDto {
    public int LinesCount { get; set; }                     // Lines.Count()
    public bool LinesIsActiveAny { get; set; }              // Lines.Any(x => x.IsActive)
    public decimal LinesAmountSum { get; set; }             // Lines.Sum(x => x.Amount)
    public string? LinesNameFirstOrDefault { get; set; }    // Lines.Select(x => x.Name).FirstOrDefault()
}
```

### 3.7. Constructors, `required` & `init`

Automatically selects the best constructor and maps parameters by name. Fully supports C# 11+ `required` and `init` members.

```csharp
public class Target {
    public required string Name { get; init; }
    public int Age { get; }
    public Target(string name, int age) { Name = name; Age = age; }
}
// Generates: new Target(source.Name, source.Age) { Name = source.Name }
```

### 3.8. Type Conversions & Nullable Value Types

Handles implicit/explicit casts, Enum <-> String, Enum <-> Int, and safely unwraps/wraps `Nullable<T>`.

```csharp
public enum Status { Active, Inactive }
public class Source { public Status Status { get; set; } public int? IntValue { get; set; } }
public class Target { public string Status { get; set; } = ""; public int IntValue { get; set; } }

// Enum -> String, Nullable<int> -> int (throws InvalidOperationException if source is null)
```

---

## ❌ 4. Unsupported Scenarios (By Design)

| Feature | Reason |
| :--- | :--- |
| **Manual Configuration** | No `MapFrom`, `Ignore`, or `ConvertUsing`. Everything is strictly convention-based. |
| **Cyclic / Recursive Graphs** | To prevent infinite loops and stack overflows at compile/runtime. |
| **Runtime Polymorphism** | Maps based on *static* compile-time types, not runtime `GetType()`. |
| **Anonymous Types** | Source generators cannot reliably map from/to anonymous types. |

---

## 📊 5. Source Generator Diagnostics

FusionMapper validates your mappings at **compile time** and reports errors directly in your IDE via Roslyn diagnostics.

| Code | Severity | Description |
| :--- | :--- | :--- |
| **FMAP001** | Error | **Cannot generate mapping.** Thrown when types are incompatible, a `required` member cannot be mapped, or no suitable constructor is found. |
| **FMAP002** | Error | **Unsupported mapping inside expression tree.** Thrown when trying to map to an *existing* object (e.g., `Map().To(existing)`) inside an `IQueryable` projection. |
| **FMAP003** | Warning | **Anonymous source/target type.** Thrown when the source or target type is an anonymous type. |

---

## 🏗 6. What Code Does It Generate?

Because FusionMapper uses **Source Generators** and **C# Interceptors**, it generates highly optimized, readable C# code at build time. There is **zero runtime reflection**.

When you write:

```csharp
var dto = user.Map().To<UserDto>();
```

The Source Generator intercepts the call and generates the following code behind the scenes:

### 1. The Mapper Method (Zero-overhead logic)

```csharp
// <auto-generated />
#nullable enable
namespace FusionMapper;

[global::System.CodeDom.Compiler.GeneratedCodeAttribute("FusionMapper", "1.0.0.0")]
static class Generated
{
    [global::System.Runtime.CompilerServices.MethodImplAttribute(
        global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | 
        global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
    public static UserDto Map__User__To__UserDto(User source)
    {
        if (source == null) return default;
        return new UserDto() 
        { 
            Name = source.Name, 
            Age = source.Age 
        };
    }
}
```

### 2. The Interceptor (Rewrites your call, .NET 9+)

```csharp
namespace System.Runtime.CompilerServices
{
    sealed file class InterceptsLocationAttribute : Attribute { /* ... */ }
}

namespace FusionMapper
{
    static file class Interceptors
    {
        [global::System.Runtime.CompilerServices.InterceptsLocation(1, "base64_encoded_location_data")]
        public static UserDto To(this in global::FusionMapper.FusionSource<User> receiver)
        {
            ref User source = ref SourceAccessor<User>.GetValue(in receiver);
            return global::FusionMapper.Generated.Map__User__To__UserDto(source);
        }
    }
}
```

### 3. The Initializer (Pre-warms caches for Expression Trees)

For `IQueryable` projections, it registers expression trees at startup using `ModuleInitializer` and `UnsafeAccessor` (on .NET 9+):

```csharp
static file class Initializer
{
    [global::System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Initialize()
    {
        var cache = GetCache(null!);
        cache.TryAdd((typeof(User), typeof(UserDto)), global::FusionMapper.Generated.Project__User__To__UserDto);
    }
}
```

---

## 🤝 Contributing & License

FusionMapper is open-source and licensed under the **MIT License**. Contributions, bug reports, and feature requests are welcome!

*Built with ❤️ using Qwen, .NET 10, C# 14, and tested with TUnit.*
