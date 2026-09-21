# Weequery.Mapster

Mapster support for [Weequery](https://github.com/rvoisin-mcl/Weequery). Weequery decides **which rows**;
Mapster decides **what a row looks like**.

```csharp
var (page, total) = context.Minions
    .WithWeequery()
    .BindProperties(MinionBindings)
    .ApplyCondition(request.Filter)
    .ApplySorts(request.Sort, DefaultSort)
    .ApplyPagination(request.PageSize, request.Page)
    .ProjectToPaged<Minion, MinionSummary>();

var matched = await total.FirstOrDefaultAsync();
var rows    = await page.ToListAsync();
```

Only the columns the DTO needs leave the database:

```sql
SELECT "m"."Name", "m"."Alias", "m"."Pay"
FROM "Minions" AS "m"
WHERE "m"."IsActive"
```

Mapster is MIT and has stayed MIT, so there is no licence to inherit and nothing to warn about. If you use
AutoMapper or Mapperly instead, [`Weequery.AutoMapper`](../Weequery.AutoMapper/README.md) and
[`Weequery.Mapperly`](../Weequery.Mapperly/README.md) do the same job the same way.

## What it adds

| | |
|---|---|
| `ProjectTo<T, TDto>(config?)` | `Build()`, then Mapster's own `ProjectToType`. Returns `IQueryable<TDto>` |
| `ProjectToPaged<T, TDto>(config?)` | `BuildPaged()`, with **only the page** projected. Returns `PagedQuery<TDto>` |

**The paged one is the reason this package exists.** The unpaged case is already a one-liner without it,
`inquiry.Build().ProjectToType<MinionSummary>()`, and needs only one type argument, because C# infers all of a
method's type arguments or none. The paged case has no such shortcut and one detail that is easy to get wrong.

## The configuration is optional

This is the one place Mapster differs from the AutoMapper companion. Mapster maps by convention, so a DTO whose
names line up needs no registration at all:

```csharp
.ProjectTo<Minion, MinionSummary>()                 // TypeAdapterConfig.GlobalSettings
.ProjectTo<Minion, MinionSummary>(myConfiguration)  // or one you keep per scope
```

Passing nothing uses `TypeAdapterConfig.GlobalSettings`, exactly as Mapster's own parameterless overload does.

> [!NOTE]
> Convention is convenient right up until it is silent. A member the convention does not match is left at its
> default rather than reported, so `MinionSummary.Salary` against `Minion.Pay` comes back as `0` until something
> tells Mapster they are the same thing:
>
> ```csharp
> config.NewConfig<Minion, MinionSummary>().Map(summary => summary.Salary, minion => minion.Pay);
> ```
>
> That is Mapster's behaviour rather than this package's, and it is worth knowing because the wrong answer is a
> zero rather than an exception.

## Only the page is projected

`PagedQuery<TDto>` holds an `IQueryable<TDto>` page and an `IQueryable<int>` count, and that asymmetry is the
point. A count is of **rows**, not of what is read off them, so it asks the database for a number and reads no
column at all. Nothing of the mapping is in that statement, so a DTO holding anything Mapster will not project
cannot fail a count that never needed it.

Read the number with `FirstOrDefaultAsync`. `CountAsync` compiles and answers `1`, because it counts the rows of
the count query rather than the rows counted; the [core README](../README.md) puts the three endings side by
side.

Neither query has run, exactly as `BuildPaged` promises.

## Order of operations

Filter, sort, window, **then** project. Paging is over the entity's own sort, and projecting first would leave
Mapster's output to be sorted by names the DTO may not even have, which is what lets a sort name a field the
DTO does not expose at all:

```csharp
.ApplySorts([new Sort("IsActive", SortDirection.Ascending)])   // IsActive is nowhere on MinionSummary
.ProjectTo<Minion, MinionSummary>()
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

Built and tested against **Mapster 10.0.12**, as a minimum rather than a pin. What this package uses is
`ProjectToType`, which has been stable for a long time.

## Licence

MIT, same as Weequery and same as Mapster.
