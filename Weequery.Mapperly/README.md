# Weequery.Mapperly

[Mapperly](https://mapperly.riok.app) support for [Weequery](https://github.com/rvoisin-mcl/Weequery). I decide
**which rows**. Mapperly decides **what a row looks like**, and had decided it before either of us got out of
bed, because it decides at compile time.

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

Only the columns the DTO needs ever leave the database:

```sql
SELECT "m"."Name", "m"."Alias", "m"."Pay"
FROM "Minions" AS "m"
WHERE "m"."IsActive"
```

Mapperly is MIT, so there is no licence to inherit and nothing to warn you about. If you use AutoMapper or
Mapster instead, [`Weequery.AutoMapper`](../Weequery.AutoMapper/README.md) and
[`Weequery.Mapster`](../Weequery.Mapster/README.md) do the same job the same way.

## What it adds

| | |
|---|---|
| `ProjectTo(project)` | `Build()`, then your generated projection. Returns `IQueryable<TDto>` |
| `ProjectToPaged(project)` | `BuildPaged()`, with **only the page** projected. Returns `PagedQuery<TDto>` |

**Neither type argument has to be written out**, unlike the other two companions, which I consider a small
mercy. The projection arrives as a function, so the entity comes from the Inquiry and the DTO comes from
whatever that function hands back:

```csharp
.ProjectTo(mapper.Project)                       // Mapperly
.ProjectTo<Minion, MinionSummary>(configuration) // the other two
```

## It does not reference Mapperly, and that is the point

*[steeples fingers]*

Mapperly is a source generator. It has no runtime to call. It writes the body of your projection method into your
own assembly at compile time, and by the moment I could do anything about it the mapping is already an ordinary
method on an ordinary class belonging to you. So what I take is that method, and a dependency here would buy you
nothing whatsoever except a second copy of a generator you already reference.

Two consequences, and I want both said plainly:

- **This package has no dependencies except me.** Add Mapperly to *your* project, where the generator has to run
  anyway.
- **Nothing here is Mapperly specific.** The same call will take a hand written `Select`, or anything else
  shaped like `IQueryable<T> -> IQueryable<TDto>`. It is named for Mapperly because that is the shape it was
  built to fit and what its tests exercise, not because it can tell the difference. It cannot. It does not care.

The one thing it does check is that your function returned something at all. A null coming back would otherwise
surface as a `NullReferenceException` from wherever the query was eventually enumerated, a very long way from
the method that produced it, and I have been on the receiving end of that sort of thing.

> [!WARNING]
> **Configure renames on an object mapping, not on the projection.** Mapperly refuses `[MapProperty]` on a
> queryable projection method (warning `RMG065`), and a projection left to map by name alone leaves the
> unmatched member sitting at its default. `MinionSummary.Salary` against `Minion.Pay` comes back as `0`, with a
> warning and no error:
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
> Mapperly's rule rather than mine, and I repeat it here only because the failure it produces is a zero rather
> than an exception, and a zero is a liar.

## Only the page is projected

`PagedQuery<TDto>` holds an `IQueryable<TDto>` page and an `IQueryable<int>` count, and that asymmetry is the
whole point. A count is of **rows**, not of what you read off them, so it asks the database for a number and
reads no column at all. Nothing of the mapping is anywhere in that statement, so a DTO holding something
Mapperly will not project cannot fail a count that never needed it.

Read the number with `FirstOrDefaultAsync`. `CountAsync` compiles and answers `1`, because it counts the rows of
the count query rather than the rows counted. The [core README](../README.md) puts the three endings side by
side.

Neither query has run, exactly as `BuildPaged` promised.

## Order of operations

Filter, sort, window, **then** project.

Paging is over the entity's own sort, and projecting first would leave the generated output to be sorted by
names the DTO may not even have. My order is also what lets a sort name a field the DTO does not expose at all:

```csharp
.ApplySorts([new Sort("IsActive", SortDirection.Ascending)])   // IsActive is nowhere on MinionSummary
.ProjectTo(mapper.Project)
```

The allow-list is unchanged throughout. Conditions and sorts still name **binding keys on the entity**, and a
field nobody bound is refused exactly as it is anywhere else. The DTO's property names have nothing to do with
it.

## One thing it refuses

`ApplyProjection` and a DTO are two answers to one question. One says a row is the keys the caller named. The
other says a row is the DTO. Doing both would honour the DTO and drop the projection without a word, which for a
caller whose projection arrived in a request is a filter's worth of intention quietly discarded. So I refuse:

```
ProjectTo cannot be used on an Inquiry that has already had ApplyProjection('[Name], [Pay]') called on it:
the DTO decides what a row holds here, so the projected fields would be silently ignored. Drop one of the two
```

Use `BuildProjected()` for caller-chosen columns, or a DTO for a fixed shape. Not both. Choose.

## One collision, if you use Mapster too

Mapster and Mapperly both publish a `MapperAttribute`, so a file carrying `using Mapster;` and
`using Riok.Mapperly.Abstractions;` will not compile until one `[Mapper]` is qualified. Nothing to do with either
of my packages, but it is the very first wall you walk into if you are comparing the two, and I would rather you
heard it from me.

## Versions

Built and tested against **Riok.Mapperly 4.3.1**, which is a dependency of the *tests* rather than of this
package. Any version generating an `IQueryable<TDto>` projection method will do.

## Licence

MIT, same as me and same as Mapperly.
