# Weequery.AutoMapper

AutoMapper support for [Weequery](https://github.com/rvoisin-mcl/Weequery). Weequery decides **which rows**;
AutoMapper decides **what a row looks like**.

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

> [!IMPORTANT]
> **AutoMapper is dual licensed, and taking this package means taking that licence. Read this before installing.**
>
> AutoMapper was MIT through 14.x. From 15 it is free for individuals, for non commercial use and for
> organisations under a revenue threshold, and asks everyone above it to register a paid key at
> [automapper.io](https://automapper.io). Whether you are above that line is yours to check, not this README's
> to guess.
>
> **This package references 15.1.3, and that is the version with the licence.** It used to reference 14.0.0 to
> stay MIT, which cost it [CVE-2026-32933](https://github.com/advisories/GHSA-rvv3-g6hj-g44x), an
> unauthenticated denial of service through uncontrolled recursion on deeply nested object graphs, CVSS 7.5,
> `StackOverflowException` and process termination. It affects **every** MIT release and the MIT line will not be
> patched, so there is no version of this dependency that is both MIT and safe. Buying the patch is what the
> licence is for.
>
> It is a **minimum, not a pin**, so a project already on 16 resolves to 16 and nothing here needs changing.
> 15.1.3 is the lowest patched version of the lowest licensed major, which keeps the floor as low as it can
> honestly go.
>
> If the licence does not suit you, [`Weequery.Mapster`](../Weequery.Mapster/README.md) and
> [`Weequery.Mapperly`](../Weequery.Mapperly/README.md) do the same job against MIT mappers.
>
> Weequery itself has no dependencies, and this package is separate so that installing Weequery never drags
> AutoMapper in.

## If you would rather stay on 14

You can, and it costs two lines. 14.0.0 is the last MIT release, and everything this package uses is present
from 14 onwards, so nothing here stops working:

```xml
<PropertyGroup>
  <NoWarn>NU1605</NoWarn>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Weequery.AutoMapper" Version="3.0.0" />
  <PackageReference Include="AutoMapper" Version="14.0.0" />
</ItemGroup>
```

The second reference is what picks the version: one you declare yourself wins over the one a package you depend
on asks for. The `NoWarn` is not decoration. Without it restore **fails**, exit code and all:

```
error NU1605: Detected package downgrade: AutoMapper from 15.1.3 to 14.0.0
```

That is NuGet's own default in current SDKs rather than anything this package chose, and it is the right one: a
dependency stepping backwards over a security floor should cost a deliberate line of configuration. `NU1903`
then stays on for every restore, telling you about the CVE. Leave it on. Once you have done this it is the only
thing left that knows which version you are on.

**And no, the floor cannot be 14 while 15 stays the default.** A `Version` is a minimum, and NuGet installs the
*lowest* version that satisfies it rather than the newest available, so the floor and the default are the same
number. A floor of 14 would put every consumer on 14, which is where this package started and why it moved.

## What it adds

| | |
|---|---|
| `ProjectTo<T, TDto>(configuration)` | `Build()`, then AutoMapper's own `ProjectTo`. Returns `IQueryable<TDto>` |
| `ProjectToPaged<T, TDto>(configuration)` | `BuildPaged()`, with **only the page** projected. Returns `PagedQuery<TDto>` |

Both also take an `IMapper`, for the caller who has that injected rather than the configuration.

**The paged one is the reason this package exists.** The unpaged case is already a one-liner without it,
`inquiry.Build().ProjectTo<MinionSummary>(configuration)`, and needs only one type argument, because C# infers
all of a method's type arguments or none. The paged case has no such shortcut and one detail that is easy to get
wrong.

## Only the page is projected

`PagedQuery<TDto>` holds an `IQueryable<TDto>` page and an `IQueryable<int>` count, and that asymmetry is the
point. A count is of **rows**, not of what is read off them, so it asks the database for a number and reads no
column at all. Nothing of the mapping is in that statement, so a DTO holding anything AutoMapper will not project
cannot fail a count that never needed it.

Read the number with `FirstOrDefaultAsync`. `CountAsync` compiles and answers `1`, because it counts the rows of
the count query rather than the rows counted; the [core README](../README.md) puts the three endings side by
side.

Neither query has run, exactly as `BuildPaged` promises.

## Order of operations

Filter, sort, window, **then** project. Paging is over the entity's own sort, and projecting first would leave
AutoMapper's output to be sorted by names the DTO may not even have, which is what lets a sort name a field the
DTO does not expose at all:

```csharp
.ApplySorts([new Sort("IsActive", SortDirection.Ascending)])   // IsActive is nowhere on MinionSummary
.ProjectTo<Minion, MinionSummary>(configuration)
```

The allow-list is unchanged: conditions and sorts still name **binding keys on the entity**, and a field nobody
bound is refused exactly as it is anywhere else. The DTO's property names have nothing to do with it.

## One thing it refuses

`ApplyProjection` and a DTO are two answers to one question, one says a row is the keys the caller named, the
other says a row is the DTO. Doing both would honour the DTO and drop the projection without saying so, which for
a caller whose projection arrived in a request is a filter's worth of intention quietly discarded. So it is
refused:

```
ProjectTo cannot be used on an Inquiry that has already had ApplyProjection('[Name], [Pay]') called on it:
the DTO decides what a row holds here, so the projected fields would be silently ignored. Drop one of the two
```

Use `BuildProjected()` for caller-chosen columns, or a DTO for a fixed shape. Not both.

## Versions

Built and tested against **AutoMapper 15.1.3**. The `ProjectTo` surface this package uses is stable across 14,
15 and 16, so referencing a newer one in your own project works without changes here. What is not stable is
`MapperConfiguration`'s own constructor, 14 took the configuring action, 15 and later take it alongside an
`ILoggerFactory`, so the code that *builds* your configuration is yours to keep in step, not this package's:

```csharp
new MapperConfiguration(config => config.CreateMap<Minion, MinionSummary>(), loggerFactory);
```

That one line is the whole of what moving off 14 costs a caller.

## Licence

MIT, same as Weequery. The AutoMapper dependency is not, see the note above.
