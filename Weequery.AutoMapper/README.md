# Weequery.AutoMapper

AutoMapper support for [Weequery](https://github.com/rvoisin-mcl/Weequery). I decide **which rows**. AutoMapper
decides **what a row looks like**. A tidy division of labour, and the only one in this repository that comes with
a lawyer.

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

Only the columns the DTO needs ever leave the database:

```sql
SELECT "m"."Name", "m"."Alias", "m"."Pay"
FROM "Minions" AS "m"
WHERE "m"."IsActive"
```

> [!IMPORTANT]
> **AutoMapper is dual licensed, and taking this package means taking that licence. Read this before you install
> anything.** I am telling you now, at the top, in a box, because I refuse to have this conversation later.
>
> AutoMapper was MIT through 14.x. From 15 it is free for individuals, for non commercial use and for
> organisations under a revenue threshold, and asks everyone above that line to register a paid key at
> [automapper.io](https://automapper.io). Whether *you* are above the line is yours to establish. It is not this
> README's business to guess at your revenue, however curious I may be.
>
> **This package references 15.1.3, and that is the version with the licence.** It used to reference 14.0.0 to
> stay MIT, which cost it [CVE-2026-32933](https://github.com/advisories/GHSA-rvv3-g6hj-g44x): an
> unauthenticated denial of service through uncontrolled recursion on deeply nested object graphs, CVSS 7.5,
> `StackOverflowException`, process termination. It affects **every** MIT release and the MIT line will never be
> patched, so there is no version of this dependency that is both MIT and safe. Buying the patch is what the
> licence is for. I am in the denial-of-service business and even I will not ship you one by accident.
>
> It is a **minimum, not a pin**, so a project already on 16 resolves to 16 and nothing here needs touching.
> 15.1.3 is the lowest patched version of the lowest licensed major, which keeps the floor as low as it can
> honestly go, and I do mean honestly.
>
> If the licence does not suit you, [`Weequery.Mapster`](../Weequery.Mapster/README.md) and
> [`Weequery.Mapperly`](../Weequery.Mapperly/README.md) do precisely the same job against MIT mappers, and I
> will not be offended.
>
> I have no dependencies myself, and this package is separate so that installing me never drags AutoMapper in
> behind you.

## If you would rather stay on 14

You can. It costs two lines and a small amount of dignity. 14.0.0 is the last MIT release, and everything I use
is present from 14 onwards, so nothing here stops working:

```xml
<PropertyGroup>
  <NoWarn>NU1605</NoWarn>
</PropertyGroup>

<ItemGroup>
  <PackageReference Include="Weequery.AutoMapper" Version="3.0.0" />
  <PackageReference Include="AutoMapper" Version="14.0.0" />
</ItemGroup>
```

The second reference is what picks the version: one you declare yourself beats the one a package you depend on
asks for. The `NoWarn` is not decoration. Without it, restore **fails**, exit code and all:

```
error NU1605: Detected package downgrade: AutoMapper from 15.1.3 to 14.0.0
```

That is NuGet's own default in current SDKs rather than anything I chose, and for once I agree with it entirely:
a dependency stepping backwards over a security floor ought to cost somebody a deliberate line of configuration.
`NU1903` then stays on for every restore, reminding you about the CVE. **Leave it on.** Once you have done this,
that warning is the only thing left in the building that knows which version you are on.

**And no, the floor cannot be 14 while 15 stays the default.** A `Version` is a minimum, and NuGet installs the
*lowest* version that satisfies it rather than the newest available, so the floor and the default are the same
number. A floor of 14 would put every consumer on 14, which is exactly where this package started and exactly
why it moved.

## What it adds

| | |
|---|---|
| `ProjectTo<T, TDto>(configuration)` | `Build()`, then AutoMapper's own `ProjectTo`. Returns `IQueryable<TDto>` |
| `ProjectToPaged<T, TDto>(configuration)` | `BuildPaged()`, with **only the page** projected. Returns `PagedQuery<TDto>` |

Both will also take an `IMapper`, for the caller who has that injected rather than the configuration. I am
accommodating.

**The paged one is the reason this package exists.** The unpaged case is already a one-liner without me,
`inquiry.Build().ProjectTo<MinionSummary>(configuration)`, and needs only one type argument, because C# infers
all of a method's type arguments or none of them. The paged case has no such shortcut and exactly one detail
that is easy to get wrong.

## Only the page is projected

`PagedQuery<TDto>` holds an `IQueryable<TDto>` page and an `IQueryable<int>` count, and that asymmetry is the
whole point. A count is of **rows**, not of what you read off them, so it asks the database for a number and
reads no column at all. Nothing of the mapping appears in that statement, so a DTO holding something AutoMapper
will not project cannot fail a count that never needed it.

Read the number with `FirstOrDefaultAsync`. `CountAsync` compiles and answers `1`, because it counts the rows of
the count query rather than the rows counted. The [core README](../README.md) puts the three endings side by
side, where you may study them at your leisure.

Neither query has run, exactly as `BuildPaged` promised.

## Order of operations

Filter, sort, window, **then** project.

Paging is over the entity's own sort, and projecting first would leave AutoMapper's output to be sorted by names
the DTO may not even have. My order is also what lets a sort name a field the DTO does not expose at all:

```csharp
.ApplySorts([new Sort("IsActive", SortDirection.Ascending)])   // IsActive is nowhere on MinionSummary
.ProjectTo<Minion, MinionSummary>(configuration)
```

The allow-list is unchanged by any of it. Conditions and sorts still name **binding keys on the entity**, and a
field nobody bound is refused exactly as it is anywhere else. The DTO's property names have nothing to do with
this and never did.

## One thing it refuses

`ApplyProjection` and a DTO are two answers to one question. One says a row is the keys the caller named. The
other says a row is the DTO. Doing both would honour the DTO and drop the projection without a word, which for a
caller whose projection arrived in a request is a filter's worth of intention quietly discarded. So I refuse:

```
ProjectTo cannot be used on an Inquiry that has already had ApplyProjection('[Name], [Pay]') called on it:
the DTO decides what a row holds here, so the projected fields would be silently ignored. Drop one of the two
```

Use `BuildProjected()` for caller-chosen columns, or a DTO for a fixed shape. Not both. I will not be party to
both.

## Versions

Built and tested against **AutoMapper 15.1.3**. The `ProjectTo` surface I use is stable across 14, 15 and 16, so
referencing a newer one in your own project works without a thing changing here.

What is *not* stable is `MapperConfiguration`'s own constructor: 14 took the configuring action, 15 and later
take it alongside an `ILoggerFactory`. So the code that *builds* your configuration is yours to keep in step,
not mine:

```csharp
new MapperConfiguration(config => config.CreateMap<Minion, MinionSummary>(), loggerFactory);
```

That single line is the whole of what moving off 14 costs you. I have seen people pay more for less.

## Licence

MIT, same as me. The AutoMapper dependency is emphatically not, see the box above, which I put there for a
reason.
