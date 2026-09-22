# Weequery.Mapster

Mapster support for [Weequery](https://github.com/rvoisin-mcl/Weequery). I decide **which rows**. Mapster
decides **what a row looks like**. Neither of us interferes with the other, which is more than I can say for most
of my working relationships.

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

Only the columns the DTO actually needs ever leave the database. Nothing else is smuggled out:

```sql
SELECT "m"."Name", "m"."Alias", "m"."Pay"
FROM "Minions" AS "m"
WHERE "m"."IsActive"
```

Mapster is MIT and has stayed MIT, so there is no licence to inherit and nothing for me to warn you about, which
I find almost unsettling. If you use AutoMapper or Mapperly instead,
[`Weequery.AutoMapper`](../Weequery.AutoMapper/README.md) and
[`Weequery.Mapperly`](../Weequery.Mapperly/README.md) do the same job the same way.

## What it adds

| | |
|---|---|
| `ProjectTo<T, TDto>(config?)` | `Build()`, then Mapster's own `ProjectToType`. Returns `IQueryable<TDto>` |
| `ProjectToPaged<T, TDto>(config?)` | `BuildPaged()`, with **only the page** projected. Returns `PagedQuery<TDto>` |

**The paged one is the entire reason this package exists.** The unpaged case is already a one-liner without me,
`inquiry.Build().ProjectToType<MinionSummary>()`, and needs only one type argument, because C# infers all of a
method's type arguments or none of them. The paged case has no such shortcut and exactly one detail that is easy
to get wrong, which is where I come in.

## The configuration is optional

This is the one place Mapster parts company with the AutoMapper companion. Mapster maps by convention, so a DTO
whose names line up needs no registration at all:

```csharp
.ProjectTo<Minion, MinionSummary>()                 // TypeAdapterConfig.GlobalSettings
.ProjectTo<Minion, MinionSummary>(myConfiguration)  // or one you keep per scope
```

Pass nothing and you get `TypeAdapterConfig.GlobalSettings`, exactly as Mapster's own parameterless overload
does. I am not in the business of inventing behaviour.

> [!NOTE]
> Convention is marvellously convenient right up until the moment it goes quiet. A member the convention does
> not match is left at its default rather than reported, so `MinionSummary.Salary` against `Minion.Pay` comes
> back as `0` until somebody tells Mapster the two are the same thing:
>
> ```csharp
> config.NewConfig<Minion, MinionSummary>().Map(summary => summary.Salary, minion => minion.Pay);
> ```
>
> That is Mapster's behaviour and not mine, and I mention it only because the wrong answer here is a zero rather
> than an exception, and a zero will look you in the eye and say nothing.

## Only the page is projected

`PagedQuery<TDto>` holds an `IQueryable<TDto>` page and an `IQueryable<int>` count, and that asymmetry is the
whole point. A count is of **rows**, not of what you read off them, so it asks the database for a number and
reads no column whatsoever. Nothing of the mapping is anywhere in that statement, which means a DTO holding
something Mapster refuses to project cannot possibly fail a count that never needed it.

Read the number with `FirstOrDefaultAsync`. `CountAsync` compiles and answers `1`, because it counts the rows of
the count query rather than the rows counted, and it will do so with a completely straight face. The
[core README](../README.md) puts the three endings side by side.

Neither query has run. `BuildPaged` promised that, and I keep my promises.

## Order of operations

Filter, sort, window, **then** project. In that order, and not another one.

Paging is over the entity's own sort, and projecting first would leave Mapster's output to be sorted by names the
DTO may not even possess. Doing it my way is also precisely what lets a sort name a field the DTO does not expose
at all:

```csharp
.ApplySorts([new Sort("IsActive", SortDirection.Ascending)])   // IsActive is nowhere on MinionSummary
.ProjectTo<Minion, MinionSummary>()
```

The allow-list is untouched by any of this. Conditions and sorts still name **binding keys on the entity**, and a
field nobody bound is refused exactly as it is everywhere else in this operation. The DTO's property names have
nothing to do with it, and never will.

## One thing it refuses

`ApplyProjection` and a DTO are two answers to one question. One says a row is the keys the caller named. The
other says a row is the DTO. Doing both would honour the DTO and drop the projection without a word, and for a
caller whose projection arrived in a request that is a filter's worth of intention quietly binned. So I refuse:

```
ProjectTo cannot be used on an Inquiry that has already had ApplyProjection('[Name], [Pay]') called on it:
the DTO decides what a row holds here, so the projected fields would be silently ignored. Drop one of the two
```

Use `BuildProjected()` for caller-chosen columns, or a DTO for a fixed shape. Pick one. I will not do both and
neither should you.

## Versions

Built and tested against **Mapster 10.0.12**, as a minimum rather than a pin. What this uses is `ProjectToType`,
which has been stable for a very long time and shows no sign of developing ambitions.

## Licence

MIT, same as me and same as Mapster.
