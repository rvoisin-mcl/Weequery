# Weequery

Weequery turns a filter that arrives as data -- from a form, a query string, a client or any other source -- into a
real `IQueryable` expression. You declare which properties may be asked about, and callers may ask about only
those: the declaration is an allow-list.

- **Targets .NET 8 and .NET 10.**
- **Works with Entity Framework Core.** Conditions become SQL, with values sent as parameters rather than
  concatenated into the statement.
- **Works in memory.** The same condition filters a `List<T>`.
- **Verified against SQLite, PostgreSQL and SQL Server.**

## Contents

- [Quick start](#quick-start)
- [Bindings](#bindings)
  - [Constant bindings](#constant-bindings)
  - [Key rules](#key-rules)
  - [Resolving bindings automatically](#resolving-bindings-automatically)
  - [Value converters](#value-converters)
  - [Ignoring unbound fields](#ignoring-unbound-fields)
- [Conditions](#conditions)
- [The query language](#the-query-language)
  - [One spelling per operator](#one-spelling-per-operator)
- [Operators](#operators)
  - [Regular expression matching](#regular-expression-matching)
- [Null semantics](#null-semantics)
- [String comparison](#string-comparison)
  - [Operators covered](#operators-covered)
  - [In memory versus in the database](#in-memory-versus-in-the-database)
- [Indexing into a collection](#indexing-into-a-collection)
  - [Missing elements](#missing-elements)
  - [Where an index can appear](#where-an-index-can-appear)
  - [Provider support for indexing](#provider-support-for-indexing)
- [Quantifiers over collections](#quantifiers-over-collections)
  - [Collections of values](#collections-of-values)
  - [Expressing joins as quantifiers](#expressing-joins-as-quantifiers)
- [Projection](#projection)
  - [Wildcards](#wildcards)
  - [Binding uses](#binding-uses)
  - [Combining broad and narrow bindings](#combining-broad-and-narrow-bindings)
- [Sorting and paging](#sorting-and-paging)
  - [Sort strings](#sort-strings)
  - [Paged results with a total](#paged-results-with-a-total)
  - [Default page size](#default-page-size)
- [Serializing conditions](#serializing-conditions)
  - [Combined query strings](#combined-query-strings)
- [Query requests](#query-requests)
- [Validation](#validation)
  - [Backend operator support](#backend-operator-support)
- [In-memory predicates](#in-memory-predicates)
- [Supported types](#supported-types)
- [Common mistakes](#common-mistakes)
- [TypeScript client](#typescript-client)
- [Projecting to a DTO](#projecting-to-a-dto)
- [Elasticsearch](#elasticsearch)
- [OData](#odata)
- [Performance](#performance)
- [Building and testing](#building-and-testing)
- [Credits](#credits)
- [License](#license)

## Quick start

```csharp
using Weequery;

var minions = context.Minions
    .WithWeequery()
    .BindResolve()
    .RemoveBinding("Name") // keep Name out of the allow-list
    .ApplyCondition("(Pay > 10000) AND (IsActive = true)")
    .ApplySort(new Sort("Pay", SortDirection.Descending))
    .ApplyPagination(pageSize: 20, page: 0)
    .Build()
    .ToList();
```

`WithWeequery()` starts the query. `Build()` returns an `IQueryable<T>` with everything applied. Nothing executes
until the result is enumerated, so it composes with any further LINQ.

In a controller, the condition comes from the caller. This is the intended use; the bindings decide what the caller
can reach:

```csharp
[HttpPost("search")]
public async Task<IActionResult> Search([FromBody] SearchRequest request)
{
    var query = _context.Minions
        .WithWeequery()
        .BindProperties(MinionBindings)
        .ApplyCondition(request.Filter?.Unpack())     // a TransportCondition off the wire
        .ApplySorts(request.Sorts)
        .ApplyPagination(request.PageSize, request.Page)
        .Build();

    return Ok(await query.ToListAsync());
}
```

`SearchRequest` here is an application type. Weequery also provides a request type that carries all four parts of a
query and binds directly from a query string; see [the whole request, in one object](#query-requests).

## Bindings

A **binding** maps a **key** -- the name callers use -- to a property on the entity. Only bound properties can be
filtered or sorted on. Naming anything unbound throws `WeequeryException`.

```csharp
.BindProperty(minion => minion.Name)              // key defaults to "Name"
.BindProperty(minion => minion.Pay, "Salary")     // key is "Salary"
.BindProperty("Lair.Capacity")                    // a path, given as a string; key defaults to the path
```

Paths may go deeper, and may reach into a `Nullable<>`:

```csharp
.BindProperty("HireDate.Year", "HireYear")        // DateTime.Year
.BindProperty("BirthDate.Year", "BirthYear")      // DateTime?.Year
```

A selector can name such a path by going as far as the compiler allows and passing the rest as segments:

```csharp
.BindProperty(minion => minion.BirthDate, ["Year"], "BirthYear")   // same as "BirthDate.Year"
.BindProperty(minion => minion.Lair, ["Capacity"])                 // key is "Capacity"
```

> [!IMPORTANT]
> Given segments, the key defaults to the **last segment**; given the path as a string, it defaults to the **whole
> path**. Both are legal keys; the segments form keeps its original default so existing keys do not change. Name
> the key explicitly to get the other.

Segments exist because `minion => minion.BirthDate.Year` does not compile against a `DateTime?`, and
`minion.BirthDate!.Value.Year` means something different: naming `Value` unwraps the nullable, producing a plain
`int` with no null of its own, so `IsNull` no longer applies. Segments reach through the nullable instead.

### Constant bindings

A binding can stand for a constant supplied by your code, under a name the caller may refer to but cannot set:

```csharp
.BindConstant("Threshold", payThreshold)
.ApplyCondition("Pay > [Threshold]")        // the caller names it; the application supplies the value
```

The caller writes the shape of the question; the application supplies values the caller must not see, such as a
per-tenant limit, a cutoff date or the current date.

- The value reaches the database as a **parameter**, so the statement is identical whatever the value.
- The value is the same for every row, so sorting on a constant is refused.

For a fixed set of bindings, declare it once and reuse it:

```csharp
static readonly BindingRequest[] MinionBindings =
[
    new(nameof(Minion.Name), null),                 // null key means "use the path"
    new(nameof(Minion.Pay), "Salary"),
    new(["Lair", "Capacity"], "LairCapacity"),      // segments, key defaults to the last one
];

query.WithWeequery().BindProperties(MinionBindings);
```

`BindProperties` resolves a given set once per process and caches it, so calling it on every request costs a copy
rather than a property lookup.

### Key rules

A key is **one or more valid unquoted SQL names, separated by periods**. A name is an ASCII letter or underscore,
followed by letters, digits or underscores. The period lets a nested property be bound under its existing path.

```csharp
WeequeryException.IsBindingKey("Total_2");        // true
WeequeryException.IsBindingKey("Lair.Capacity");  // true
WeequeryException.IsBindingKey("my field");       // false
WeequeryException.IsBindingKey("Lair..Capacity"); // false, an empty segment is not a name
WeequeryException.IsBindingKey(".Capacity");      // false, nor is a missing one
```

Every segment must satisfy the whole rule, so a leading or trailing period, two periods in a row, and a segment
starting with a digit are all refused. `IsBindingKey` is the check applied to every key.

A nested property therefore needs no second name:

```csharp
.BindProperty("Lair.Capacity")                    // key is "Lair.Capacity"
.BindProperty("Lair.Capacity", "LairCapacity")    // or expose a different name
```

A period is not a delimiter in the query language, so a dotted key is a single token and is read and written
unquoted: `Lair.Capacity > 500` parses, and is written back as `([Lair.Capacity] > '500')`.

- **Keys are case-insensitive.** `pay`, `Pay` and `PAY` all find a property bound as `Pay`. Two keys differing only
  in case are the same key; binding both is a duplicate, refused when the second is created.
- **Keys may not be reserved words.** A key is written as a bare field name, so a key such as `Contains` would make
  a query ambiguous. Binding one throws:

  ```csharp
  .BindProperty(minion => minion.Notes, "Contains")    // throws
  ```

- **Only the whole key is checked.** An operator is only ever a whole word, so `Lair.And` is a single token, is not
  the conjunction, and binds; `And` alone does not.

The reserved words, matched without regard to case:

| Group | Words |
|---|---|
| Operator names | `IsNull`, `IsNotNull`, `IsIn`, `IsNotIn`, `IsBetween`, `IsNotBetween`, `StartsWith`, `DoesNotStartWith`, `EndsWith`, `DoesNotEndWith`, `Contains`, `DoesNotContain`, `IsMatch`, `DoesNotMatch` |
| SQL operator words | `AND`, `OR`, `NOT`, `IS`, `IN`, `BETWEEN`, `NULL` |
| Combined-string separators | `OrderBy`, which introduces a [sort clause](#sorting-and-paging); `Select`, which introduces a [projection](#combined-query-strings) |

### Resolving bindings automatically

**`BindResolve()`** walks the entity type and binds every readable property it finds, each keyed by its own name.
Use it for large models where listing every property by hand is impractical.

```csharp
context.Minions
    .WithWeequery()
    .BindResolve()                       // every property on Minion, keyed by its own name
    .ApplyCondition(request.Filter)
```

> [!WARNING]
> `BindResolve()` inverts the allow-list: every property name on the entity becomes readable and filterable by
> callers. A property such as `PasswordHash`, `InternalRiskScore` or `TenantID` is published along with the rest.
> This is reasonable for an internal tool over a model you own, and not for an entity carrying sensitive or audited
> data. Resolve the bindings once, print them and review the result before shipping:
>
> ```csharp
> foreach (var request in Inquiry<Minion>.ResolveBindables())
> {
>     Console.WriteLine($"{request.Key} -> {request.PropertyPath}");
> }
> ```

**Depth.** By default the walk goes one level down: the entity's own properties and the properties of those, keyed
by their dotted paths. `BindResolve(maxDepth: 0)` stops at the entity. The maximum depth is 16.

```csharp
.BindResolve(maxDepth: 0)      // Name, Pay, Lair
.BindResolve()                 // ...and Lair.Name, Lair.Capacity
.BindResolve(maxDepth: 3)      // as far as you like, up to 16
```

**Collections.** The walk descends into collections, so quantifiers work without explicit declarations. Every
collection of objects is bound twice under one key: as a property, which an [index](#indexing-into-a-collection)
reads a single element from, and as a collection, which answers
[`Any`, `All` and `None`](#quantifiers-over-collections).

```csharp
.BindResolve(maxDepth: 0)      // Assignments Any (LairID = 5)
.BindResolve()                 // ...and Assignments Any (Lair.Name = 'Volcano')
```

**One depth budget covers collections too.** The entity's own collections are entered free, so depth 0 still
reaches them and binds the element's own properties -- on a link table, the pair of ids and the two entities they
point at. Depth 1 reaches through to the far side. That second hop is expensive: on a model with three collections
it raised the allow-list from 18 nameable keys to 124, because the far side of a link table is a whole other
entity. Spend depth only where the second hop is wanted, and [list the result](#listing-bindings).

**The free entry applies only at the root.** A collection further down costs the level taken to reach it, so an
element found at depth `d` is walked to `maxDepth - d - 1`, and a collection the budget does not reach is not bound:

```csharp
// Yard -> Bay -> Cartons -> Carton -> Stamp
.BindResolve(maxDepth: 1)      // nothing: depth 1 reaches Bay, leaving none to enter Cartons
.BindResolve(maxDepth: 2)      // Bay.Cartons Any (Code = 'X'), and no further
.BindResolve(maxDepth: 3)      // ...and Bay.Cartons Any (Stamp.Mark = 'X')
```

A free entry at every level would compound, and a walk of 1 would include a second hop nobody requested.

Other rules of the walk:

- **Collections of values.** A `List<string>` or an array of numbers is entered too, with the element named `Value`:
  `Tags Any (Value = 'urgent')`; see [A collection of values](#collections-of-values). A `Dictionary<,>`, a
  `byte[]` and a `string` remain ordinary bindings; [index them](#indexing-into-a-collection) instead.
- **Emptiness.** To ask whether an entity has no elements, use `Assignments None (...)` rather than a null test. A
  quantifier is total: it answers the same for an absent collection as for an empty one, a distinction databases
  do not make anyway.
- **No descent.** To get the property list without entering collections (the behaviour before descent was added),
  bind `ResolveBindables` directly:

  ```csharp
  .BindProperties(Inquiry<Minion>.ResolveBindables())
  ```

- **Self-references terminate.** A type already open on the current path is bound but not descended into, so
  `Parent` is a key and `Parent.Parent` is not. Without this rule, a `Node` with two self-references resolved half a
  million keys at depth 16, taking nine seconds and nearly two gigabytes per request.
- **Reserved words get an underscore.** A property named like a keyword, such as `Contains`, is keyed `Contains_`
  rather than refusing the whole model. The path is unchanged; only the key differs. Nested keys are already
  unambiguous, so `Inner.Contains` is left alone.

#### Removing bindings

There are two ways to exclude bindings: subtract them afterwards with `RemoveBinding`, or exclude them before
resolution with `BindingResolutionSettings`. The second is usually preferable.

`RemoveBinding` suits a model where the exceptions are fewer than the rules:

```csharp
.BindResolve()
.RemoveBinding("PasswordHash")
.RemoveBinding("Lair.SelfDestructCode")
```

<a name="and-taking-one-back-off"></a>
**It can remove a single use** as well as a whole binding; it is the only API in the library that revokes a use:

```csharp
.BindResolve()
.RemoveBinding("Pay", BindingUse.Test | BindingUse.Sort)   // still readable, no longer testable
.RemoveBinding("PasswordHash")                                  // gone entirely
```

- A binding left with no uses is removed entirely.
- Removing a use the binding never had changes nothing.
- A bound collection has no use of its own (it answers a quantifier, which is a condition), so a removal including
  `Test` removes it and one without `Test` leaves it.

**It accepts the same wildcards as a projection**, matched by the same code, so `Lair.*` cannot mean one thing when
[listing](#listing-bindings) and another when removing. A trailing `.*` matches everything under a branch but not
the branch itself:

```csharp
.RemoveBinding("Lair.*")     // Lair.Name, Lair.Capacity; Lair itself stays, still testable for null
.RemoveBinding("*")          // all of it, collections included
```

The period is significant: `"Lair.*"` does not match a key called `Lairyard`.

**It reaches inside a collection**, using the spelling `ListBindings` reports. This is the only way to remove
element keys after the fact, since they are not keys of the query itself:

```csharp
.RemoveBinding("Assignments[].*")            // nothing may be asked about an element any more
.RemoveBinding("Assignments[].Minion.*")     // the far side of the link table, and no more than that
.RemoveBinding("Assignments[].Lair.Name")    // just the one, no wildcard needed
```

Emptying a collection's inner allow-list removes the collection binding too, since no condition could satisfy an
empty one. The property binding is untouched either way, so the key can still be
[indexed](#indexing-into-a-collection) and tested for null; only the quantifier goes. This is the after-the-fact
equivalent of `IgnorePaths = ["Assignments[]."]`, which is usually preferable.

A wildcard that matches nothing is not an error in `RemoveBinding`, unlike in a projection: an empty projection
would silently return nothing, whereas an empty subtraction has done what it was asked.

**`BindingResolutionSettings` excludes paths before resolution**, so they are never bound in the first place. Start
from `Default` with a `with` expression; otherwise the rules it already carries are dropped:

```csharp
var settings = BindingResolutionSettings.Default with
{
    IgnorePaths = ["PasswordHash", "Audit."],
    IgnoreTypes = [typeof(byte[])],
};

query.WithWeequery().BindResolve(maxDepth: 2, settings);
```

| Setting | Description |
|---|---|
| `IgnorePaths` | Whole paths to leave out, matched without regard to case. `"Lair"` drops the property **and** everything under it; **`"Lair."`**, with the period, binds `Lair` itself and stops there, so it can still be tested for null |
| `IgnoreTypes` | Leaves out any property of these types, matched on the declared type, so a `Nullable<>` is its own type |
| `IgnoreTypeWhenAssignable` | Makes `IgnoreTypes` catch anything assignable to one of them, so a base class or interface covers everything derived from it |
| `DoNotExpandTypes` | Binds a property of this type without descending into it, so the property is reachable and testable for null without exposing its members |
| `IgnoreAttributes` | Leaves out any property carrying one of these attributes and everything under it, any property whose type carries one, and any collection whose element type carries one. `Default` holds `NotMappedAttribute`, so properties the model marks as not mapped stay off the allow-list without being listed in `IgnorePaths` |

**To exclude a path inside a collection, name the collection first.** An element is walked from its own root, so a
path inside it is identified by its collection, using the spelling [`ListBindings`](#listing-bindings) reports:

```csharp
IgnorePaths =
[
    "AssignmentsAsMember[].",                 // bind the collection, resolve nothing inside it
    "AssignmentsAsLeader[].Member",           // leave Member out of this collection's elements
    "LairAssignments[].Minion.",              // bind Minion there, and stop at it
    "LairAssignments",                        // take the name away altogether
]
```

- `"AssignmentsAsMember[]."` leaves the property binding in place, so the key can still be
  [null tested](#null-semantics) and [indexed](#indexing-into-a-collection); only the quantifier goes. This is the
  same distinction the trailing period draws elsewhere.
- `"AssignmentsAsMember."` alone does nothing: a collection is never expanded as a navigation, so there is nothing
  for it to stop.
- A bare path such as `"Minion"` is matched against the element's own paths, so it applies inside **every**
  collection that has one. Both spellings subtract, so naming both removes both.

`Default` excludes one thing: anything marked `[NotMapped]`, which the model declares is not a column and which a
provider could not translate. Assigning `IgnoreAttributes` replaces that default rather than adding to it, so spread
the default in to keep it:

```csharp
BindingResolutionSettings.Default with { IgnoreAttributes = [.. BindingResolutionSettings.Default.IgnoreAttributes, typeof(SecretAttribute)] }
```

Beyond the settings, the walk applies fixed rules that cannot be turned off.

**Where the walk stops.** Bind these by hand if needed:

- **Structs.** `HireDate` is a key; `HireDate.Year` is not, because `DateTime` is not a class. The same applies to
  enums and to the `Nullable<>` of either. Use [segments](#bindings) to reach in.
- **Containers.** An array, a `List<>`, a `Dictionary<,>` and a `string` are bound but never entered. Their outer
  members (`Length`, `Rank`, `SyncRoot`, `Count`, `Capacity`) are bookkeeping; a `byte[]` previously resolved seven
  keys this way. Elements are reached by [indexing](#indexing-into-a-collection), not by resolving each element.
- **Indexers.** `Chars` on a string and `Item` on a list have no bindable path, so they are skipped rather than
  failing at bind time.

**What the walk skips without stopping:**

- **Structs with no builder.** A money type, a coordinate or a strongly typed ID has no comparison and no null to
  test, so the property is skipped. Previously a single `Money` property caused the whole model to be refused. An
  unsupported **reference** type is different: it is bound as something that can be tested for null.

**What the walk follows:**

- **Interfaces.** Members an interface promises are reachable through it, so `Place.Capacity` is resolved, as is
  `Place.DisplayName` where `IPlace` inherits it from `INamed`. Previously a model navigating by interface resolved
  nothing below it.

> [!NOTE]
> `BindResolve` **adds** to existing bindings rather than replacing them, so a key bound by hand and then resolved
> again is a duplicate and is refused. Resolve first, remove afterwards.

#### Listing bindings

`ListBindings` reads the allow-list without changing it. Each entry gives a key, the path behind it and the uses it
allows. Use it to review what `BindResolve` produced.

The result is one flat list. A property of the entity and a property of an element of one of its collections are
both entries; the second is spelled with the collection it is reached through:

```csharp
foreach (var bound in query.WithWeequery().BindResolve().ListBindings())
{
    Console.WriteLine(bound);
}

// 'Alias' may be used for all
// 'LairAssignments', a collection, may be used for all
// 'LairAssignments[].Lair.Name', written 'Lair.Name' inside LairAssignments, may be used for test
// 'LairAssignments[].Minion.Pay', written 'Minion.Pay' inside LairAssignments, may be used for test
// 'Pay', which is Salary, may be used for test, projection
```

The empty brackets mark where an element is chosen: the entry is the [path you would bind by hand](#bindings) with
the choice left out. `LairAssignments[0].Lair.Name` binds; `LairAssignments[].Lair.Name` is the same path with no
element picked. Written as a condition as it stands, the parser reports the missing index:

```
Expected an index for field 'LairAssignments' at position 15
```

**A listed key is a name for the binding, not a condition.** A condition takes one of two other shapes, and the
entry provides what each needs: `ElementKey` is what to write inside a quantifier, and `ElementOf` is the collection
the quantifier names.

```csharp
// LairAssignments[].Lair.Name          Key          what the listing calls it
// LairAssignments                      ElementOf    the collection it is reached through
// Lair.Name                            ElementKey   what you write inside Any, All or None

.ApplyCondition("LairAssignments Any (Lair.Name = 'Volcano')")     // quantify over all of them
.ApplyCondition("LairAssignments[0] IsNotNull")                    // or name one element
```

A dotted path after an index is valid in a binding but not in a condition. To ask about one element's far side,
bind it under its own key and ask about that:

```csharp
.BindProperty("LairAssignments[0].Lair.Name", "FirstLair")
.ApplyCondition("FirstLair = 'Volcano'")
```

`ListBindings` is the only way to inspect the element half of resolved bindings, which is usually the larger half
because a link table's element brings in a whole other entity. The example model lists **18** keys on the entity
and **106** more behind three of its collections; `LairAssignments[].Minion.Pay` is nameable without anyone having
asked for it.

- A [collection](#quantifiers-over-collections) appears once, although it is bound twice under the one name: the
  listing reports what a caller may write, not the internal representation.
- Every element entry reports `Test`, the only use an element has. No element entry is itself a collection; an
  inner set cannot bind a further one.
- `IsCollection` is the only field that distinguishes a collection. A quantifier is a condition, so
  `LairAssignments` and `Name` both grant `Test` and their uses do not separate them.
- The listing is a report, not a request: it describes the allow-list as built, including parts no request could
  have produced.

### Value converters

A comparison has two sides from different places: the **client** value the caller wrote and the **source** value
the row holds. A **converter** normalises one side, the other or both, so values spelled differently compare equal:

```csharp
.BindProperty(minion => minion.Alias, "Alias",
    convert: ValueConverter.For<string>(alias => alias.ToUpper()))
```
```
Alias = 'ghost'      finds the minion stored as "Ghost"
Alias = 'GHOST'      so does this
```

`ConversionTarget` selects the sides; the default is `Both`:

| Value | Converts | Use for |
|---|---|---|
| `Client` | what the caller wrote | a column already stored normalised; convert the input to match it |
| `Source` | what the row holds | a column callers write patterns against, where converting their text would break the pattern |
| `Both` | both | neither side is normalised. The usual choice |

**The converter becomes part of the query**, which is why it takes an expression rather than a delegate:

```sql
WHERE "m"."Alias" IS NOT NULL AND upper("m"."Alias") = @Value
-- @Value = 'GHOST'
```

The client side converted the parameter once while the query was built; the source side is `upper(...)` in the
SQL. A provider can translate `alias => alias.ToUpper()` but not a call into application code; such a call fails
when the query runs, not when the binding is made.

> [!NOTE]
> A converted column is an expression, so an index on the column is not used unless the database has an index on
> that expression. Converting a million rows to compare against one value is slow; normalising the stored data once
> is fast. `Client` alone costs nothing.

Converters are not applied to:

- **Null tests.** `IsNull` and `IsNotNull` ask whether a value exists, which no conversion changes; the null guard
  every other operator carries also reads the raw property. A converter cannot make a null look present.
- **Sorting.** Order follows the stored data.
- **Projections.** A projected row returns what is stored.

Rules:

- The converter is declared for the **unwrapped** type, so a `DateTime?` property takes
  `ValueConverter.For<DateTime>`; the null guard has already established that a value exists.
- A converter whose type does not match the property is refused where it is declared, not when a query fails.
- Every value an operator holds is converted: both ends of an `IsBetween`, every entry of an `IsIn`. Comparing two
  properties converts each with its own converter.
- `BindConstant` also accepts a converter.

> [!WARNING]
> A converter on a string binding also converts the pattern of an `IsMatch`, and upper-casing a regular expression
> changes what its character classes mean. Use `Source` on bindings callers write patterns against.

### Ignoring unbound fields

By default, a query naming an unbound field is refused in full. `IgnoreUnboundFields` drops those parts and runs
the rest:

```csharp
context.Minions.WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = true })
    .BindProperties(MinionBindings)
    .ApplyCondition("IsActive = true AND Gizmo = 3")   // Gizmo is dropped; this filters on IsActive alone
```

The setting exists for **stale saved filters**: a caller saved a query, a binding was later renamed or removed, and
refusing the query would prevent the caller from opening the saved view, even to fix it.

It is available only as a setting, fixed where the query starts. No call later in the chain changes it, because
leniency applies to the whole query and a change partway through would leave part of the chain built without it.

> [!WARNING]
> **Dropping always widens the result.** A missing test does not constrain, so a condition made entirely of unbound
> fields prunes to nothing, and a query with no condition **returns every row**:
>
> ```csharp
> WithWeequery(lenient).BindProperties(MinionBindings).ApplyCondition("Gizmo = 3")   // every row, silently
> ```
>
> This is why the setting is off by default. If some rows must not be visible to the caller, apply that constraint
> to the `IQueryable` before passing it to Weequery, or bind it as a constant and AND it in yourself. Never rely on
> a filter string that may be ignored.

The effect differs by part of the query:

| Part | Effect of a dropped field |
|---|---|
| **Sort** | Rows return in a different order. Harmless |
| **Projection** | A key is missing from the row. A projection whose fields were *all* dropped returns rows with no columns, not all columns |
| **Condition** | **Different rows.** The case that needs care |

Inside a quantifier, the collection's own allow-list applies: an unbound field inside the brackets is dropped from
the inner condition, a quantifier left with no test is dropped entirely, and so is one naming an unbound collection.

Not dropped, lenient or not:

- **A field bound without the requested use.** See [What a binding is for](#binding-uses): a use
  restriction is a deliberate statement about what a caller may do, and ignoring it would defeat it.
- **Any other error:** malformed text, an operator that does not fit the property, a value that does not parse.

Dropped regardless of the setting: a sort naming a field that is bound but has nothing to order by -- a constant,
or a type with no comparison. Applying it could not change the order, so dropping it returns the same rows. It is
recorded like any other drop, with a reason identifying the case.

**`DroppedFields`** reports what the last build removed, so a saved view can be repaired:

```csharp
var rows = inquiry.Build().ToList();

foreach (var dropped in inquiry.DroppedFields)
{
    // "'Gizmo' dropped from the condition, nothing bound it"
    Warn($"{dropped.Field} no longer applies and has been removed from your saved view");
}
```

- Each entry carries the key and the part of the query that lost it, as a `BindingUse`: `Condition`, `Sort` or
  `Projection`.
- A key missing from two parts is two entries; a key missing twice from one part is one entry.
- It is filled in **when the query is built**, not when it is enumerated, so it is ready as soon as `Build()`
  returns. Each build resets it, so it describes the current query rather than accumulating.
- The name is the key with any index stripped: `Tallies[apples]` and `Tallies[pears]` both report `Tallies`, once,
  because one binding is missing.
- Inside a quantifier the name is the element key, scoped to the collection's own allow-list:
  `Assignments Any (Loot = 3)` reports `Loot`.

## Conditions

A **condition** is a small tree. Build one in code:

```csharp
ICondition condition = new ConjunctionCondition(Operator.And,
[
    new OneValueCondition<decimal>(Operator.GreaterThan, "Salary", 10000m),
    new NotCondition(Operator.Not,
        new OneValueCondition<string>(Operator.Contains, "Name", "temp")),
    new NoValueCondition(Operator.IsNull, "FireDate"),
]);
```

There is one type per **shape**, and the operator determines which:

| Type | Operators |
| --- | --- |
| `NoValueCondition` | `IsNull`, `IsNotNull` |
| `OneValueCondition<T>` | `=`, `<>`, `<`, `<=`, `>`, `>=`, the substring family, `IsMatch`, `DoesNotMatch` |
| `TwoValueCondition<T>` | `IsBetween`, `IsNotBetween` |
| `MultipleValueCondition<T>` | `IsIn`, `IsNotIn` (up to 1000 values) |

A constructed condition therefore always holds the right number of values for its operator. An operator that does
not belong to the type is refused at construction, not when the query runs. Use
`ConditionFunctions.BuildComparison` when the operator is not known until run time.

To compare a property against **another bound property**, pass a binding key as the operand instead of a value:

```csharp
ICondition condition = new OneValueCondition<string>(
    Operator.LessThan, "HireDate", ConditionValue.Binding("FireDate"));
```

- Any operand of any shape can be a binding key, and keys mix with values: `Pay IsIn (8000, [Cap])` is a list
  holding a value and a property.
- A key is a name whatever the property holds, so only a condition over `string` can carry one.
- Both sides must be bound, so the comparison exposes nothing beyond what the allow-list already permits.

Fluent helpers build the same tree:

```csharp
var conjunction = new ConjunctionCondition(Operator.And, []);

conjunction
    .AddIsGreaterThanTest("Salary", 10000m)
    .AddDoesNotContainTest("Name", "temp")
    .AddIsNullTest("FireDate");
```

Every helper that takes a value also takes a `ValueSource`, defaulting to `Raw`, so naming a property takes one
more argument:

```csharp
conjunction
    .AddIsLessThanTest("HireDate", "FireDate", ValueSource.Binding)
    .AddIsBetweenTest("Salary", "10000", ValueSource.Raw, "Ceiling", ValueSource.Binding)
    .AddIsInTest("Name", [ConditionValue.Raw("Scott"), ConditionValue.Binding("Alias")]);
```

## The query language

`ApplyCondition(string)` parses a compact text form of a condition, which is easier to send from a console than a
tree.

```
(Pay > 10000) AND (IsActive = true)
Name StartsWith 'Al'
Pay IsBetween (8000, 12000)
Alias IsNull
Pay > [Threshold]                     brackets name a bound property
Pay IsIn (8000, [Ceiling])            and they mix
```

`ToQuery()` is the inverse: it writes a condition back out as text.

- **Precedence.** `AND` binds tighter than `OR`, as in SQL and C#. Parentheses group freely.
- **Case.** Keywords, operator names and field names are case-insensitive: `AND`, `and` and `And` are the same
  word.
- **Alternate spellings.** `==`, `!=`, `IN` and `BETWEEN` are accepted and will remain so. `&&`, `||`, `!`,
  `IS NULL`, `IS NOT NULL`, `NOT IN` and `NOT BETWEEN` are also accepted, but are deprecated; see
  [One spelling per operator](#one-spelling-per-operator).
- **No `LIKE`.** `LIKE` is not supported; use `Contains`, `StartsWith` or `EndsWith`.
- **Quoting.** Quote a value that contains a space or a delimiter, or that looks like a keyword. Use `'single'`
  or `"double"` quotes; a literal closes on the quote that opened it, so the other kind needs no escaping:
  `"it's"` and `'say "hi"'` both read as written. A backslash escapes a quote or another backslash and nothing
  else, so `'\w'` is the two characters it looks like. `''` and `""` are the empty string.
- **Unquoted values.** Simple values need no quotes: numbers, `true`, enum names, GUIDs, ISO dates and times.
- **Culture.** Values are always parsed with the invariant culture, so a query means the same on every machine.

### One spelling per operator

Earlier versions accepted every common spelling of an operator (`&&` and `AND`, `IsNull` and `IS NULL`, `ORDER BY`
and `OrderBy`). As a result, the same question could be written several ways and the written output depended on
the style chosen, so queries could not be compared, cached by text, or read at a glance. The **Native** style
settles on one spelling for each operator:

```csharp
condition.ToQuery(QueryStyle.Native);      // "(([Pay] > '10000') AND ([IsActive] = 'true'))"
```

The rule is that **an operator is one word**. The rest follows from it:

- **Conjunctions** are `AND`, `OR`, `NOT`. The punctuation forms `&&`, `||` and `!` are refused.
- **No operator contains a space.** `IS NULL`, `IS NOT NULL`, `NOT IN` and `NOT BETWEEN` are refused; write
  `IsNull`, `IsNotNull`, `IsNotIn`, `IsNotBetween`.
- **A range is a list.** Write `IsBetween (1, 5)`, never `IsBetween 1 AND 5`, where `AND` would be a separator in
  one place and a conjunction in another.
- **Sorts use `OrderBy`, not `ORDER BY`**, where a condition and a sort clause travel as
  [one string](#combined-query-strings).
- **One-word alternates still read.** `IN` and `BETWEEN` are read as `IsIn` and `IsBetween`; `==` is read as `=`,
  and `!=` as `<>`. Output is always the canonical form: `=`, `<>`, `IsIn`, `IsBetween`.
- **Case still does not matter.** `isnull` and `IsNull` are the same word.

`QueryStyle.Native` is the default wherever text is written, so `ToQuery()` and `ToString()` produce it without
being asked. `QueryStyle.CSharp` and `QueryStyle.Sql` are marked `[Obsolete]`; they still write what they always
wrote, and the parser still reads everything it always read.

**Strict reading is opt-in.** The parser stays permissive by default, so that saved filters using older spellings
keep working across a minor version. Pass the style to get the strict grammar:

```csharp
.ApplyCondition(request.Filter, QueryStyle.Native)          // refuses && and IS NULL
.ApplySorts(request.Sort, DefaultSort, QueryStyle.Native)   // refuses ORDER BY
ConditionFunctions.ParseQuery(text, QueryStyle.Native);
ParsedQuery.Parse(text, defaultSort, QueryStyle.Native);
Sort.Parse(clause, defaultSort, QueryStyle.Native);
transport.Unpack(QueryStyle.Native);
```

> [!NOTE]
> [`QueryRequest`](#query-requests) is the exception: it always reads Native and only Native,
> and takes no style argument. Text arriving from outside is not a filter saved before the change, so a caller can
> be held to the one grammar. The permissive default exists for saved filters held by the application.

Every refusal names the replacement:

```
'&&' at position 9 is not valid in the Native style, write 'AND'
'IS NULL' is not valid in the Native style, write 'IsNull'
'ORDER BY' at position 11 is not valid in the Native style, write 'OrderBy'
```

## Operators

In the table below, **Written** is the form always produced on output. **Also read** lists other one-word forms
accepted under `QueryStyle.Native`. **Refused by Native** lists multi-word forms that only the permissive parser
accepts. See [One spelling per operator](#one-spelling-per-operator).

| Operator | Written | Also read | Refused by Native | Values | Notes |
|---|---|---|---|---|---|
| `IsNull` | `IsNull` | `= null` | `IS NULL` | 0 | needs a nullable property |
| `IsNotNull` | `IsNotNull` | `<> null` | `IS NOT NULL` | 0 | needs a nullable property |
| `Equals` | `=` | `==` | | 1 | |
| `NotEqual` | `<>` | `!=` | | 1 | |
| `LessThan` | `<` | | | 1 | strings order too |
| `LessThanOrEqual` | `<=` | | | 1 | |
| `GreaterThan` | `>` | | | 1 | |
| `GreaterThanOrEqual` | `>=` | | | 1 | |
| `IsBetween` | `IsBetween (a, b)` | `BETWEEN (a, b)` | `IsBetween a AND b` | 2 | inclusive of both ends |
| `IsNotBetween` | `IsNotBetween (a, b)` | | `NOT BETWEEN` | 2 | |
| `IsIn` | `IsIn` | `IN` | | 0-1000 | an empty list matches nothing |
| `IsNotIn` | `IsNotIn` | | `NOT IN` | 0-1000 | |
| `StartsWith` | `StartsWith` | | | 1 | strings only |
| `DoesNotStartWith` | `DoesNotStartWith` | | | 1 | strings only |
| `EndsWith` | `EndsWith` | | | 1 | strings only |
| `DoesNotEndWith` | `DoesNotEndWith` | | | 1 | strings only |
| `Contains` | `Contains` | | | 1 | strings only |
| `DoesNotContain` | `DoesNotContain` | | | 1 | strings only |
| `IsMatch` | `IsMatch` | | | 1 | strings only, **not on every provider** |
| `DoesNotMatch` | `DoesNotMatch` | | | 1 | strings only, **not on every provider** |
| `Any` | `Any (...)` | | | | a bound collection, [quantified](#quantifiers-over-collections) |
| `All` | `All (...)` | | | | true of an empty collection |
| `None` | `None (...)` | | | | true of an empty collection |
| `And` | `AND` | | `&&` | | `ConjunctionCondition` |
| `Or` | `OR` | | `\|\|` | | `ConjunctionCondition` |
| `Not` | `NOT` | | `!` | | `NotCondition` |

A `bool` accepts only the null tests, equality and the `IsIn` family. Ordering operators on a `bool` are refused
when the condition is built, rather than failing later with an error about `Boolean` having no comparison
operator.

### Regular expression matching

`IsMatch` and `DoesNotMatch` match a string against a regular expression. They are the only two operators that do
not work on every provider, because standard SQL has no regular expression and each provider implements its own.

| Where | Behaviour |
|---|---|
| In memory | .NET's own `Regex`, bounded by `Inquiry<T>.MatchTimeout` |
| SQLite | `REGEXP`, which Microsoft.Data.Sqlite implements with .NET's `Regex`, so it agrees with memory |
| PostgreSQL | the `~` operator, which is POSIX ARE, **not** .NET |
| SQL Server | **not supported.** The query fails when it is built |

```csharp
.ApplyCondition(@"Name IsMatch '^A\w+ Fox$'")
.ApplyCondition("Alias DoesNotMatch '^Gh'")
.ApplyCondition("Name IsMatch [Alias]")        // the pattern can come from the row
```

Write the pattern as usual: `'\w'`, `'\d+'` and `'\p{Lu}'` all mean what they look like.

- **PostgreSQL uses a different dialect.** Lookarounds, lazy quantifiers and named groups are .NET features, not
  POSIX ones, so a pattern using them can match different rows there than in memory. Keep to the common subset
  where the same condition must give the same answer on both.
- **Match timeout.** Where Weequery runs the match, it is bounded; the default is one second.
  `Inquiry<T>.MatchTimeout` is a static on the generic type, so it is set **per entity type**. Exceeding it raises
  `RegexMatchTimeoutException` from wherever the query is being enumerated.
- **The bound applies in memory only.** Translated to SQL, the pattern runs under the database's own limits. The
  `Regex.IsMatch` overload that takes a timeout is not translatable by any provider, so using it would turn the
  query into a table scan on the client.

```csharp
Inquiry<Minion>.MatchTimeout = TimeSpan.FromMilliseconds(250);   // Regex.InfiniteMatchTimeout removes the bound
```

> [!WARNING]
> A pattern is caller input, and a regular expression can cost far more than it appears to. Matching `(a+)+$`
> against a few dozen non-matching characters takes time exponential in their number. Every other operator is
> bounded by the size of the data; these two let a caller turn a search box into a denial of service using only
> punctuation. Keep `MatchTimeout` bounded wherever patterns come from outside.

`DoesNotMatch` is a **negative operator**, not a negation. A row with no value matches neither `DoesNotMatch` nor
`IsMatch`. `!(Alias IsMatch '^G')` is a different question and also returns the null rows; see
[Null semantics](#null-semantics).

## Null semantics

**A null satisfies nothing except `IsNull`.**

Every other operator is built as "the property has a value" ANDed with the test on that value. A null is therefore
not matched by the negative operators either: it is not "not equal to 5", it is **unknown**, as a database treats
it.

For any column, the rows matching an operator, the rows matching its negative operator, and the null rows
partition the table. A condition gives the same answer against a database as in memory.

`Not` is the exception: it negates the whole test, guard included, so the null rows come back:

```
Alias <> 'Ghost'        every minion who has an alias, and it isn't Ghost
NOT (Alias = 'Ghost')   the same, plus every minion with no alias at all
```

Both forms are useful, they are not interchangeable, and neither is normalised into the other.

The same applies to a property reached through a nullable. `BirthDate.Year` on a `DateTime?` is legal and behaves
as a nullable even though `Year` is an `int`. `Lair.Name` where the minion has no lair matches nothing, and
`Lair.Name IsNull` asks whether the lair is there. A database answers this through the join; the in-memory guard
makes the two agree.

## String comparison

Every string operator compares **ordinally** by default: the stored characters, compared as stored, case-sensitive,
with no linguistic rules. This applies where Weequery performs the comparison, and the comparison is one of the
two things a query may set for itself, through `InquirySettings.StringComparison`:

```csharp
var settings = InquirySettings.Default with { StringComparison = StringComparison.CurrentCulture };

context.Minions
    .WithWeequery(settings)
    .BindProperties(MinionBindings)
    .ApplyCondition("Name StartsWith 'Al'")
    .Build();
```

Ordinal is the default for two reasons:

- **It matches the database.** A column comparison happens in the database under the column's collation, and no
  collation in common use equates strings whose characters differ. Ordinal keeps in-memory and database results
  consistent, as the [null semantics](#null-semantics) do.
- **It is an order of magnitude faster.** A linguistic comparison walks a collation table; an ordinal one compares
  bytes. Over ten thousand rows the same filter takes 24us ordinal against 190us linguistic; see
  [BENCHMARKS.md](BENCHMARKS.md).

### Operators covered

Every string operator routes through the setting:

| Operator | Built from | Compares by |
|---|---|---|
| `=` and `<>` | `string.Equals` | the setting |
| `<`, `<=`, `>`, `>=` | `string.Compare` | the setting |
| `IsBetween`, `IsNotBetween` | the two above | the setting |
| `StartsWith`, `EndsWith`, `Contains` and their negatives | the matching `string` method | the setting |
| `IsIn`, `IsNotIn` | `Contains` over the list | the setting |
| `IsNull`, `IsNotNull` | a null test | **nothing**; see below |
| `IsMatch`, `DoesNotMatch` | [`Regex`](#regular-expression-matching) | its own pattern; `RegexOptions` is not this setting |

- **Null tests are not comparisons**, and neither is an equality against a null: they ask whether a value is
  present. The null guards that every string operator carries are likewise unaffected.
- **Case-insensitive matching** is this setting, not a separate feature:

  ```csharp
  InquirySettings.Default with { StringComparison = StringComparison.OrdinalIgnoreCase }
  ```

  It is often what is wanted when a culture is requested. It is not the default: `Name = 'alice fox'` does
  **not** find Alice Fox unless it is set.

### In memory versus in the database

The setting applies **in memory only**. Translated to a database it has no effect and cannot: the overloads that
take a `StringComparison` are not translatable by any provider, so using them would turn the query into a table
scan on the client. The column's collation decides there.

Under the default, the two agree. Any other setting deliberately separates them:

```
Name = 'Alice'   against a stored value holding a soft hyphen, "Al\u00ADice"

  Ordinal        no match. Those are not the same characters and one of them is longer
  CurrentCulture a match. A soft hyphen is ignorable, so it compares as "Alice"
  a database     no match, under any collation you are likely to be using
```

A linguistic setting is sometimes correct, for a filter meant to read the way a person reads. Choose it
knowingly, since in-memory and database results will then differ.

> [!WARNING]
> **`BuildExpression` returns the translatable form, and compiling it yourself applies neither rule.** It is the
> shape handed to a provider. Compiled directly, each method uses the framework's own default, and those are
> inconsistent: `StartsWith` and `EndsWith` compare linguistically while `Contains` and `==` are ordinal. Use
> [`BuildDelegate`](#in-memory-predicates), which applies the rules, and pass the intended settings.

## Indexing into a collection

Bind a list, an array or a dictionary, and a caller can ask about one element of it, named in brackets:

```csharp
.BindProperty(minion => minion.Commendations)     // a List<string>
.BindProperty(minion => minion.Tallies)           // a Dictionary<string, int>
```
```
Commendations[0] StartsWith 'Order of'
Tallies[shark_maintenance] > 5
```

- Lists and arrays index by position; dictionaries by key.
- The kind of collection is read from its interfaces rather than its class, so a property declared as `IList<T>`
  or `IDictionary<K,V>` indexes as the concrete type does.
- The index is text, converted to the collection's key type when the query is built, like any other value.

### Missing elements

An index at which nothing sits is neither an error nor a default value. It is the absence of a value and behaves
as a [nullable property](#null-semantics) does. All three of these are missing:

```
Tallies[pears]        the dictionary is there and has no such key
Commendations[9]      the list is there and is shorter than that
Tallies[apples]       there is no dictionary at all
```

The usual partition applies:

| Condition | Matches |
|---|---|
| `Tallies[pears] IsNull` | every row, in all three cases above |
| `Tallies[pears] > 0` | nothing |
| `Tallies[pears] <> 99` | **nothing**; the negative operators do not match a missing element either |
| `NOT (Tallies[pears] = 99)` | **everything**; `Not` negates the guard along with the test |

The collection is checked for null before it is indexed, so reading an element of a missing collection never
happens and never throws. The collection remains a binding in its own right: `Tallies IsNull` asks whether the
dictionary is there at all.

### Where an index can appear

An index can appear in four places. The examples above are **conditions**, where the caller picks the index.

**In a binding path**, where the application picks the index and the path may continue past the element. Use this
for "whatever is in slot zero" when the caller should not see the collection:

```csharp
.BindProperty("Assignments[0].LairID", "PrimaryLair")
.BindProperty("Tallies[shark_maintenance]", "SharkBudget")
```

With a selector, the compiler checks the whole path. Lists, arrays and dictionaries index as in any other C#:

```csharp
.BindProperty(minion => minion.Assignments[0].LairID, "PrimaryLair")
.BindProperty(minion => minion.Tallies["shark_maintenance"], "SharkBudget")
.BindProperty(minion => minion.Commendations[0], "TopCommendation")
```

- **A key is required.** The key an element binding would derive, such as `Assignments[0]`, is the same text a
  condition uses to ask for element zero of a binding called `Assignments`. The two could not be told apart, so a
  derived key is refused; supply the name the caller will see.
- **The index must be a literal.** `minion.Commendations[position]` over a variable is refused: a binding is made
  once, not per row, so it would capture the value `position` held at that moment while appearing to follow it.
  Write the number, or bind the collection and let the caller name the index.
- **A period inside brackets is not a separator**, so a dictionary key may contain one: `Tallies[a.b]` is one step
  keyed `a.b`.
- **In the [segments form](#bindings)**, a bracketed segment is an index and a bare one is
  a property name: `["Assignments", "[0]", "LairID"]`.

**In an operand**, comparing a value against an element:

```
Pay > [Tallies][shark_maintenance]
Tallies[shark_maintenance] > [Threshold]
```

**In a sort**, which orders nulls first as for any nullable:

```
OrderBy Tallies[shark_maintenance] DESC
```

All four forms produce the same guarded element access, so they mean the same thing and fail the same way. When
written back out, an index is always a second pair of brackets, `([Tallies][apples] > '5')`, never
`'Tallies[apples]'`, because only the first form reads back.

On the wire the index is a member of its own, omitted when there is none, so a payload written before indexing
existed is unchanged byte for byte:

```jsonc
{ "Operator": 6, "Field": "Tallies", "Index": "apples", "Values": ["5"], "Conditions": [] }
```

### Provider support for indexing

Indexing always works in memory. Against a database, as with [`IsMatch`](#regular-expression-matching), support
varies:

| Collection | Against a database |
|---|---|
| A **primitive collection**: a `List<string>`, an `int[]`, anything EF stores as a JSON array in one column | **Translates.** The guard becomes the provider's length function and the read becomes its array access: `json_array_length`/`->>` on SQLite, `cardinality`/`[n]` on PostgreSQL, `OPENJSON`/`JSON_VALUE` on SQL Server. Values are still sent as parameters |
| A **navigation collection** (rows in another table) | **Translates**, but see the warning below |
| A **`Dictionary<K,V>`** | **Not supported.** EF has no mapping for a dictionary property, so a model with one does not build. Index dictionaries in memory only |

PostgreSQL counts array positions from one. EF adjusts the index, so `Tags[0]` reaches the first element there as
everywhere else.

> [!WARNING]
> On a navigation collection, `Assignments[0].LairID` becomes a single-row subquery with **no `ORDER BY`**. A set
> has no first row, so "element zero" is whichever row the database returns first, which may differ between runs
> and between providers. Index a primitive collection, whose order is its stored order, or bind the intended value
> explicitly and give it a name.

## Quantifiers over collections

Indexing picks one element. A **quantifier** asks about the elements as a set, for example: does this minion have
an assignment to a lair called Volcano?

Bind the collection with `BindCollection`, and declare what may be asked about one of its elements:

```csharp
.BindCollection(minion => minion.LairAssignments, "Assignments", inner => inner
    .BindProperty(assignment => assignment.LairID)
    .BindProperty(assignment => assignment.IsPrimary)
    .BindProperty(assignment => assignment.Lair.Name, "LairName"))
```

```
Assignments Any  (LairName StartsWith 'Volcano')
Assignments All  (IsPrimary = false)
Assignments None (LairID = 5)
```

The quantifiers are `Any`, `All` and `None`. The parentheses are required: without them the end of the inner
condition could not be distinguished from whatever follows it.

**Resolved element bindings.** Omit the action and pass a depth instead, and the element's allow-list is walked out
of the element type by the same [`ResolveBindables`](#resolving-bindings-automatically) that walks
the entity:

```csharp
.BindCollection(minion => minion.LairAssignments, "Assignments")
.ApplyCondition("Assignments Any (Lair.Name = 'Volcano')")
```

The depth is counted from the element, as the entity's depth is counted from the entity, and defaults to the same
value, 1. On a link table the default is usually right: `0` binds the element's own members, which for a join row
are two ids and the two navigations they point at, while `1` also reaches the far side of each navigation:

```csharp
.BindCollection(minion => minion.LairAssignments, "Assignments", 0)   // LairID, MinionID, Lair, Minion
.BindCollection(minion => minion.LairAssignments, "Assignments")      // ...and Lair.Name, Lair.Capacity
```

> [!WARNING]
> The warning on [`ResolveBindables`](#resolving-bindings-automatically) applies twice over here:
> resolving opens the element type and, at the default depth, the types beyond it. Everything either can reach
> becomes nameable inside the brackets. Review the result, or declare the inside by hand where the element holds
> anything that should not be exposed. There is no `BindingUse` to set: an element is only tested, never sorted
> on and never read back.

Behaviour:

- **The inside is a separate allow-list.** Binding the collection exposes nothing within it. `Assignments` on its
  own is not a field a caller can compare, and `LairName` cannot be named outside the brackets. The two lists do
  not leak into each other, and a key cannot be a property in one and a collection in the other.
- **The inner condition is scoped to one element.** These are different questions; the first is usually the
  intended one:

  ```
  Assignments Any (LairID = 5 AND IsPrimary = true)          one assignment that is both
  Assignments Any (LairID = 5) AND Assignments Any (IsPrimary = true)   two assignments will do
  ```

- **The full language applies inside**: `AND`, `OR`, `NOT`, every operator, and nesting.
- **A quantifier is never unknown.** It is the one bound condition a null cannot defeat. An empty collection and a
  missing one give the same answer, since in both cases no element matches:

  | Condition | Empty collection | No collection at all |
  |---|---|---|
  | `Assignments Any (...)` | false | false |
  | `Assignments All (...)` | true | true |
  | `Assignments None (...)` | true | true |

  A quantifier and its negation therefore partition the rows, unlike the operators described in
  [Null semantics](#null-semantics).

**SQL translation.** Quantifying is the question SQL is built to answer, unlike indexing. It becomes an `EXISTS`
subquery, and the elements never leave the database:

```sql
SELECT "m"."MinionID", "m"."Name", "m"."Pay", ...        -- and the rest of the entity
FROM "Minions" AS "m"
WHERE EXISTS (
    SELECT 1
    FROM "LairAssignments" AS "l"
    WHERE "m"."MinionID" = "l"."MinionID" AND "l"."LairID" = @Value)
```

`All` and `None` translate to `NOT EXISTS`, which is why they need no guard: a subquery that finds no rows is
false, so its negation is true whether the collection is empty or absent. Verified on SQLite, PostgreSQL and SQL
Server.

On the wire a quantifier has the existing condition shape (an operator, a field and one child), so the payload
format is unchanged:

```jsonc
{ "Operator": 23, "Field": "Assignments", "Values": [],
  "Conditions": [ { "Operator": 6, "Field": "LairID", "Values": ["5"], "Conditions": [] } ] }
```

### Collections of values

A collection of scalars (a `List<string>`, an `int[]`, a list of dates, Guids or enums) has no element property to
name, so the element itself is called **`Value`**:

```csharp
.BindCollection(minion => minion.Tags, "Tags")
.BindCollection(minion => minion.Scores, "Scores")
```

```
Tags   Any  (Value = 'urgent')
Tags   None (Value StartsWith 'tmp')
Scores All  (Value >= 50)
Scores Any  (Value > 5 AND Value < 10)        one score between the two, not one above and another below
```

- **Binding.** With the action omitted, a collection of values binds `Value` alone; there is nothing to walk, so a
  depth or settings have nothing to apply to. The explicit form is `inner => inner.BindValue()`. `BindResolve`
  does the same for every collection of values it finds, alongside collections of objects.
- **`Value` is the only key inside**, and refers to the element there and nowhere else. It is not a reserved word,
  so a model with a property called `Value` is unaffected outside a quantifier over values.
- **Everything above still holds**: every operator the element type supports, parentheses scoping several tests to
  one element, and a missing collection answering as an empty one.
- **Null elements.** A null element in a `List<string?>` or `List<int?>` is a null like any other: `Value IsNull`
  finds it and a comparison passes over it.
- **Not collections of values:** a `byte[]` (a blob, `varbinary` to a database), a dictionary (a sequence of pairs;
  [index it](#indexing-into-a-collection) instead), and a string (a sequence of characters).
- **The key is still an ordinary property**, so `Tags IsNull` and `Tags[0] = 'urgent'` work as before, and unlike
  a collection of objects it still projects: a `List<string>` is one column to a provider that maps it.
- **Removing the quantifier.** To keep the property but drop the quantifier, ignore `"Tags[]"` in the settings, or
  remove `"Tags[].Value"` afterwards.

| Target | Support |
|---|---|
| EF Core 8 and later | Mapped as primitive collections. Quantifiers translate on SQLite (`json_each`), PostgreSQL (the array itself) and SQL Server (`OPENJSON`), with the value still sent as a parameter |
| OData | Declare the field with `ODataField.ForValues`; `Tags Any (Value = 'red')` becomes `Tags/any(d1: d1 eq 'red')` |
| Elasticsearch | Declare the field with `ElasticField.ForValues`; the condition becomes a query against the array field, for conditions that can be answered one value at a time. See [Weequery.Elasticsearch](Weequery.Elasticsearch/README.md) |

### Expressing joins as quantifiers

Two common filters are usually written as a SQL `JOIN` but are expressed in Weequery as a quantifier.

**A child table filtered on one of its own columns.**

```sql
SELECT * FROM Minions
JOIN LairAssignments ON (Minions.MinionID = LairAssignments.MinionID) AND (LairAssignments.LairID = @id)
```

The `ON` clause has two halves. The first is the navigation, which the model already defines; only the second is
written:

```csharp
context.Minions
    .WithWeequery()
    .BindProperties(MinionBindings)
    .BindCollection(minion => minion.LairAssignments, "Assignments", inner => inner
        .BindProperty(assignment => assignment.LairID))
    .ApplyCondition("Assignments Any (LairID = 5)")
    .Build();
```

```sql
SELECT "m"."MinionID", "m"."Name", "m"."Pay", ...        -- and the rest of the entity
FROM "Minions" AS "m"
WHERE EXISTS (
    SELECT 1
    FROM "LairAssignments" AS "l"
    WHERE "m"."MinionID" = "l"."MinionID" AND "l"."LairID" = @Value)
```

`MinionID` is never bound on the assignment or named in the condition. It defines how the tables relate, which is
not the caller's choice.

**Two hops, through a link table to its far side.**

```sql
SELECT * FROM Minions
JOIN LairAssignments ON (Minions.MinionID = LairAssignments.MinionID)
JOIN Lairs ON (Lairs.LairID = LairAssignments.LairID) AND (Lairs.Name = @name)
```

This is still one condition. The second hop is a property path bound inside the collection, the `LairName` binding
from [Quantifiers over collections](#quantifiers-over-collections):

```csharp
    .BindCollection(minion => minion.LairAssignments, "Assignments", inner => inner
        .BindProperty(assignment => assignment.LairID)
        .BindProperty(assignment => assignment.Lair.Name, "LairName"))
    .ApplyCondition("Assignments Any (LairName = 'Volcano')")
```

Both joins appear inside the `EXISTS`: the link table and the lair it reaches:

```sql
WHERE EXISTS (
    SELECT 1
    FROM "LairAssignments" AS "l"
    INNER JOIN "Lairs" AS "l0" ON "l"."LairID" = "l0"."LairID"
    WHERE "m"."MinionID" = "l"."MinionID" AND "l0"."Name" = @Value)
```

`"l0"` is the table `JOIN Lairs` refers to. Bind `LairID` beside `LairName`: a caller matching a lair by id needs no
`Lairs` table and gets a query one join shorter, the first statement above.

**Two hops where both ends are the same table.**

A link table can lead back to the type it started from. A minion who manages other minions is the same two hops
with `Minions` at both ends:

```csharp
class Minion { Guid MinionID; string Name; List<Report> Reports; }
class Report { Guid BossID; Minion Boss; Guid UnderlingID; Minion Underling; }
```

```sql
SELECT * FROM Minions
JOIN Reports ON (Minions.MinionID = Reports.BossID)
JOIN Minions AS Underlings ON (Underlings.MinionID = Reports.UnderlingID) AND (Underlings.Name = @name)
```

The binding is unchanged; the second hop is a property path inside the collection, regardless of where it lands:

```csharp
    .BindCollection(minion => minion.Reports, "Reports", report => report
        .BindProperty(row => row.UnderlingID)
        .BindProperty(row => row.Underling.Name, "UnderlingName"))
    .ApplyCondition("Reports Any (UnderlingName = 'Alice Fox')")
```

```sql
SELECT "m"."MinionID", "m"."Name"
FROM "Minions" AS "m"
WHERE EXISTS (
    SELECT 1
    FROM "Reports" AS "r"
    INNER JOIN "Minions" AS "m0" ON "r"."UnderlingID" = "m0"."MinionID"
    WHERE "m"."MinionID" = "r"."BossID" AND "m0"."Name" = @Value)
```

`"m0"` is the `Underlings` alias and `"m"` is the minion being asked about. The two allow-lists keep these apart:
`Name` outside the brackets is the boss, and `UnderlingName` is nameable only inside them, so one condition can ask
about both and refer to a different row each time:

```
Name StartsWith 'A' AND Reports Any (UnderlingName StartsWith 'A')
```

```sql
WHERE "m"."Name" LIKE @Value_startswith ESCAPE '\' AND EXISTS (
    SELECT 1
    FROM "Reports" AS "r"
    INNER JOIN "Minions" AS "m0" ON "r"."UnderlingID" = "m0"."MinionID"
    WHERE "m"."MinionID" = "r"."BossID" AND "m0"."Name" LIKE @Value1_startswith ESCAPE '\')
```

> [!NOTE]
> Two EF Core requirements apply to self references. Both navigations must be configured, since EF cannot infer
> which of the two `Minion` properties on `Report` is the inverse of the collection. An `Include` down a self
> reference loads the same entity type as both parent and child into one change tracker, producing a graph rather
> than a list; account for this before serializing it.

**Differences from a `JOIN`:**

- **Each row is returned once**, however many elements match. With two assignments to lairs called Volcano, a
  `JOIN` returns that minion twice; the quantifier returns it once. With a link table in the middle this is the
  usual case, and no `DISTINCT` is needed.
- **The other table's columns are not returned.** `SELECT *` across a join reads the joined rows too; an
  `Inquiry<Minion>` reads minions. Loading related rows is EF's job, and composes:

  ```csharp
  context.Minions
      .Include(minion => minion.LairAssignments)
      .ThenInclude(assignment => assignment.Lair)
      .WithWeequery()
      ...
  ```

  The filter stays an `EXISTS` and the `Include` adds its own join beside it. It loads every assignment of a
  matching minion, not only the matching ones; use `Include(minion => minion.LairAssignments.Where(...))` where
  only the matching ones are wanted.

## Projection

Conditions decide which rows a query returns. A **projection** decides which columns. Apply one with
`ApplyProjection` and build with `BuildProjected()`:

```csharp
var rows = query.WithWeequery()
    .BindProperties(MinionBindings)
    .ApplyCondition("IsActive = true")
    .ApplyProjection("Name, Pay")
    .BuildProjected();          // IQueryable<Dictionary<string, object?>>
```

```jsonc
[ { "Name": "Alice Fox", "Pay": 12000 },
  { "Name": "Bob Samuelson", "Pay": 0 } ]
```

The projection uses the same allow-list as conditions. Any bound field is projectable, under the same keys and
the same case-insensitive matching, and an unbound field is refused exactly as it is in a condition. Nothing extra
is declared, and a projection grants nothing that filtering had not already granted.

The result is a narrower `SELECT`, not a whole row read and then discarded:

```sql
SELECT "m"."Name", "m"."Pay"
FROM "Minions" AS "m"
WHERE "m"."IsActive" = @Value
```

This is verified on SQLite, PostgreSQL and SQL Server. Three columns of a wide table over a page of twenty is
substantially less work than twenty whole rows.

| Rule | Behaviour |
|---|---|
| **Keys use the binding's spelling** | `name`, `NAME` and `Name` all come back as `Name`, so callers typing it differently get the same shape |
| **Order is the order requested** | `"Pay, Name"` and `"Name, Pay"` differ in the order the entries are added |
| **A field named twice is kept once** | A dictionary holds each key once, so a duplicate has no meaning. A list assembled from checkboxes is safe |
| **Values are boxed** | `object?`, whatever the property held. Null where the value is null or the path to it runs through a null. A nullable value type boxes to null, not to its default |
| **No projection means everything allowed** | `BuildProjected()` with no projection reads every bound field that grants `Projection` |
| **A constant projects its value** | The same value for every row; see [Constant bindings](#constant-bindings) |
| **A collection of objects does not project** | It holds many rows and a column holds one. Query its elements with [a quantifier](#quantifiers-over-collections) instead. A [collection of values](#collections-of-values) does project, being one column to a provider that maps it |
| **An indexed field projects** | `Tallies[apples]` reads that one element and comes back keyed `Tallies[apples]`. An index with no element is a null, [as elsewhere](#missing-elements) |
| **The last call wins** | A projection is one list of columns and does not accumulate, so a second `ApplyProjection` replaces the first |

`ApplyProjection` also accepts the keys directly:

```csharp
.ApplyProjection(["Name", "Pay"])
.ApplyProjection(Projection.Parse(request.Fields))
```

Projection combines with paging:

```csharp
var (page, total) = query.WithWeequery()
    .BindProperties(MinionBindings)
    .ApplyCondition(request.Filter)
    .ApplySorts(request.Sort, DefaultSort)
    .ApplyPagination(request.PageSize, request.Page)
    .ApplyProjection(request.Fields)
    .BuildPagedProjected();
```

Only the page is projected. The count is over rows rather than over what is read from them, so the total is the
same with or without a projection.

### Wildcards

`*` reads every field the caller may read, and `Prefix.*` reads every field under that prefix:

```csharp
.ApplyProjection("*")                    // everything this caller is allowed
.ApplyProjection("Lair.*")               // Lair.Name, Lair.Capacity, Lair.Address.City
.ApplyProjection("Name, Lair.*")         // one field and a branch, in that order
```

- A wildcard expands within the allow-list, never around it. It matches only keys that grant `Projection`, so a
  field bound `Test`-only is no more readable through `*` than by name.
- A key matched twice, by name and by a wildcard, is read once, in the position it was first requested.
- The prefix keeps its dot: `Lair.*` matches `Lair.Name` and does not match `Lairyard.Capacity`.
- A prefix matching nothing behaves like any other unbound field: refused, or dropped and recorded where
  [`IgnoreUnboundFields`](#ignoring-unbound-fields) is set.

```
'Gizmo.*' matches nothing: no binding under 'Gizmo.' can be projected
```

> [!NOTE]
> `*` is not the same as sending no projection. Both read every allowed field, but `*` still names a projection:
> the DTO companions refuse it as [two answers to one question](#projecting-to-a-dto), and
> `AppliedProjection.IsEmpty` is false.

The same two wildcards work in [`Weequery.OData`](#odata) and [`Weequery.Elasticsearch`](#elasticsearch), where
they expand against the declared field set rather than against bindings. Those packages have no `BindingUse`, so
every declared field is readable.

### Binding uses

A binding grants three separable permissions: filter on the key, sort on it, and read it back. Pass a
`BindingUse` to grant only some of them:

```csharp
.BindProperty(minion => minion.Name)                                       // all three, the default
.BindProperty(minion => minion.Notes,  "Notes",  BindingUse.Projection)    // read it back, and nothing else
.BindProperty(minion => minion.OwnerID,"Tenant", BindingUse.Test)     // filter on it, never show it
.BindProperty(minion => minion.Morale, "Morale", BindingUse.Test | BindingUse.Sort)
```

| Use of the key | `Notes` | `Tenant` | `Morale` | `Name` |
|---|---|---|---|---|
| In a condition (`Notes Contains 'shark'`) | refused | allowed | allowed | allowed |
| In a sort (`OrderBy Notes`) | refused | refused | allowed | allowed |
| In a projection (`ApplyProjection("...")`) | allowed | refused | refused | allowed |
| As an operand (`Name = [Notes]`) | **refused** | allowed | allowed | allowed |

Two typical cases:

- A free-text note that should be returned but not searched, because `Contains` against an unindexed column of a
  large table is expensive.
- A tenant or owner column that must be filterable and must never be returned.

`Name = [Notes]` is a read of `Notes`: if allowed, a caller could learn the column's contents by bisection, one
query at a time. `Test` therefore governs both sides of an operator; the two sides cannot be granted separately.

A narrowed key is not unbound, and its refusal states what the key is bound for:

```
'Notes' cannot be used in a condition: it is bound for Projection
'Morale' cannot be projected: it is bound for Test, Sort
```

`BindingUse` is a flags enum:

| Value | Grants |
|---|---|
| `None` | nothing |
| `Test` | use in a condition, on either side of an operator |
| `Sort` | use in a sort |
| `Projection` | use in a projection |
| `All` | all three; the default everywhere |

It is accepted by every form of `BindProperty`, by `BindConstant`, and by `BindingRequest` for a whole set:

```csharp
new BindingRequest(nameof(Minion.Notes), "Notes", BindingUse.Projection)
```

`BindConstant` grants `Test | Projection`, since a constant has one value for every row and nothing to order by.
A sort naming a constant is dropped rather than refused, recorded in
[`DroppedFields`](#ignoring-unbound-fields) as *"it is a constant and has one value for every
row"*; the order is identical with or without the clause.

A projection that names nothing reads every field granting `Projection`, and nothing else: "all of it" means all
of what this caller may read.

### Combining broad and narrow bindings

`BindResolve` also takes a use. The recommended pattern is to make the whole model readable and then grant the
few fields a caller may filter and sort on:

```csharp
.BindResolve(use: BindingUse.Projection)                              // everything readable
.BindProperty(minion => minion.Name, use: BindingUse.Test)       // ...these two also askable
.BindProperty(minion => minion.Pay,  use: BindingUse.Test | BindingUse.Sort)
```

`Name` ends up `Projection | Test` and `Pay` ends up `All`. **Binding the same property twice adds to its uses
rather than replacing them**, so the order of the two calls does not matter. Every field the second pass did not
name stays readable and cannot be filtered or sorted on.

> [!WARNING]
> **A use is only ever added, never removed.** The same rule means a narrow binding does not survive a broad call
> after it:
>
> ```csharp
> .BindProperty(minion => minion.Pay, use: BindingUse.Projection)
> .BindResolve()                        // grants All to everything, including Pay
> ```
>
> `Pay` is now filterable. To prevent this, narrow the resolve with `BindResolve(use: BindingUse.Projection)`,
> call it first, or remove the use afterwards with [`RemoveBinding`](#and-taking-one-back-off), the only operation
> in the library that revokes.

A [`ValueConverter`](#value-converters) merges the same way. A bind that does not mention a
converter does not ask for none, so the call that names one wins, in either order. This lets a resolved set, which
carries no converters, be given one afterwards:

```csharp
.BindResolve(use: BindingUse.Projection)
.BindProperty(minion => minion.Name, convert: Upper, use: BindingUse.Test)   // Name is now folded
```

Two *different* converters on the same key are refused, since the library cannot merge them (for example
`TOUPPER` and `TOLOWER`) and choosing one silently would apply a conversion nobody asked for:

```
'Name' is already bound with a different ValueConverter. One key cannot mean two normalisations of the same
property, so bind it once with the converter it should have
```

> [!NOTE]
> "Different" means a different object, not different behaviour. A converter wraps a delegate, so two built from
> the same lambda cannot be shown to agree and count as two. Store the converter in a field and pass that, rather
> than calling `ValueConverter.For<string>(...)` twice.

On the wire, the projection is one more member on `TransportCondition`, omitted when unused, so payloads written
before it existed are unchanged:

```jsonc
{ "Query": "IsActive = true", "Projection": "Name, Pay" }
```

```csharp
.ApplyCondition(transport.Unpack())
.ApplyProjection(transport.UnpackProjection())    // None where the payload named none, so this is safe either way
```

## Sorting and paging

```csharp
.ApplySorts([
    new Sort("IsActive", SortDirection.Ascending),
    new Sort("Salary", SortDirection.Descending),   // breaks ties within IsActive
])
.ApplyPagination(pageSize: 20, page: 2)             // page is zero based
```

- Sorts apply in the order given, each breaking ties in the one before.
- Sort fields must be bound.
- Nullable properties sort nulls first.
- A field with no ordering of its own (a collection, a navigation property) is **dropped** rather than refused. It
  can be bound and tested for null, but has no first row, so the clause imposes no order. It is recorded in
  [`DroppedFields`](#ignoring-unbound-fields) with the type named.
- One element of a collection does sort: `OrderBy Tallies[shark_maintenance] DESC` orders by a number, and rows
  with no such element sort as nulls. See [reaching into a collection](#indexing-into-a-collection).

> [!IMPORTANT]
> **Always page with a sort.** Paging without one is accepted, but the order is arbitrary, so the same row may
> appear on two pages. Sort on something that breaks every tie:
>
> ```csharp
> .ApplySorts([
>     new Sort("Salary", SortDirection.Descending),
>     new Sort("MinionID", SortDirection.Ascending),  // nothing ties on this, so the order is total
> ])
> .ApplyPagination(pageSize: 20, page: 2)
> ```

### Sort strings

Sorts have their own small language, separate from the condition language, so a caller can send the two
independently:

```csharp
.ApplySorts("Salary DESC, Name")
```

A sort string is a comma-separated list of fields, each optionally followed by a direction. Without a direction
a field sorts ascending, as in SQL. `Asc`, `Ascending`, `Desc` and `Descending` are accepted, case-insensitively,
and the clause may open with `OrderBy`:

```
Salary                              ascending, which is what a field on its own means
Salary DESC
Salary Descending, Name             several, applied in the order written
OrderBy Salary DESC, Name ASC       the same clause, opened with the separator
[Lair.Capacity] DESC                a field written the way a condition writes one
```

`QueryStyle.Native` refuses the two-word `ORDER BY` and accepts only `OrderBy`, the same rule it applies to
operators. Pass the style to enforce it:

```csharp
.ApplySorts(request.Sort, DefaultSort, QueryStyle.Native)
Sort.Parse(clause, defaultSort, QueryStyle.Native);
```

A field named `Order` is unaffected in either style; only the two words together form a prefix.

The second argument is the **default sort**, used when the caller requested none:

```csharp
.ApplySorts(request.Sort, [new Sort("MinionID", SortDirection.Ascending)])
```

`Sort.Parse(clause, defaults)` does the same without a query, returning the list.

`ToQuery()` writes a sort list back out in one canonical spelling: field bracketed, direction always stated, in
its short form. Unlike a condition, the round trip is exact, since a sort carries no value to be re-read against a
property's type.

```csharp
sorts.ToQuery();                    // "[Salary] DESC, [Name] ASC"
```

### Paged results with a total

A total ("Showing 21 to 40 of 387") is the size of the whole filtered set, so it needs a second query.
`BuildPaged()` builds both:

```csharp
var (page, total) = context.Minions
    .WithWeequery()
    .BindProperties(MinionBindings)
    .ApplyCondition(request.Filter)
    .ApplySorts(request.Sort, DefaultSort)
    .ApplyPagination(request.PageSize, request.Page)
    .BuildPaged();

var matched = await total.FirstOrDefaultAsync();
var rows    = await page.ToListAsync();
```

It returns a **`PagedQuery<T>`**, two queries and a deconstructor:

```csharp
public record PagedQuery<T>(IQueryable<T> Page, IQueryable<int> Total);
```

| Member | Contents |
|---|---|
| `Page` | Exactly what `Build()` returns: every condition, then every sort, then the window |
| `Total` | The number of rows the conditions matched, as a query of one number. It carries the filter only, since a count does not depend on order |

Deconstruct with `var (page, total) =`, or keep the record and read both members.

`Total` selects a number, not rows; no entity column appears in it:

```sql
SELECT COUNT(*)
FROM (
    SELECT 1 AS "Key"
    FROM "Minions" AS "m"
    WHERE "m"."Pay" > @Value
) AS "m0"
GROUP BY "m0"."Key"
```

The nesting is required. LINQ has no scalar query: `Count()` is a terminal operation, so no `IQueryable` *is* a
count. A grouping on a constant is the only count that remains a query; every row lands in the one group, and
every provider tested folds it into a single `COUNT` over the filtered set.

Read `Total` with `FirstOrDefaultAsync`. Other endings compile but behave differently:

| Ending | Result |
|---|---|
| `FirstOrDefaultAsync()` | The total, and `0` where nothing matched. Correct |
| `SingleAsync()` | The total, but throws where nothing matched: a grouping over no rows produces no group, so the query returns no row |
| `CountAsync()` | Always `1`, the number of rows in the count query. Compiles, looks natural, and is silently wrong |

- **Neither query has run.** Weequery does not execute them. `FirstOrDefaultAsync` belongs to Entity Framework
  Core, and the core library has no dependencies. Execute both with whatever executes your other queries; as with
  `Build()`, nothing happens until enumeration.
- Use `total`, not the length of `page`, which only reports the page size requested.
- **It is two round trips.** A `COUNT` and a `SELECT` are two statements. If a screen shows no total, use
  `Build()` instead.
- **`Page` still composes.** It has not run, so it accepts a `Select`:

  ```csharp
  var (page, total) = query.BuildPaged();

  var matched = await total.FirstOrDefaultAsync();
  var rows    = await page.Select(minion => new MinionSummary(minion.Name, minion.Pay)).ToListAsync();
  ```

- **`Total` does not compose.** It is an aggregate, with nothing left to compose against. A count needing a
  different filter from the page needs a second Inquiry.
- **Without `ApplyPagination` there is no window**, so `Page` is the whole filtered, sorted result and the count
  equals its length. This is not an error, but the count is then a redundant round trip.

### Default page size

Without a window, a request that omits the page size returns every row in the table. `DefaultPageSize` sets a
window for every query built from those settings, including queries that never call `ApplyPagination`:

```csharp
var settings = InquirySettings.Default with { DefaultPageSize = 50 };

context.Minions
    .WithWeequery(settings)
    .BindProperties(MinionBindings)
    .ApplyCondition(request.Filter)
    .Build();                              // fifty rows. Nothing here mentioned paging
```

The default is a floor for requests that name no size, not a ceiling: a caller that names a size gets it. A caller
may also name a page and leave the size to the default:

```csharp
.ApplyPagination(pageSize: null, page: 2)   // page two, fifty at a time
.ApplyPagination(pageSize: 0,    page: 2)   // the same: zero is treated as no size
.ApplyPagination(pageSize: 200,  page: 2)   // page two, two hundred, because they asked
```

| Argument value | Treated as |
|---|---|
| `pageSize` null, zero or negative | not specified: `DefaultPageSize`, or no window where that is null. A query string makes an omitted field and a zero field the same mistake, so both get a page rather than one getting a 400 |
| `page` negative | the first page, since there is nothing before it |
| `pageSize` and `page` whose row offset overflows `int` | **refused** (throws) |

```csharp
.ApplyPagination(pageSize: 50, page: -1)    // the first page
.ApplyPagination(pageSize: 1000, page: int.MaxValue)   // throws
```

No single argument value throws; only the overflowing pair does. A thousand rows a page, two billion pages in, is
an offset that does not fit the `int` that `Skip` takes. Unchecked, it would wrap to a negative skip and silently
return the first page. There is no sensible value to substitute: the first page was not requested, and the last
page is not known without running the query.

> [!WARNING]
> **`DefaultPageSize` is not a cap.** `PageSize = 1000000` returns a million rows. If that matters, clamp the value
> on the way in, where the refusal can explain why. A default handles the request that forgot; a limit handles the
> request that did not.

- The default `DefaultPageSize` is `null`: no window, all rows, as before the setting existed.
- `DefaultPageSize = 0` is refused when the settings are created. A zero in your own startup code is a typo and
  should fail on the first run; a `pageSize=0` from a caller's form is treated as no size.

## Serializing conditions

A condition can cross a process boundary in two forms.

**As a query string**, with `ToQuery()`:

```csharp
string text = condition.ToQuery();     // "(([Salary] > 10000) AND ([Name] Contains 'temp'))"
ICondition again = ConditionFunctions.ParseQuery(text);
```

**As an object graph**, with `Pack()`:

```csharp
var json = JsonSerializer.Serialize(condition.Pack());
var again = JsonSerializer.Deserialize<PackedCondition>(json).Unpack();
```

`TransportCondition` carries either form, so a request DTO can accept whichever the client prefers.

Both round trips preserve **meaning, not types**. Values travel as text and come back as strings, which the
expression builder parses against the bound property's type when the query is built. Formatting is invariant and
round-trippable, so a `DateTime` keeps its sub-second precision and its `Kind`.

Each packed operand is either a value or the key of a bound property. A value is written alone; only a key
carries a `Source`, so an ordinary condition costs nothing extra:

```jsonc
// Name IsIn ('Alice Fox', [Alias])
{ "Operator": 10, "Field": "Name", "Conditions": [],
  "Values": [ "Alice Fox", { "Source": 1, "Value": "Alias" } ] }

// Name IsIn ('Alice Fox', 'Bob Samuelson')
{ "Operator": 10, "Field": "Name", "Conditions": [],
  "Values": [ "Alice Fox", "Bob Samuelson" ] }
```

The two are distinguished by **shape** (a string versus an object), never by guessing from the text. No value can
be mistaken for a key, and a key cannot arrive as bare text.

### Combined query strings

`ParsedQuery` carries a condition, sorts and a projection in one string, each part introduced by its own keyword:

```csharp
var (condition, sorts, fields) = ParsedQuery.Parse("Pay > 10000 OrderBy Pay DESC Select Name, Pay", defaultSort);

query.ApplyCondition(condition).ApplySorts(sorts).ApplyProjection(fields);
```

It deconstructs into three parts, or into two (condition and sorts) when the projection is not needed. Every part
is optional: with no separator the whole string is a condition, a leading separator means that part and no
filter, and `defaultSort` applies wherever no sorts are named.

```
Pay > 10000 OrderBy Pay DESC Select Name, Pay   all three
Pay > 10000 Select Name, Pay                    a condition and a projection, and no sorting
OrderBy Pay DESC Select Name                    sorts and a projection, and no filtering at all
Select Name, Pay                                a projection, and nothing else
Pay > 10000                                     a condition, and whatever default sort was given
```

It writes back out:

```csharp
parsed.ToQuery();       // "([Pay] > '10000') OrderBy [Pay] DESC Select [Name], [Pay]"
```

- **The order is fixed**: condition, then sorts, then projection, because position identifies the part.
  `Select Name OrderBy Pay` is refused, not reordered.
- **The separators are mandatory.** Without them the whole string is read as one condition.
- Under `QueryStyle.Native` the sort separator is `OrderBy` only, and `ToQuery(QueryStyle.Native)` writes that
  spelling, so a combined string survives its own round trip. `Select` is one word in every style.
- **A `Select` with nothing after it is refused**, rather than read as an empty projection. Omitting the keyword
  already means "no projection", so a bare `Select` is treated as an unfinished column list.
- A binding may not be named `Select`, just as none may be named `OrderBy`: where a part could begin, the word is
  read as the separator. It is listed with the other [reserved words](#key-rules).
- Split points are found by parsing each part and seeing where it ends, not by searching the text for keywords.
  `Name = 'Select'` is one comparison and no projection.

## Query requests

`QueryRequest` holds a whole request: what to ask and which page of the answer to return. Every member is a
scalar, so it binds from a query string without a custom model binder or a parameter per part:

```csharp
[HttpGet("search")]
public async Task<IActionResult> Search([FromQuery] QueryRequest request)
{
    var (page, total) = _context.Minions
        .WithWeequery()
        .BindProperties(MinionBindings)
        .ApplyRequest(request, DefaultSort)
        .BuildPagedProjected();

    return Ok(new { total = await total.FirstOrDefaultAsync(), rows = await page.ToListAsync() });
}
```

| Member | Is | Read by |
|---|---|---|
| `Query` | the whole query as [one string](#combined-query-strings): filter, then sorts, then fields | `Unpack()` |
| `Page` | which page, counting from zero | `ApplyPagination` |
| `PageSize` | how many rows a page holds | `ApplyPagination` |

```
?query=Pay > 10000 OrderBy Pay DESC Select Name,Pay&page=0&pageSize=20
```

- **The filter, sorts and fields are one string**, in the grammar [`ParsedQuery`](#combined-query-strings) reads:
  the same text a user types into one box and a saved view stores. There is no combination of members to get
  wrong.
- **The window is kept out of `Query`.** Paging is not part of the query language, and a caller cannot set it by
  typing into the filter box.
- **`Query` is read as [`Native`](#one-spelling-per-operator) only**, with no style to pass. `&&`, `IS NULL` and
  `ORDER BY` are refused, each refusal naming the spelling to use instead. The permissive parser exists for text
  saved before the native spelling was settled, which is text the application holds; callers can be held to the
  one grammar.
- **A packed condition goes on a [`TransportCondition`](#serializing-conditions)**, not here. A query string
  cannot carry a packed tree, and a JSON body has its own type for it.
- **`ApplyRequest` is exactly the calls it replaces**, in the required order. To make them yourself, `Unpack()`
  returns all three parts at once, and `UnpackCondition()`, `UnpackSorts()` and `UnpackProjection()` return one
  each.

> [!WARNING]
> **A `QueryRequest` grants nothing.** A request names fields; the bindings decide whether it may have them. Every
> allow-list rule applies to a request exactly as to a hand-built condition, and a `QueryRequest` naming
> `PasswordHash` gets the same refusal as a string naming it. Binding from the query string does not put the query
> string in charge.

An empty member keeps its existing meaning:

| Left empty | Means |
|---|---|
| no filter in `Query` | no condition added. A condition the Inquiry already had stays, since conditions accumulate |
| no `OrderBy` in `Query` | the `defaultSort` passed to `ApplyRequest` |
| no `Select` in `Query` | **clears any projection already applied**, and reads every field the caller may read |
| `PageSize` | [`DefaultPageSize`](#default-page-size), or no window where there is none. **`0` and negative values mean the same**: a size that cannot hold a page is no size |
| `Page` | the first page. **A negative value means the same**, since there is nothing before the first |

The `Select` row differs deliberately. A projection is one list and does not accumulate, so a request naming no
columns asks for every column the caller may read. See [binding uses](#binding-uses).

**Nothing is parsed until it is applied.** The members hold the text as it arrived, so creating and reading a
request costs and refuses nothing. Malformed text throws `WeequeryException` at `ApplyRequest`, where a request
handler can answer with a 400 -- or check first with [validation](#validation).

## Validation

`Validate()` reports every problem in a request at once, without throwing, so a caller who got three inputs wrong
learns about all three in one round trip:

```csharp
var problems = inquiry.Validate(request, DefaultSort);

if (!problems.IsValid)
{
    return BadRequest(problems.Problems.Select(problem => problem.ToString()));
}

var (page, total) = inquiry.ApplyRequest(request, DefaultSort).BuildPagedProjected();
```

```
condition: Unbound field: 'Gizmo'
sort: Unbound field: 'Doohickey'
projection: Unbound field: 'Widget'
```

Each problem carries the `WeequeryError` to branch on, the message to show, and a `Part` saying where it came
from, so a message can be shown beside the input that caused it:

| `Part` | Source |
|---|---|
| `Condition` | the filter |
| `Sort` | the sorts |
| `Projection` | the projection |
| `None` | the request itself, which is [the one paging refusal](#default-page-size) |

Two overloads:

```csharp
inquiry.Validate()                          // what is applied, resolved against the bindings
inquiry.Validate(request, DefaultSort)      // that, plus reading the request's text in the first place
```

`ApplyCondition(string)` parses immediately, so a malformed filter throws before the no-argument `Validate()` can
see it. The request overload parses the text itself, inside a `try`, and reports both what will not parse and what
parses but names nothing bound. These are two passes, so one part can report one problem from each: a sort clause
that will not parse is one finding, and a sort clause naming an unbound field is another.

- **Validation changes nothing.** The request overload works on a copy; the Inquiry is unchanged and may be
  validated any number of times.
- **One problem per part per pass.** A condition naming two unbound fields reports the first; the second appears
  after the first is fixed. Reporting every fault would require the expression builder to continue past an
  unresolved field and build a tree around the gap, which Weequery does not do.
- **Nothing executes.** Validation builds what it needs and discards the result; the cost is that of a build.
- **It fills `DroppedFields`**, as a build does, so what a query [drops](#ignoring-unbound-fields)
  and what it refuses arrive together. This is its only side effect.
- **Valid means it will build**, not that it will return rows or that the database will accept it. An
  [`IsMatch`](#regular-expression-matching) against SQL Server validates here and fails at the provider,
  unless the operator is excluded as described below.

### Backend operator support

By default, validation cannot know what the backend can run. [`IsMatch`](#regular-expression-matching) is
the standard example: SQL Server has no regular expressions, so a filter using a bound field and a legal operator
passes validation and is then refused by the provider, far from the request and with an unhelpful message.

Declare the unsupported operators in the settings:

```csharp
var minions = context.Minions.WithWeequery(InquirySettings.Default with
{
    Operators = OperatorSupport.Without(Operator.IsMatch, Operator.DoesNotMatch),
});
```

The refusal then happens with every other refusal:

```csharp
inquiry.ApplyCondition("Name IsMatch '^A'").Validate();
// test: 'Name' is tested with IsMatch, which this data source does not support

inquiry.ApplyCondition("Name IsMatch '^A'").Build();
// throws WeequeryException(NotTranslatable, ... the same message)
```

`Build()` throws and `Validate()` reports, as for every other refusal. The code is `WeequeryError.NotTranslatable`,
the same one the [OData](#odata) and [Elasticsearch](#elasticsearch) translators throw when an operator has no
representation on their side.

| Member | Meaning |
|---|---|
| `OperatorSupport.Without(...)` | Every operator except these. Recommended: a backend is usually everything minus two or three operators |
| `OperatorSupport.Supporting(...)` | Exactly these and nothing else. A literal list, so `And`, `Or` and `Not` must be named where conditions use them |
| `OperatorSupport.Everything` | The default. Refuses nothing |

- **Every operator counts**, including `And`, `Or`, `Not` and the quantifiers. `Supporting` is literal and does not
  add the conjunctions implicitly.
- **Operators inside a quantifier count too.** This is where the operator walk differs from the field walk: a
  quantifier's children use a different allow-list, so fields stop at the boundary, but they run on the same
  backend, so operators do not.

  ```
  Assignments Any (LairName IsMatch '^V')      still an IsMatch, still refused
  ```

- `condition.OperatorsUsed()` returns the list of operators a condition uses.

> [!NOTE]
> Operator support does not guarantee the query will translate. It only guarantees the query uses no operator
> declared missing. A bound property nothing maps, a comparison the provider cannot express, or a collection it
> will not reach through still surface at the provider.
> [`Weequery.EntityFrameworkCore`](Weequery.EntityFrameworkCore/README.md) checks the first of these, and
> `Build().ToQueryString()` exposes the rest.

## In-memory predicates

Without a database, build a predicate directly and apply it to an in-memory list:

```csharp
Expression<Func<Minion, bool>> predicate =
    Inquiry<Minion>.BuildExpression(MinionBindings, condition);

Func<Minion, bool> compiled =
    Inquiry<Minion>.BuildDelegate(MinionBindings, condition, settings);
```

| Method | Returns | String rules and regex timeout |
|---|---|---|
| `BuildDelegate` | a compiled `Func<T, bool>` | Applied. Runs in memory, so it applies the [string comparison rules](#string-comparison) and the regex timeout, and takes the settings that decide the former. Without the argument it uses `InquirySettings.Default` |
| `BuildExpression` | a translatable `Expression<Func<T, bool>>` | Not applied |

> [!WARNING]
> If you compile the result of `BuildExpression` yourself, neither the string comparison rules nor the regex
> timeout apply. See the section on how strings compare.

## Supported types

`bool`, `byte`, `sbyte`, `char`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`,
`DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid`, `string`, and any `enum`.

The `Nullable<>` form of each is also supported. A property of an unsupported reference type may still be bound,
but supports only `IsNull` and `IsNotNull`.

## Common mistakes

- **Discarding the result of `Apply` or `Bind`.** An Inquiry is immutable: every `Apply` and `Bind` leaves the
  instance unchanged and returns a new one carrying the change. A chain composes like a LINQ chain; a call whose
  result is discarded does nothing, and no warning is given.

  ```csharp
  var inquiry = query.WithWeequery().BindProperties(MinionBindings);

  inquiry.ApplyCondition("IsActive = true");   // does nothing. The filtered one went nowhere
  var all = inquiry.Build();                   // every row, and no complaint
  ```

  Keep the chain or keep the result:

  ```csharp
  var active = inquiry.ApplyCondition("IsActive = true").Build();
  var paid   = inquiry.ApplyCondition("Pay > 10000").Build();   // paid, and only paid
  ```

  Immutability makes a configured Inquiry safe to keep and branch from: no branch affects another. A copy copies
  the lists, not their contents, and bindings are immutable and shared, so a copy costs one dictionary and two
  lists.

- **Nesting conditions more than 16 levels deep.** `Pack()`, `Unpack()` and `ToQuery()` throw beyond that depth.
  `ToString()` writes `<nested too deep>` where it stopped and returns what it has, so that debugging output never
  throws. The limit counts logical nesting, not parentheses: `(((Salary > 1)))` has no nesting, while
  `A AND B OR C AND D` has no parentheses and is a tree two levels deep, because precedence nests it.

- **Expecting values in the SQL text.** Filter values are always sent as parameters, never written into the SQL.
  The same condition shape produces one statement and one query plan whatever the values:

  ```sql
  -- Name = 'Alice Fox' and Name = 'Someone Else' both produce:
  WHERE "m"."Name" = @Value
  ```

- **Sending more than 1000 values in an `IsIn` list.** The list becomes parameters, and providers limit how many
  they accept. The condition is refused when built, with the operator and the count named, on every route in.

- **Assuming string matching is case-insensitive.** In memory every string operator compares ordinally unless the
  query says otherwise, so `Name = 'alice fox'` does not find Alice Fox; ask for `OrdinalIgnoreCase` if needed.
  Against a database the column's collation decides, including case sensitivity, and no setting changes that. See
  [How strings compare](#string-comparison).

- **Expecting enums to order by name.** An enum orders by its underlying values. With
  `enum Rank { Low = 1, High = 2 }`, `Rank > Low` finds `High`, and renaming the members changes nothing.

- **Exposing property names through keys.** Keys are the allow-list, and auto-generated keys are the property path,
  so `BindProperty(x => x.Name)` puts the property name on the wire. Pass explicit keys if the model's names should
  not be public.

- **Letting `WeequeryException` escape.** Parse failures, unbound fields, unsupported operators and bad values all
  throw `WeequeryException`. Most of these come from caller input, so a request handler should normally catch it
  and return a bad request.

## TypeScript client

The TypeScript package in [`js/`](js/README.md) builds conditions in the browser: the same condition tree, query
language, packed JSON and operator numbers. The operator numbers are the wire format and are pinned by a test on
both sides, so neither side can drift unnoticed.

```ts
import { and, BindingSet, gt, eq, toQuery, pack, validateQuery } from 'weequery';

toQuery(and(gt('Salary', 10000), eq('IsActive', true)));
// "(([Salary] > 10000) AND ([IsActive] = True))"
```

Given the same binding list as the server, it validates queries and predicts the server's refusals, so an unbound
field, a `Contains` on a number or a sort on a constant becomes a message beside the input rather than a round trip
and a 400. The package has no dependencies.

> [!IMPORTANT]
> Client-side validation grants nothing. The server's binding list remains the only allow-list; an outdated copy on
> the client only makes less accurate predictions.

## Projecting to a DTO

[Projection](#projection) returns a dictionary, because the caller chooses the columns. When the application
chooses them and the shape is an existing type, use one of three companion packages, one per mapper, each with the
same two methods:

```csharp
var (page, total) = context.Minions
    .WithWeequery()
    .BindProperties(MinionBindings)
    .ApplyCondition(request.Filter)
    .ApplySorts(request.Sort, DefaultSort)
    .ApplyPagination(request.PageSize, request.Page)
    .ProjectToPaged<Minion, MinionSummary>(configuration);
```

| Package | Mapper | How the mapping is supplied |
|---|---|---|
| [`Weequery.AutoMapper`](Weequery.AutoMapper/README.md) | AutoMapper | an `IConfigurationProvider` or an `IMapper` |
| [`Weequery.Mapster`](Weequery.Mapster/README.md) | Mapster | a `TypeAdapterConfig`, or nothing, since Mapster maps by convention |
| [`Weequery.Mapperly`](Weequery.Mapperly/README.md) | Mapperly | the generated projection method itself, there being no runtime to ask |

Weequery decides which rows; the mapper decides the shape of a row. Only the DTO's columns leave the database, and
only the page is projected, since a count is of rows rather than of what is read from them.

Use the package for the mapper you already use. They are separate so that the core library never pulls in a
mapper, and choosing one never pulls in the other two. The core library has no dependencies.

> [!NOTE]
> **AutoMapper licensing.** AutoMapper is dual-licensed from v15: free for individuals, for non-commercial use and
> for organisations under a revenue threshold, with a paid key above it. `Weequery.AutoMapper` uses 15.1.3 because
> v14, the last MIT release, has an unpatched high-severity denial-of-service vulnerability
> ([CVE-2026-32933](https://github.com/advisories/GHSA-rvv3-g6hj-g44x)) that will not be fixed in the MIT line.
> Mapster and Mapperly are MIT.

## Elasticsearch

A condition is a tree, not SQL, so it can target other backends.
[`Weequery.Elasticsearch`](Weequery.Elasticsearch/README.md) translates one into Elasticsearch Query DSL:

```csharp
ElasticQuery.ToJson(ConditionFunctions.ParseQuery("IsActive = true AND Pay > 10000"), fields);
```
```jsonc
{ "bool": { "filter": [ { "term": { "active": true } },
                        { "range": { "salary": { "gt": 10000 } } } ] } }
```

With no entity or mapping to read, the allow-list is declared: a key, the index field it maps to, and the field's
type. Sorts, the window and the projection translate to `sort`, `from`/`size` and `_source`.

- **Null semantics are preserved.** Elasticsearch already agrees with [Weequery's null rules](#null-semantics) on
  the positive operators, but a bare `must_not` matches documents that lack the field entirely. Negative operators
  are therefore written with an `exists` clause beside them, so `Alias <> 'Ghost'` and `NOT (Alias = 'Ghost')`
  still return different results, as they do in Weequery.
- Quantifiers become `nested` queries, and `All` becomes "no element fails it", the only way to express it over
  nested documents.
- The package has no dependencies: the Query DSL is JSON, so it is not tied to a client version, generation or
  licence. For the same reason it works against OpenSearch.

## OData

[`Weequery.OData`](Weequery.OData/README.md) writes a condition as an OData `$filter`:

```csharp
ODataFilter.Write(ConditionFunctions.ParseQuery("IsActive = true AND Pay > 10000"), fields);
// "(Active eq true and Salary gt 10000)"
```

Sorts, the window and the projection translate to `$orderby`, `$top`/`$skip` and `$select`. Quantifiers become
lambdas: `Assignments/any(d1: d1/LairID eq 5)`.

- **Null semantics are preserved.** The OData specification says a null is *"not equal to any other value"*, so
  `Alias ne 'Ghost'` returns records with no alias, where [Weequery's does not](#null-semantics). Every negative
  operator therefore carries a guard, `(Alias ne null and Alias ne 'Ghost')`; `NOT` deliberately does not.
- Comparing two properties translates directly: `Name = [Alias]` becomes `Name eq Alias`.
- The package has no dependencies. A `$filter` is text, and the OData client libraries are large and versioned
  against their own model.

## Performance

Measurements are in [BENCHMARKS.md](BENCHMARKS.md). The performance claim has two halves.

**Execution has no overhead, and this is proven rather than timed.** For the comparison operators, Weequery gives
the provider byte-for-byte the statement a hand-written `Where` produces. The test suite asserts this on every
build by comparing the SQL. A server given identical SQL runs it identically, regardless of the machine.

**The remaining cost is client-side work**, measured by [`Weequery.Benchmarks`](Weequery.Benchmarks/README.md):
parsing the caller's filter, resolving the allow-list and building the expression tree. Results report time and
allocations per operation, with the CPU stated above each table.

```bash
dotnet run -c Release --project Weequery.Benchmarks
```

The benchmarks also cover the two support libraries. Writing an Elasticsearch query or an OData `$filter` involves
no database, which makes those the most reliable figures in the set.

> [!IMPORTANT]
> Read the baseline before the ratios; the obvious comparisons are misleading in opposite directions.
>
> - Filtering a `List` through `AsQueryable` makes LINQ to Objects compile the expression tree on every call, about
>   a third of a millisecond, which swamps everything else. A ratio taken there **flatters** Weequery.
> - Comparing a compiled predicate against a hand-written lambda **understates** Weequery. Anything produced by
>   `Expression.Compile` is a `DynamicMethod`, which the JIT does not inline into its caller; a compiler-generated
>   lambda is an ordinary method and is inlined. This applies to any expression tree, not only Weequery's.
>
> The tables therefore run each predicate three ways: as a lambda, as a compiled expression, and through Weequery.
> This separates the runtime's share of the gap from Weequery's. On a condition needing no null guards, Weequery's
> share is the smaller by a wide margin.

None of this applies to a database, where EF Core caches its plans and receives the same SQL either way.

## Building and testing

```bash
dotnet build
```

```bash
dotnet test Tests/Tests.csproj
```

The suite runs against SQLite with no setup. PostgreSQL and SQL Server tests are opt-in and report as skipped
unless enabled:

| Environment variable | Enables |
|---|---|
| `WEEQUERY_TEST_POSTGRES` | PostgreSQL tests (set to a connection string) |
| `WEEQUERY_TEST_SQLSERVER` | SQL Server tests (set to a connection string) |
| `WEEQUERY_TEST_THROUGHPUT` | throughput comparisons |

## Credits

**[Superfilter](https://github.com/Ibramadi75/Superfilter)** implemented the same idea first: dynamic filtering,
sorting and pagination over `IQueryable` in C#, mapping textual filter criteria onto strongly typed expressions. It
is MIT licensed. Weequery differs in a number of areas (the allow-list, null support, the query language, and the
wire format), but the idea was Superfilter's first.

## License

MIT.
