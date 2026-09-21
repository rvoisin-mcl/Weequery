# Weequery.Mapperly

[Mapperly](https://mapperly.riok.app) support for [Weequery](https://github.com/rvoisin-mcl/Weequery). Weequery
decides **which rows**; Mapperly decides **what a row looks like**, and decided it at compile time.

```csharp
[Mapper]
public partial class MinionMapper
{
    [MapProperty(nameof(Minion.Pay), nameof(MinionSummary.Salary))]
    public partial MinionSummary ToSummary(Minion minion);

    public partial IQueryable<MinionSummary> Project(IQueryable<Minion> minions);
}
```

```csharp
var (page, total) = context.Minions
    .WithWeequery()
    .BindProperties(MinionBindings)
    .ApplyCondition(request.Filter)
    .ApplySorts(request.Sort, DefaultSort)
    .ApplyPagination(request.PageSize, request.Page)
    .ProjectToPaged(mapper.Project);

var matched = await total.FirstOrDefaultAsync();
var rows    = await page.ToListAsync();
```

Only the columns the DTO needs leave the database:

```sql
SELECT "m"."Name", "m"."Alias", "m"."Pay"
FROM "Minions" AS "m"
WHERE "m"."IsActive"
```

Mapperly is MIT, so there is no licence to inherit and nothing to warn about. If you use AutoMapper or Mapster
instead, [`Weequery.AutoMapper`](../Weequery.AutoMapper/README.md) and
[`Weequery.Mapster`](../Weequery.Mapster/README.md) do the same job the same way.

## What it adds

| | |
|---|---|
| `ProjectTo(project)` | `Build()`, then your generated projection. Returns `IQueryable<TDto>` |
| `ProjectToPaged(project)` | `BuildPaged()`, with **only the page** projected. Returns `PagedQuery<TDto>` |

**Neither type argument has to be written out**, unlike the other two companions. The projection arrives as a
function, so the entity comes from the Inquiry and the DTO comes from what that function returns:

```csharp
.ProjectTo(mapper.Project)                       // Mapperly
.ProjectTo<Minion, MinionSummary>(configuration) // the other two
```

## It does not reference Mapperly, and that is the point

Mapperly is a source generator. It has no runtime to call: it writes the body of your projection method into your
assembly at compile time, and by the time this package could do anything the mapping is already an ordinary
method on an ordinary class of yours. So what this takes is that method, and a dependency here would buy you
nothing but a second copy of a generator you already reference.

Two consequences, both worth stating plainly:

- **This package has no dependencies except Weequery itself.** Add Mapperly to *your* project, where the
  generator has to run anyway.
- **Nothing here is Mapperly specific.** The same call takes a hand written `Select`, or anything else shaped
  like `IQueryable<T> -> IQueryable<TDto>`. It is named for Mapperly because that is the shape it was built to
  fit and what its tests exercise, not because it can tell the difference.

The one thing it does check is that your function returned something. A null coming back would otherwise surface
as a `NullReferenceException` from wherever the query was eventually enumerated, a long way from the method that
produced it.

> [!WARNING]
> **Configure renames on an object mapping, not on the projection.** Mapperly refuses `[MapProperty]` on a
> queryable projection method (warning `RMG065`) and a projection left to map by name alone leaves the
> unmatched member at its default. `MinionSummary.Salary` against `Minion.Pay` comes back as `0`, with a warning
> and no error:
>
> ```csharp
> [Mapper]
> public partial class MinionMapper
> {
>     [MapProperty(nameof(Minion.Pay), nameof(MinionSummary.Salary))]
>     public partial MinionSummary ToSummary(Minion minion);   // declaring this is what fixes it
>
>     public partial IQueryable<MinionSummary> Project(IQueryable<Minion> minions);
> }
> ```
>
> Declaring the object mapping beside the projection is what carries the configuration into it. That is
> Mapperly's rule rather than this package's, and it is repeated here because the failure it produces is a zero
> rather than an exception.

## Only the page is projected

`PagedQuery<TDto>` holds an `IQueryable<TDto>` page and an `IQueryable<int>` count, and that asymmetry is the
point. A count is of **rows**, not of what is read off them, so it asks the database for a number and reads no
column at all. Nothing of the mapping is in that statement, so a DTO holding anything Mapperly will not project
cannot fail a count that never needed it.

Read the number with `FirstOrDefaultAsync`. `CountAsync` compiles and answers `1`, because it counts the rows of
the count query rather than the rows counted; the [core README](../README.md) puts the three endings side by
side.

Neither query has run, exactly as `BuildPaged` promises.

## Order of operations

Filter, sort, window, **then** project. Paging is over the entity's own sort, and projecting first would leave
the generated output to be sorted by names the DTO may not even have, which is what lets a sort name a field the
DTO does not expose at all:

```csharp
.ApplySorts([new Sort("IsActive", SortDirection.Ascending)])   // IsActive is nowhere on MinionSummary
.ProjectTo(mapper.Project)
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

## One collision, if you use Mapster too

Mapster and Mapperly both publish a `MapperAttribute`, so a file with `using Mapster;` and
`using Riok.Mapperly.Abstractions;` will not compile until one `[Mapper]` is qualified. Nothing to do with these
packages, but it is the first thing you hit if you are comparing the two.

## Versions

Built and tested against **Riok.Mapperly 4.3.1**, which is a dependency of the *tests* rather than of this
package. Any version generating an `IQueryable<TDto>` projection method works.

## Licence

MIT, same as Weequery and same as Mapperly.
