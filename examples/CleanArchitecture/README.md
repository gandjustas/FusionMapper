# CleanArchitecture example — migrated from AutoMapper to FusionMapper

A full [Jason Taylor's Clean Architecture template](https://github.com/jasontaylordev/CleanArchitecture)
solution (.NET 10, Aspire, EF Core, MediatR) with **AutoMapper 16.2.0 completely replaced by
[FusionMapper](https://www.nuget.org/packages/FusionMapper)**. It shows what a real-world
AutoMapper-to-FusionMapper migration looks like: the entire mapping "configuration" disappears,
and nothing is registered in DI.

The example consumes FusionMapper **as an end user would** — via a `PackageReference` to the published
NuGet package. No project references, no generator wiring: the package configures the source generator
and C# interceptors itself.

```xml
<PackageReference Include="FusionMapper" Version="1.0.0" />
```

Based on the MIT-licensed template, Copyright (c) 2023 JasonTaylorDev — see [LICENSE](LICENSE).
The only modification is the mapper swap described below.

## The complete migration diff

Everything that changed to move off AutoMapper (5 files edited, 3 profile classes deleted):

| File | Change |
|---|---|
| `Directory.Packages.props` / `src/Application/Application.csproj` | `AutoMapper` → `FusionMapper` |
| `src/Application/GlobalUsings.cs` | `global using AutoMapper;` + `AutoMapper.QueryableExtensions;` → `global using FusionMapper;` |
| `LookupDto.cs`, `TodoListDto.cs`, `TodoItemDto.cs` | nested `private class Mapping : Profile` deleted — all three, including the only `ForMember` in the codebase |
| `src/Application/DependencyInjection.cs` | `builder.Services.AddAutoMapper(cfg => cfg.AddMaps(...))` deleted — FusionMapper has nothing to register |
| `src/Application/TodoLists/Queries/GetTodos/GetTodos.cs` | `IMapper` constructor injection removed; EF projection rewritten (see below) |
| `tests/.../MappingTests.cs` | `MapperConfiguration` + `AssertConfigurationIsValid` replaced by two concrete mapping tests |

The EF Core queryable projection — usually the hardest part of leaving AutoMapper:

```csharp
// Before (AutoMapper)
Lists = await _context.TodoLists
    .AsNoTracking()
    .ProjectTo<TodoListDto>(_mapper.ConfigurationProvider)
    .OrderBy(t => t.Title)
    .ToListAsync(cancellationToken);

// After (FusionMapper)
Lists = await _context.TodoLists
    .AsNoTracking()
    .Project()
    .To<TodoListDto>()
    .OrderBy(t => t.Title)
    .ToListAsync(cancellationToken);
```

## Why no mapping configuration is needed

The profiles deleted above existed only to work around AutoMapper's explicitness. FusionMapper
covers the same cases by convention:

| Mapping | Convention |
|---|---|
| `TodoItem.Priority` (`PriorityLevel` enum) → `TodoItemDto.Priority` (`int`) | enum → int conversion |
| `TodoList.Colour` (`Colour` value object) → `TodoListDto.Colour` (`string`) | implicit operator on the value object |
| `TodoList.Items` (`IList<TodoItem>`) → `TodoListDto.Items` (`IReadOnlyCollection<TodoItemDto>`) | nested collection, elements mapped recursively |
| everything else | member names match |

Compile-time validation replaces `AssertConfigurationIsValid`: the source generator emits
FMAP001 errors / FMAP005 warnings for target members it cannot map, and the solution builds
with `TreatWarningsAsErrors=true` — so a silent unmapped member fails the build.

## Run

Requires the .NET 10 SDK.

```bash
# unit tests (includes the mapping tests)
dotnet test examples/CleanArchitecture/tests/Application.UnitTests

# full app (Aspire AppHost + Angular/React client; needs Docker)
dotnet run --project examples/CleanArchitecture/src/AppHost
```
