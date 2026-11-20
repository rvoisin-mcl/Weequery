# Weequery.Mapperly

[Mapperly](https://mapperly.riok.app) support for [Weequery](https://github.com/rvoisin-mcl/Weequery). Weequery
decides **which rows** a query returns; Mapperly decides **what a row looks like**, at compile time. The package
projects a filtered, sorted and paged Inquiry onto a DTO, and projects only the page.

## Installation

```bash
dotnet add package Weequery.Mapperly
```

Mapperly is MIT, so the package carries no licence obligation beyond its own. For AutoMapper or Mapster,
[`Weequery.AutoMapper`](../Weequery.AutoMapper/README.md) and [`Weequery.Mapster`](../Weequery.Mapster/README.md)
do the same job the same way.

**The package does not reference Mapperly.** Mapperly is a source generator with no runtime to call: it writes the
body of the projection method into the application's own assembly at compile time, so by the time this package is
involved the mapping is an ordinary method on an ordinary class. The package takes that method, and a dependency
would add only a second copy of a generator the application already references.

- **The package has no dependencies except Weequery.** Add Mapperly to the application project, where the generator
  has to run anyway.
- **Nothing in the package is Mapperly specific.** It accepts a hand-written `Select`, or any function shaped like
  `IQueryable<T> -> IQueryable<TDto>`. It is named for Mapperly because that is the shape it was built to fit and
  what its tests exercise; it cannot tell the difference.

## Example

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

## Methods

| Method | Description |
|---|---|
| `ProjectTo(project)` | `Build()`, then the generated projection. Returns `IQueryable<TDto>` |
| `ProjectToPaged(project)` | `BuildPaged()`, with **only the page** projected. Returns `PagedQuery<TDto>` |

**Neither type argument is written out**, unlike the other two companions. The projection arrives as a function, so
the entity type comes from the Inquiry and the DTO type from the function's return type:

```csharp
.ProjectTo(mapper.Project)                       // Mapperly
.ProjectTo<Minion, MinionSummary>(configuration) // the other two
```

The one check the package makes on the function is that it returned a value. A null result would otherwise surface
as a `NullReferenceException` wherever the query was eventually enumerated, far from the method that produced it.

## Mapping configuration

> [!WARNING]
> **Configure renames on an object mapping, not on the projection.** Mapperly refuses `[MapProperty]` on a
> queryable projection method (warning `RMG065`), and a projection mapped by name alone leaves an unmatched member
> at its default. `MinionSummary.Salary` against `Minion.Pay` comes back as `0`, with a warning and no error:
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
> Declaring the object mapping beside the projection carries its configuration into the projection. This is
> Mapperly's rule, not the package's.

## Paged projection

`PagedQuery<TDto>` holds an `IQueryable<TDto>` page and an `IQueryable<int>` count. The count is of **rows**, not of
what is read from them, so it asks the database for a number and reads no column. Nothing of the mapping appears in
that statement, so a DTO holding something Mapperly cannot project cannot make the count fail.

- Read the count with `FirstOrDefaultAsync`. `CountAsync` compiles and returns `1`, because it counts the rows of
  the count query rather than the rows counted. The [core README](../README.md) compares the three endings.
- Neither query has run when `ProjectToPaged` returns, as with `BuildPaged`.

## Order of operations

Filter, sort, window, **then** project.

Paging is over the entity's own sort; projecting first would leave the generated output to be sorted by names the
DTO may not have. This order also lets a sort name a field the DTO does not expose:

```csharp
.ApplySorts([new Sort("IsActive", SortDirection.Ascending)])   // IsActive is not on MinionSummary
.ProjectTo(mapper.Project)
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

## Using Mapperly alongside Mapster

Mapster and Mapperly both define a `MapperAttribute`, so a file with both `using Mapster;` and
`using Riok.Mapperly.Abstractions;` does not compile until one `[Mapper]` is qualified. The collision is between the
two mappers, not in either Weequery package, but it is the first error met when comparing them.

## Versions

Built and tested against **Riok.Mapperly 4.3.1**, a dependency of the *tests* rather than of the package. Any
version that generates an `IQueryable<TDto>` projection method works.

## Licence

MIT, as Weequery and Mapperly.
