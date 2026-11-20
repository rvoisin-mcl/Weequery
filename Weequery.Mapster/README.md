# Weequery.Mapster

Mapster support for [Weequery](https://github.com/rvoisin-mcl/Weequery). Weequery decides **which rows** a query
returns; Mapster decides **what a row looks like**. The package projects a filtered, sorted and paged Inquiry onto
a DTO, and projects only the page.

## Installation

```bash
dotnet add package Weequery.Mapster
```

Mapster is MIT and has remained MIT, so the package carries no licence obligation beyond its own. For AutoMapper or
Mapperly, [`Weequery.AutoMapper`](../Weequery.AutoMapper/README.md) and
[`Weequery.Mapperly`](../Weequery.Mapperly/README.md) do the same job the same way.

## Example

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

## Methods

| Method | Description |
|---|---|
| `ProjectTo<T, TDto>(config?)` | `Build()`, then Mapster's own `ProjectToType`. Returns `IQueryable<TDto>` |
| `ProjectToPaged<T, TDto>(config?)` | `BuildPaged()`, with **only the page** projected. Returns `PagedQuery<TDto>` |

**`ProjectToPaged` is the reason the package exists.** The unpaged case is already one line without it,
`inquiry.Build().ProjectToType<MinionSummary>()`, with a single type argument; `ProjectTo<T, TDto>` needs both,
because C# infers all of a method's type arguments or none of them. The paged case has no such shortcut and one
detail that is easy to get wrong (see [Paged projection](#paged-projection)).

## Mapping configuration

The configuration is optional, which is where this package differs from the AutoMapper companion. Mapster maps by
convention, so a DTO whose member names match the entity's needs no registration:

```csharp
.ProjectTo<Minion, MinionSummary>()                 // TypeAdapterConfig.GlobalSettings
.ProjectTo<Minion, MinionSummary>(myConfiguration)  // or one kept per scope
```

With no argument the package uses `TypeAdapterConfig.GlobalSettings`, as Mapster's own parameterless overload does.

> [!WARNING]
> A member the convention does not match is left at its default rather than reported. `MinionSummary.Salary`
> against `Minion.Pay` comes back as `0`, with no exception, until the two are mapped explicitly:
>
> ```csharp
> config.NewConfig<Minion, MinionSummary>().Map(summary => summary.Salary, minion => minion.Pay);
> ```
>
> This is Mapster's behaviour, not the package's.

## Paged projection

`PagedQuery<TDto>` holds an `IQueryable<TDto>` page and an `IQueryable<int>` count. The count is of **rows**, not of
what is read from them, so it asks the database for a number and reads no column. Nothing of the mapping appears in
that statement, so a DTO holding something Mapster cannot project cannot make the count fail.

- Read the count with `FirstOrDefaultAsync`. `CountAsync` compiles and returns `1`, because it counts the rows of
  the count query rather than the rows counted. The [core README](../README.md) compares the three endings.
- Neither query has run when `ProjectToPaged` returns, as with `BuildPaged`.

## Order of operations

Filter, sort, window, **then** project.

Paging is over the entity's own sort; projecting first would leave Mapster's output to be sorted by names the DTO
may not have. This order also lets a sort name a field the DTO does not expose:

```csharp
.ApplySorts([new Sort("IsActive", SortDirection.Ascending)])   // IsActive is not on MinionSummary
.ProjectTo<Minion, MinionSummary>()
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

Built and tested against **Mapster 10.0.14**, referenced as a minimum rather than a pin. The package uses only
`ProjectToType`, which has been stable for a long time.

## Licence

MIT, as Weequery and Mapster.
