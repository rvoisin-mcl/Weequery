# Weequery.AutoMapper

AutoMapper support for [Weequery](https://github.com/rvoisin-mcl/Weequery). Weequery decides **which rows** a query
returns; AutoMapper decides **what a row looks like**. The package projects a filtered, sorted and paged Inquiry
onto a DTO, and projects only the page.

## Installation

```bash
dotnet add package Weequery.AutoMapper
```

> [!IMPORTANT]
> **AutoMapper is dual licensed, and installing this package means accepting that licence.**
>
> AutoMapper was MIT through 14.x. From 15 it is free for individuals, for non-commercial use and for
> organisations under a revenue threshold; organisations above that threshold must register a paid key at
> [automapper.io](https://automapper.io). Whether a given organisation is above the threshold is for it to
> establish.
>
> **This package references AutoMapper 15.1.4, which is a licensed version.** It previously referenced 14.0.0 to
> stay MIT, which exposed it to [CVE-2026-32933](https://github.com/advisories/GHSA-rvv3-g6hj-g44x): an
> unauthenticated denial of service through uncontrolled recursion on deeply nested object graphs, CVSS 7.5,
> ending in a `StackOverflowException` and process termination. The CVE affects **every** MIT release and the MIT
> line will not be patched, so no version of this dependency is both MIT and safe. The licence pays for the patch.
>
> Two later advisories, [GHSA-6vhm-j9h7-f9gh](https://github.com/advisories/GHSA-6vhm-j9h7-f9gh) and
> [GHSA-gpwp-5797-mjff](https://github.com/advisories/GHSA-gpwp-5797-mjff), are further high-severity denials of
> service through uncontrolled recursion. They affect every release below 15.1.4, and 16.0.0 through 16.2.x.
>
> The reference is a **minimum, not a pin**: a project already on 16 resolves to 16 with no change here, and should
> be on 16.3.0 or later. 15.1.4 is the lowest patched version of the lowest licensed major, the lowest floor that
> is still safe.
>
> If the licence is unsuitable, [`Weequery.Mapster`](../Weequery.Mapster/README.md) and
> [`Weequery.Mapperly`](../Weequery.Mapperly/README.md) do the same job against MIT mappers. To stay on 14
> regardless, see [Staying on AutoMapper 14](#staying-on-automapper-14).
>
> Weequery itself has no dependencies; this package is separate so that installing Weequery never brings
> AutoMapper in.

## Example

```csharp
var (page, total) = context.Minions
    .WithWeequery()
    .BindProperties(MinionBindings)
    .ApplyCondition(request.Filter)
    .ApplySorts(request.Sort, DefaultSort)
    .ApplyPagination(request.PageSize, request.Page)
    .ProjectToPaged<Minion, MinionSummary>(configuration);

var matched = await total.FirstOrDefaultAsync();
var rows    = await page.ToListAsync();
```

Only the columns the DTO needs leave the database:

```sql
SELECT "m"."Name", "m"."Alias", "m"."Pay"
FROM "Minions" AS "m"
WHERE "m"."IsActive"
```

## Methods

| Method | Description |
|---|---|
| `ProjectTo<T, TDto>(configuration)` | `Build()`, then AutoMapper's own `ProjectTo`. Returns `IQueryable<TDto>` |
| `ProjectToPaged<T, TDto>(configuration)` | `BuildPaged()`, with **only the page** projected. Returns `PagedQuery<TDto>` |

**`ProjectToPaged` is the reason the package exists.** The unpaged case is already one line without it,
`inquiry.Build().ProjectTo<MinionSummary>(configuration)`, with a single type argument; `ProjectTo<T, TDto>` needs
both, because C# infers all of a method's type arguments or none of them. The paged case has no such shortcut and
one detail that is easy to get wrong (see [Paged projection](#paged-projection)).

## Mapping configuration

Both methods take either a configuration or an `IMapper`, for a caller that has the mapper injected rather than
the configuration.

`MapperConfiguration`'s constructor is not stable across versions: 14 takes the configuring action, 15 and later
take it alongside an `ILoggerFactory`. The code that builds the configuration belongs to the application and must
match the AutoMapper version it references:

```csharp
new MapperConfiguration(config => config.CreateMap<Minion, MinionSummary>(), loggerFactory);
```

## Paged projection

`PagedQuery<TDto>` holds an `IQueryable<TDto>` page and an `IQueryable<int>` count. The count is of **rows**, not of
what is read from them, so it asks the database for a number and reads no column. Nothing of the mapping appears in
that statement, so a DTO holding something AutoMapper cannot project cannot make the count fail.

- Read the count with `FirstOrDefaultAsync`. `CountAsync` compiles and returns `1`, because it counts the rows of
  the count query rather than the rows counted. The [core README](../README.md) compares the three endings.
- Neither query has run when `ProjectToPaged` returns, as with `BuildPaged`.

## Order of operations

Filter, sort, window, **then** project.

Paging is over the entity's own sort; projecting first would leave AutoMapper's output to be sorted by names the
DTO may not have. This order also lets a sort name a field the DTO does not expose:

```csharp
.ApplySorts([new Sort("IsActive", SortDirection.Ascending)])   // IsActive is not on MinionSummary
.ProjectTo<Minion, MinionSummary>(configuration)
```

The allow-list is unchanged. Conditions and sorts name **binding keys on the entity**, and an unbound field is
refused as it is anywhere else. The DTO's property names play no part.

## Conflict with ApplyProjection

`ApplyProjection` and a DTO both define what a row holds: one as the keys the caller named, the other as the DTO.
Honouring both would mean silently dropping the projection, which for a projection that arrived in a request
discards the caller's intent. `ProjectTo` and `ProjectToPaged` therefore throw:

```
ProjectTo cannot be used on an Inquiry that has already had ApplyProjection('[Name], [Pay]') called on it:
the DTO decides what a row holds here, so the projected fields would be silently ignored. Drop one of the two
```

Use `BuildProjected()` for caller-chosen columns, or a DTO for a fixed shape, not both.

## Versions

Built and tested against **AutoMapper 15.1.4**. The `ProjectTo` surface the package uses is stable across 14, 15
and 16, so a newer AutoMapper referenced by the application works unchanged. The only code change when moving off
14 is the `MapperConfiguration` constructor (see [Mapping configuration](#mapping-configuration)).

### Staying on AutoMapper 14

14.0.0 is the last MIT release, and everything the package uses is present from 14 onwards. Override the version
with two additions to the project file:

```xml
<PropertyGroup>
  <NoWarn>NU1605</NoWarn>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Weequery.AutoMapper" Version="3.0.0" />
  <PackageReference Include="AutoMapper" Version="14.0.0" />
</ItemGroup>
```

- The direct `AutoMapper` reference selects the version: a reference declared by the project takes precedence over
  one requested by a package it depends on.
- The `NoWarn` is required. Without it, restore **fails** with a non-zero exit code:

  ```
  error NU1605: Detected package downgrade: AutoMapper from 15.1.4 to 14.0.0
  ```

  This is NuGet's default in current SDKs, not a choice made by this package.

> [!WARNING]
> `NU1903` stays on for every restore, reporting the advisories. **Leave it on.** After the override it is the only
> remaining signal of which AutoMapper version the project is on.

> [!NOTE]
> The floor cannot be 14 while 15 remains the default. A `Version` is a minimum, and NuGet installs the *lowest*
> version that satisfies it rather than the newest, so the floor and the default are the same number. A floor of
> 14 would put every consumer on 14, which is the situation the package moved away from.

## Licence

MIT, as Weequery. The AutoMapper dependency is not MIT; see [Installation](#installation).
