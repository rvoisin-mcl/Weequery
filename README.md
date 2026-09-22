# Weequery

*[steeples fingers]*

Gentlemen. Welcome. Sit down. **Sit. Down.**

You've all been asking me the same question. "Boss, how do I let the field agents filter the henchman
roster without me shipping a new build of the doomsday console every time somebody wants to sort by pay?"

Well. I've spent the last thirty years in a cryogenic freezer thinking about exactly this, and I've built a
"library" called **Weequery**.

It turns a filter that arrives as *data* from a form, from a query string, from a client, from a fax, I don't
care into a real `IQueryable` expression. You declare which properties may be asked about. Everyone else asks
about only those. It's an allow-list. It's beautiful.

- **Targets .NET 8 and .NET 10.** The eight is a courtesy to those of you still thawing out.
- **Works with Entity Framework Core.** Your conditions become SQL. Values go as parameters, not glued into the
  statement like some kind of *animal*.
- **Works in memory.** The same condition filters a `List<T>`.
- **Verified against SQLite, PostgreSQL and SQL Server.**

You, put your hand down.

## Contents

- [The plan](#the-plan)
- [Deciding what they may ask about](#deciding-what-they-may-ask-about)
  - [Values I supply, that they merely name](#values-i-supply-that-they-merely-name)
  - [The rules for keys](#the-rules-for-keys)
  - [Binding all of it, against my better judgement](#binding-all-of-it-against-my-better-judgement)
  - [Normalising what gets compared](#normalising-what-gets-compared)
  - [Forgetting a field instead of refusing it](#forgetting-a-field-instead-of-refusing-it)
- [Conditions](#conditions)
- [The query language](#the-query-language)
  - [One spelling per operator](#one-spelling-per-operator)
- [Operators](#operators)
  - [The "laser" `IsMatch` and `DoesNotMatch`](#the-laser-ismatch-and-doesnotmatch)
- [How nulls behave](#how-nulls-behave)
- [How strings compare](#how-strings-compare)
  - [What routes through it](#what-routes-through-it)
  - [Which half of the line it settles](#which-half-of-the-line-it-settles)
- [Reaching into a collection](#reaching-into-a-collection)
  - [A missing element is a null. Yes, again.](#a-missing-element-is-a-null-yes-again)
  - [Four places the brackets can go](#four-places-the-brackets-can-go)
  - [Where it runs](#where-it-runs)
- [Asking about all of them at once](#asking-about-all-of-them-at-once)
  - [The join you were going to write](#the-join-you-were-going-to-write)
- [Reading back only some of it](#reading-back-only-some-of-it)
  - [Asking for all of them, or all of one branch](#asking-for-all-of-them-or-all-of-one-branch)
  - [What a binding is *for*](#what-a-binding-is-for)
  - [Broad for reading, narrow for asking](#broad-for-reading-narrow-for-asking)
- [Sorting and paging](#sorting-and-paging)
  - [Or send me a string, if constructing objects is beyond you](#or-send-me-a-string-if-constructing-objects-is-beyond-you)
  - ["Showing 21 to 40 of 387"](#showing-21-to-40-of-387)
  - [A size for the ones who name none](#a-size-for-the-ones-who-name-none)
- [Sending a condition across the wire](#sending-a-condition-across-the-wire)
  - [Filter, order and select in one string](#filter-order-and-select-in-one-string)
- [The whole request, in one object](#the-whole-request-in-one-object)
- [Asking whether it will work, before finding out](#asking-whether-it-will-work-before-finding-out)
  - [Telling it what your backend cannot do](#telling-it-what-your-backend-cannot-do)
- [Without an IQueryable](#without-an-iqueryable)
- [Types I accept](#types-i-accept)
- [Things you will get wrong](#things-you-will-get-wrong)
- [The other end of the wire](#the-other-end-of-the-wire)
- [If you would rather have your own DTO](#if-you-would-rather-have-your-own-dto)
- [The same filter, against an index](#the-same-filter-against-an-index)
- [Or against an OData service](#or-against-an-odata-service)
- [What all this costs you](#what-all-this-costs-you)

## The plan

```csharp
using Weequery;

var minions = context.Minions
    .WithWeequery()
    .BindResolve()
    .RemoveBinding("Name") // Lets not make it too easy for the agents
    .ApplyCondition("(Pay > 10000) AND (IsActive = true)")
    .ApplySort(new Sort("Pay", SortDirection.Descending))
    .ApplyPagination(pageSize: 20, page: 0)
    .Build()
    .ToList();
```

`WithWeequery()` begins the scheme. `Build()` hands back an `IQueryable<T>` with everything applied. Nothing
executes until you enumerate it, so it composes with whatever else you had planned.

In a controller, the condition arrives from the caller a stranger, on the internet, typing. That is the 
entire point, and it is only alarming if you skipped the previous section:

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

That request is one you wrote. There is also one I wrote, which carries all four parts of a query and binds
straight off a query string see [the whole request, in one object](#the-whole-request-in-one-object).

## Deciding what they may ask about

This is the important part, so put down the shark food and *listen*.

A **binding** maps a **key** the name outsiders use to a property on your entity. Only bound properties can
be filtered or sorted on. Nothing else. Ever. If it isn't bound, asking for it gets a `WeequeryException` and a
very disappointed look from me.

```csharp
.BindProperty(minion => minion.Name)              // key defaults to "Name"
.BindProperty(minion => minion.Pay, "Salary")     // key is "Salary"
.BindProperty("Lair.Capacity")                    // a path, given as a string; key defaults to the path
```

Paths may go deeper, and may reach *into* a `Nullable<>`:

```csharp
.BindProperty("HireDate.Year", "HireYear")        // DateTime.Year
.BindProperty("BirthDate.Year", "BirthYear")      // DateTime?.Year
```

A selector can name such a path too, by going as far as the compiler will follow and passing the rest as
segments:

```csharp
.BindProperty(minion => minion.BirthDate, ["Year"], "BirthYear")   // same as "BirthDate.Year"
.BindProperty(minion => minion.Lair, ["Capacity"])                 // key is "Capacity"
```

Note that one: **given segments, the key defaults to the last of them; given the path as a string, it defaults to
the whole path.** Both are legal keys now, and the segments form kept its own default rather than change the key
on anyone already using it. Name the key if you want the other.

Why not just write `minion => minion.BirthDate.Year`? Because C# won't compile it against a `DateTime?`, and
`minion.BirthDate!.Value.Year` means something *different* naming `Value` **unwraps**, so you get a plain `int`
that has no null of its own and `IsNull` stops applying to it. Segments reach *through*. My father would have
found this fascinating. He invented the question mark. The details are unimportant.

### Values I supply, that they merely name

A binding can stand for a constant **your** code provides, under a name the caller may refer to but cannot set:

```csharp
.BindConstant("Threshold", payThreshold)
.ApplyCondition("Pay > [Threshold]")        // they name it, I decide what it is
```

Do you see it? *Do you see it?* The caller writes the *shape* of the question. I fill in the part they must not
see the per-tenant limit, the cutoff date, today's date, the number I am not telling them.

It reaches the database as a **parameter**, so the statement is identical whatever the value. It's the same for
every row, so sorting on it is refused. Obviously.

For a fixed set, declare it once and reuse it:

```csharp
static readonly BindingRequest[] MinionBindings =
[
    new(nameof(Minion.Name), null),                 // null key means "use the path"
    new(nameof(Minion.Pay), "Salary"),
    new(["Lair", "Capacity"], "LairCapacity"),      // segments, key defaults to the last one
];

query.WithWeequery().BindProperties(MinionBindings);
```

`BindProperties` resolves a given set once for the life of the process and keeps it, so calling it per request 
which is the normal thing to do costs a copy rather than a property lookup each time.

### The rules for keys

**They must be one or more valid unquoted SQL names, separated by periods.** A name is an ASCII letter or
underscore, then letters, digits or underscores. A period joins two of them, which is what lets a nested property
be bound under the path it already has.

```csharp
WeequeryException.IsBindingKey("Total_2");        // true
WeequeryException.IsBindingKey("Lair.Capacity");  // true
WeequeryException.IsBindingKey("my field");       // false
WeequeryException.IsBindingKey("Lair..Capacity"); // false, an empty segment is not a name
WeequeryException.IsBindingKey(".Capacity");      // false, nor is a missing one
```

Every segment is held to the whole of the rule, so a leading or trailing period, two in a row, and a segment
starting with a digit are all still refused. `IsBindingKey` is the rule a key is held to.

Which means a nested property needs no second name:

```csharp
.BindProperty("Lair.Capacity")                    // key is "Lair.Capacity"
.BindProperty("Lair.Capacity", "LairCapacity")    // unless you want the caller to see something else
```

A period is not a delimiter in the query language, so a dotted key is one word to the tokenizer and reads and
writes unquoted like any other: `Lair.Capacity > 500` parses, and comes back out as `([Lair.Capacity] > '500')`.

**Keys are case-insensitive** `pay`, `Pay` and `PAY` all find a property bound as `Pay`. Which
means two keys differing only in case are the *same key*, and binding both is a duplicate refused when the
second one is created.

**They may share a name with a keyword.** A key is written as a bare field name, so a key called `Contains`
makes a query that reads two ways, and I did not claw my way out of a Belgian orphanage to write an *ambiguous
parser*.

```csharp
.BindProperty(minion => minion.Notes, "Contains")    // throws. Obviously it throws.
```

The reserved words are the operator names `IsNull`, `IsNotNull`, `IsIn`, `IsNotIn`, `IsBetween`,
`IsNotBetween`, `StartsWith`, `DoesNotStartWith`, `EndsWith`, `DoesNotEndWith`, `Contains`, `DoesNotContain`,
`IsMatch`, `DoesNotMatch`, the words SQL spells its operators with: `AND`, `OR`, `NOT`, `IS`, `IN`, `BETWEEN`,
`NULL`, and the two words a combined string is split on, `OrderBy`, which a
[sort clause](#sorting-and-paging) reads as its own, and `Select`, which introduces a
[projection](#filter-order-and-select-in-one-string). Case does not matter.

Only the **whole** key is held to that, because only a whole word is ever read as an operator. `Lair.And` is one
word to the tokenizer and is not the conjunction, so it binds; `And` on its own does not.

### Binding all of it, against my better judgement

*[removes glasses]*

Some of you have models with sixty properties on them and you have been writing sixty `BindProperty` calls, and
one of you told me it was "tedious". Fine. **`BindResolve()`** walks the type and binds every readable property
it finds:

```csharp
context.Minions
    .WithWeequery()
    .BindResolve()                       // every property on Minion, keyed by its own name
    .ApplyCondition(request.Filter)
```

Now *look at me*. This is the allow-list saying **bind everything!**. Every property name on that entity goes on
the wire for a stranger to read and filter by, and if one of them is called `PasswordHash` or `InternalRiskScore`
or `TenantID`, you have just published it. That is a perfectly reasonable thing to do to an internal tool over a
model you own. It is not a thing to do to an entity with an audit trail hanging off it. **Resolve it once, print
it, and read what you got** before you ship it:

```csharp
foreach (var request in Inquiry<Minion>.ResolveBindables())
{
    Console.WriteLine($"{request.Key} -> {request.PropertyPath}");
}
```

**It goes one level down by default.** `BindResolve()` binds Minion's own properties and the properties of those,
keyed by their dotted paths. `BindResolve(maxDepth: 0)` stops at the entity, and you may go as deep as 16, which
is the ceiling rather than a suggestion:

```csharp
.BindResolve(maxDepth: 0)      // Name, Pay, Lair
.BindResolve()                 // ...and Lair.Name, Lair.Capacity
.BindResolve(maxDepth: 3)      // as far as you like, up to 16
```

**And it descends into collections**, which is what makes a quantifier work without your declaring one. Every
collection of objects it meets is bound twice over, under the one key: as a property, which is what an
[index](#reaching-into-a-collection) reads a single element out of, and as a collection, which is what answers
[`Any`, `All` and `None`](#asking-about-all-of-them-at-once). Two questions about one thing, under one name.

```csharp
.BindResolve(maxDepth: 0)      // Assignments Any (LairID = 5)
.BindResolve()                 // ...and Assignments Any (Lair.Name = 'Volcano')
```

**There is one depth, and a collection spends what is left of it.** The entity's **own** collections are free to
enter, so zero still reaches them and gives you the element's own properties, which on a link table is the pair of
ids and the two things they point at. One reaches through to the far side. That second hop is where it stops being
cheap: on a model with three collections it took the allow-list from 18 nameable keys to 124, because the far side
of a link table is the whole of another entity. Spend the depth where you want the second hop, and
*[read what you got](#and-reading-back-what-you-bound)*.

**That discount is the root's and nobody else's.** A collection further down costs the level it took to reach it,
like any other descent, so an element found at depth `d` is walked to `maxDepth - d - 1` and a collection the
budget does not reach is not bound at all:

```csharp
// Yard -> Bay -> Cartons -> Carton -> Stamp
.BindResolve(maxDepth: 1)      // nothing: 1 got the walk to Bay, and there is nothing left to enter Cartons with
.BindResolve(maxDepth: 2)      // Bay.Cartons Any (Code = 'X'), and no further
.BindResolve(maxDepth: 3)      // ...and Bay.Cartons Any (Stamp.Mark = 'X')
```

If it were granted at every level it would compound, each one paying for itself again, and a walk of 1 would drag
in a second hop nobody asked for. One discount, at the top, once.

Not every sequence is one. A `List<string>`, a `Dictionary<,>`, an array of numbers and a string itself all stay
ordinary bindings, because a quantifier names a *property of an element* and none of those has one worth naming.
[Index those](#reaching-into-a-collection) instead.

For "does this one have none", ask `Assignments None (...)` rather than reaching for a null test. A quantifier is
total: it answers the same for a collection that is absent as for one that is empty, which is the distinction a
database will not make for you anyway.

And if you want the property list with nothing entered, which is what this did before it descended, bind
`ResolveBindables` yourself:

```csharp
.BindProperties(Inquiry<Minion>.ResolveBindables())
```

**A model that refers back to itself terminates.** A type already open on the path is bound but not descended
into, so `Parent` is a key and `Parent.Parent` is not. Without that, a `Node` with two self-references resolved
half a million keys at depth 16 and took nine seconds and most of two gigabytes to bind them, *per request*, which
is the kind of thing that ends up being my fault.

**A property sharing a name with a keyword gets an underscore.** A key called `Contains` would make a query that reads
two ways, and refusing the whole model over one property name is no use to anybody, so it is keyed `Contains_`
instead. The path is untouched; only the name the caller uses changes. Nested ones are already unambiguous, so
`Inner.Contains` is left alone.

#### Taking things back out

Two ways, and you want the second one more often than you think.

`RemoveBinding` subtracts after the fact, which suits a model where the exceptions are fewer than the rules:

```csharp
.BindResolve()
.RemoveBinding("PasswordHash")
.RemoveBinding("Lair.SelfDestructCode")
```

<a name="and-taking-one-back-off"></a>
**It subtracts a use as readily as a whole binding**, which is the only thing in the library that revokes one:

```csharp
.BindResolve()
.RemoveBinding("Pay", BindingUse.Test | BindingUse.Sort)   // still readable, no longer testable
.RemoveBinding("PasswordHash")                                  // gone entirely
```

A binding left with no uses is removed entirely away rather than uselessly kept. Subtracting a use a binding never had
changes nothing.

A bound collection has no use of its own (it answers a quantifier, which is a condition, and nothing else) so
a subtraction including `Test` takes it away and one that does not leaves it alone.

**It takes the wildcards a projection takes**, and deliberately the same code decides what they match, so
`Lair.*` cannot come to mean one thing when you [read it back](#and-reading-back-what-you-bound) and another when
you remove it. A trailing `.*` is everything under a branch and *not* the branch itself:

```csharp
.RemoveBinding("Lair.*")     // Lair.Name, Lair.Capacity; Lair itself stays, still testable for null
.RemoveBinding("*")          // all of it, collections included
```

The dot is load-bearing. `"Lair.*"` cannot sweep in a key called `Lairyard`, which is exactly why the prefix keeps
it.

**And it reaches inside a collection**, in the spelling `ListBindings` reports. This is the *only* way to subtract
in there, an element's keys not being keys of the query:

```csharp
.RemoveBinding("Assignments[].*")            // nothing may be asked about an element any more
.RemoveBinding("Assignments[].Minion.*")     // the far side of the link table, and no more than that
.RemoveBinding("Assignments[].Lair.Name")    // just the one, no wildcard needed
```

Emptying the inside takes the collection with it, an empty allow-list being one no condition can satisfy. The
*property* binding is untouched either way, so the key is still there to be [indexed](#reaching-into-a-collection)
and tested for null, and only the quantifier goes. Which makes it the after-the-fact twin of
`IgnorePaths = ["Assignments[]."]`, and you want that one more often, for the reason below.

A wildcard that matches nothing is not an error here, though it is in a projection. A projection naming nothing
would quietly hand you nothing back and you would want to hear about it; a subtraction that subtracts nothing has
already done what it said it would.

`BindingResolutionSettings` subtracts *before*, which is better, because a path that was never resolved cannot be
forgotten about later. Start from `Standard` and use a `with`, or you will quietly drop the rules it already
carries:

```csharp
var settings = BindingResolutionSettings.Standard with
{
    IgnorePaths = ["PasswordHash", "Audit."],
    IgnoreTypes = [typeof(byte[])],
};

query.WithWeequery().BindResolve(maxDepth: 2, settings);
```

| Setting | What it does |
|---|---|
| `IgnorePaths` | whole paths to leave out, matched without regard to case. `"Lair"` drops the property **and** everything under it; **`"Lair."`**, with the period, binds Lair itself and stops there, so it can still be tested for null |
| `IgnoreTypes` | leave out any property of these types, matched on the declared type, so a `Nullable<>` is its own type |
| `IgnoreTypeWhenAssignable` | make `IgnoreTypes` catch anything assignable to one of them, so a base class or an interface covers everything under it |
| `DoNotExpandTypes` | bind a property of this type but do not descend into it, for a class you want reachable and testable for null without its insides going on the wire |

**To subtract inside a collection, name the collection first.** An element is walked from its own root, so a path
in there is reached by saying which collection it is in, using the same spelling
[`ListBindings`](#and-reading-back-what-you-bound) reports back at you, which is the point:

```csharp
IgnorePaths =
[
    "AssignmentsAsMember[].",                 // bind the collection, resolve nothing inside it
    "AssignmentsAsLeader[].Member",           // leave Member out of this collection's elements
    "LairAssignments[].Minion.",              // bind Minion there, and stop at it
    "LairAssignments",                        // take the name away altogether
]
```

The first of those leaves the property alone, so the key is still there to be
[null tested](#how-nulls-behave) and [indexed](#reaching-into-a-collection) and only the quantifier goes. That is
the same distinction the trailing period draws everywhere else, and it is why `"AssignmentsAsMember."` on its own
does nothing: a collection is never expanded as a navigation in the first place, so there is nothing for it to
stop.

A bare path such as `"Minion"` is still matched against the element's own paths, so it applies inside **every**
collection that has one rather than a named one. Both spellings subtract, so naming both takes both.

`Standard` subtracts nothing. It does not have to: the rules below are not settings and cannot be turned off.

**Where the walk stops on its own**, so bind these by hand if you want them:

- **At a struct.** `HireDate` is a key; `HireDate.Year` is not, because a `DateTime` is not a class. Same for an
  enum, and for the `Nullable<>` of either. Use [segments](#deciding-what-they-may-ask-about) to reach in.
- **At a container.** An array, a `List<>`, a `Dictionary<,>` and a `string` are bound and never entered. What is
  on the outside of one is bookkeeping: `Length`, `Rank`, `SyncRoot`, `Count`, `Capacity`. A `byte[]` used to
  resolve seven keys that way. What is *inside* one is reached by
  [indexing it](#reaching-into-a-collection) rather than by resolving every element, there being no end to those.
- **At an indexer.** `Chars` on a string and `Item` on a list have no path to bind, so they are skipped rather
  than left to fail when you came to bind them.

**What it steps over rather than stopping at:**

- **A struct with no builder.** A money type, a coordinate, a strongly typed ID: there is no way to compare one
  and no null to test, so the property is skipped. An unsupported *reference* type is different, and is bound as
  something you can test for null. One `Money` property on a model used to refuse the whole of it.

**And one it does follow that you might not expect:** an **interface**. What `IPlace` promises is reachable
through it, so `Place.Capacity` is resolved, and so is `Place.DisplayName` where `IPlace` inherits it from
`INamed`. A model that navigates by interface used to resolve nothing below it.

And it **adds** to what is already bound rather than replacing it, so a key you bound by hand and then resolve
again is a duplicate and is refused. Resolve first, remove after.

#### And reading back what you bound

Everything else here changes the allow-list. `ListBindings` merely reads it, which is how you carry out the
instruction I keep giving you: **read what you got**. A key, the path behind it, and what may be done with it.

**One flat list, and everything in it reads the same way.** A property of the entity and a property of something
one of its collections holds are both an entry, and the second is spelled with the collection it came through:

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

The empty brackets are where an element is chosen, and that is not decoration: it is the
[path you would bind by hand](#deciding-what-they-may-ask-about) with the choice left out.
`LairAssignments[0].Lair.Name` binds, and `LairAssignments[].Lair.Name` is the same path with nothing picked,
so the marker tells you exactly what is missing. Ask for it as it stands and the parser says so itself:

```
Expected an index for field 'LairAssignments' at position 15
```

**It is a name for the binding, not a condition you can write.** A condition takes one of two other shapes, and
the listing hands you what each needs. `ElementKey` is the short thing you write once a quantifier has already
said which collection, and `ElementOf` is that collection:

```csharp
// LairAssignments[].Lair.Name          Key          what the listing calls it
// LairAssignments                      ElementOf    the collection it is reached through
// Lair.Name                            ElementKey   what you write inside Any, All or None

.ApplyCondition("LairAssignments Any (Lair.Name = 'Volcano')")     // quantify over all of them
.ApplyCondition("LairAssignments[0] IsNotNull")                    // or name one element
```

A dotted path *after* an index is a binding and not a condition, so to ask about one element's far side, bind it
under a key of its own and then ask about that:

```csharp
.BindProperty("LairAssignments[0].Lair.Name", "FirstLair")
.ApplyCondition("FirstLair = 'Volcano'")
```

**This is the half of a resolved binding there is otherwise no way to look at**, and usually the longer half,
since the element of a link table drags the whole of another entity in behind it. That model lists **18** keys
off the entity and **106** more behind three of them. `LairAssignments[].Minion.Pay` is nameable and nobody asked
for it. This is how you find that out before your callers do.

A [collection](#asking-about-all-of-them-at-once) itself gets one entry and not two, though it is bound twice
over under the one name: you asked what a caller may write, not what I happen to keep. Every element reports
`Test`, because testing is all anyone does to one, and no element is itself a collection, an inner set having no
way to bind a further one.

`IsCollection` is the only thing that tells a collection apart: a quantifier is a condition, so `LairAssignments`
and `Name` both grant `Test` and the three uses separate them not at all.

It is a report and not a request.
comes out describes an allow-list that has already been built, including the parts of it no request could have made.


### Normalising what gets compared

A comparison has two sides and they come from different places: the **client** value the caller wrote, and the
**source** value the row holds. A converter folds one, the other, or both, so they agree about things they spell
differently:

```csharp
.BindProperty(minion => minion.Alias, "Alias",
    convert: ValueConverter.For<string>(alias => alias.ToUpper()))
```
```
Alias = 'ghost'      finds the minion stored as "Ghost"
Alias = 'GHOST'      so does this
```

`ConversionTarget` says which sides, and `Both` is the default:

| | folds | for |
|---|---|---|
| `Client` | what the caller wrote | a column already stored normalised fold the input to meet it |
| `Source` | what the row holds | a column callers write patterns against, where folding their text would break it |
| `Both` | both | neither side is normalised. The usual answer |

**It becomes part of the query**, which is why it takes an expression and not a delegate:

```sql
WHERE "m"."Alias" IS NOT NULL AND upper("m"."Alias") = @Value
-- @Value = 'GHOST'
```

The client half folded the parameter once while the query was built; the source half is `upper(...)` in the SQL.
A provider can translate `alias => alias.ToUpper()`; it cannot translate a call into your own code, and asking it
to fails when the query runs rather than when the binding is made.

> [!NOTE]
> A converted column is an expression rather than a column, so an index on it will not be used unless the
> database has one on that expression. Folding a million rows to compare against one value is the slow way round
> normalising the stored data once is the fast one. `Client` alone costs nothing.

Three things it deliberately leaves alone:

- **Null tests.** `IsNull` and `IsNotNull` ask whether there is a value at all, which no fold changes and the
  guard every other operator carries reads the raw property too. A converter cannot make a null look present.
- **Sorting.** An order should be the order of the real data.
- **Projections.** A projected row hands back what is actually stored.

The converter is declared for the **unwrapped** type, so a `DateTime?` property takes `ValueConverter.For<DateTime>`
and never has to think about the null the guard has already decided there is a value to fold. A converter whose
type does not match the property is refused where it is declared, not where a query using it fails.

Every value an operator holds goes through it: both ends of an `IsBetween`, every entry of an `IsIn`. Comparing
two properties folds each with its own. `BindConstant` takes one too.

One to think twice about: a converter on a string binding also folds the pattern of an `IsMatch`, and upper
casing a regular expression changes what its character classes mean. Use `Source` on bindings callers write
patterns against.

### Forgetting a field instead of refusing it

By default, a query naming a field nothing bound is refused, whole. `IgnoreUnboundFields` drops those parts and
runs the rest:

```csharp
context.Minions.WithWeequery(InquirySettings.Default with { IgnoreUnboundFields = true })
    .BindProperties(MinionBindings)
    .ApplyCondition("IsActive = true AND Gizmo = 3")   // Gizmo went away, so this filters on IsActive alone
```

The problem it exists for is the **stale saved filter**. A caller stored a query months ago, a binding has since
been renamed or removed, and refusing the whole thing means they cannot open their own saved view not even to
fix it.

**It is a setting and nothing else**, decided where the query starts and beside the rest of them. There is no
call further down the chain that turns it on or off, because leniency is a decision about the whole query and
one made halfway along is one that half the chain was built without.

> [!WARNING]
> **Dropping always widens.** A test that is not there does not constrain. So a condition made entirely of
> unbound fields prunes to nothing, and a query with no condition **returns every row**:
>
> ```csharp
> WithWeequery(lenient).BindProperties(MinionBindings).ApplyCondition("Gizmo = 3")   // every row, silently
> ```
>
> That is the whole hazard, and it is why this is off by default. If some rows are not the caller's to see, put
> that constraint on the `IQueryable` before Weequery gets it, or bind it as a constant and AND it in yourself.
> Never leave it to a filter string you are also willing to ignore.

It reaches all parts of a query, and the risk is not the same in each:

| | dropped field means |
|---|---|
| **Sort** | the rows come back in a different order. Harmless |
| **Projection** | a key is missing from the row. A projection whose fields *all* went reads back as a row of no columns not as all of them |
| **Condition** | **different rows.** This is the one to think about |

Inside a quantifier, the collection's own allow-list decides: an unbound field inside the brackets drops from the
inner condition, a quantifier left with no test at all drops entirely, and so does one naming a collection nobody
bound.

**A field bound without the use being asked of it does not go.** See
[What a binding is for](#what-a-binding-is-for): that is a deliberate statement about what a caller may do,
and quietly ignoring one would undo the point of making it. So is everything else that is wrong with a
query: malformed text, an operator that does not fit the property, a value that will not parse. All still
refused, lenient or not.

**One thing goes whether you asked for lenience or not**, and it is not really a lenience: a sort naming a
field that is bound but has nothing to order by, a constant or a type with no comparison. Applying it could
not have changed the order of anything, so dropping it gives the rows the caller would have had either way.
It is recorded like any other drop, with a reason saying which it was.

**And it does not have to be quiet.** `DroppedFields` says what the last build took out, so you can repair the
saved view rather than let it rot:

```csharp
var rows = inquiry.Build().ToList();

foreach (var dropped in inquiry.DroppedFields)
{
    // "'Gizmo' dropped from the condition, nothing bound it"
    Warn($"{dropped.Field} no longer applies and has been removed from your saved view");
}
```

Each entry carries the key and which part of the query lost it, as a `BindingUse`, `Condition`, `Sort` or
`Projection`. A key missing from two parts is two entries, because both are worth saying; missing twice from one
part is one. It is filled in **when the query is built**, not when it is enumerated, so it is ready as soon as
`Build()` returns. Each build resets it, so it describes the query you are holding rather than accumulating.

The name is the key with any index stripped: `Tallies[apples]` and `Tallies[pears]` both report `Tallies`, once,
because one binding is what is missing. Inside a quantifier the name is the element's, scoped to the collection's
own allow-list `Assignments Any (Loot = 3)` reports `Loot`.

## Conditions

A condition is a small tree. Build one in code:

```csharp
ICondition condition = new ConjunctionCondition(Operator.And,
[
    new OneValueCondition<decimal>(Operator.GreaterThan, "Salary", 10000m),
    new NotCondition(Operator.Not,
        new OneValueCondition<string>(Operator.Contains, "Name", "temp")),
    new NoValueCondition(Operator.IsNull, "FireDate"),
]);
```

There is one type per **shape**, and the operator decides which:

| Type | Operators |
| --- | --- |
| `NoValueCondition` | `IsNull`, `IsNotNull` |
| `OneValueCondition<T>` | `=`, `<>`, `<`, `<=`, `>`, `>=`, the substring family, `IsMatch`, `DoesNotMatch` |
| `TwoValueCondition<T>` | `IsBetween`, `IsNotBetween` |
| `MultipleValueCondition<T>` | `IsIn`, `IsNotIn` (up to 1000 values) |

So a condition that *exists* already holds the right number of values for what it does. An operator that doesn't
belong to the type is refused when you construct it, not three layers down when the query runs and everyone is
already in the escape pod. Use `ConditionFunctions.BuildComparison` when the operator isn't known until run time.

To compare a property against **another bound property**, hand the operand a binding key instead of a value:

```csharp
ICondition condition = new OneValueCondition<string>(
    Operator.LessThan, "HireDate", ConditionValue.Binding("FireDate"));
```

Any operand of any shape can be one, and they mix `Pay IsIn (8000, [Cap])` is a list holding a value *and* a
property. A key is a name whatever the property holds, so only a condition over `string` can carry one.

Both sides must be bound, so this exposes nothing new. They can compare two things I already let them see. That
is all. That is the whole freedom I have granted them.

There are fluent helpers, for those of you who find constructors upsetting:

```csharp
var conjunction = new ConjunctionCondition(Operator.And, []);

conjunction
    .AddIsGreaterThanTest("Salary", 10000m)
    .AddDoesNotContainTest("Name", "temp")
    .AddIsNullTest("FireDate");
```

Every helper that takes a value also takes a `ValueSource`, defaulting to `Raw`, so naming a property is one more
argument:

```csharp
conjunction
    .AddIsLessThanTest("HireDate", "FireDate", ValueSource.Binding)
    .AddIsBetweenTest("Salary", "10000", ValueSource.Raw, "Ceiling", ValueSource.Binding)
    .AddIsInTest("Name", [ConditionValue.Raw("Scott"), ConditionValue.Binding("Alias")]);
```

## The query language

`ApplyCondition(string)` parses a compact text form, which is far easier to send from a console than a tree.

```
(Pay > 10000) AND (IsActive = true)
Name StartsWith 'Al'
Pay IsBetween (8000, 12000)
Alias IsNull
Pay > [Threshold]                     brackets name a bound property
Pay IsIn (8000, [Ceiling])            and they mix
```

- **`AND` binds tighter than `OR`**, matching SQL and C#. Parentheses group freely.
- **Keywords, operator names and field names are case-insensitive.** `AND`, `and` and `And` are the same word.
- **Alternate spellings are accepted.** `==`, `!=`, `IN` and `BETWEEN` read and always will. `&&`, `||`, `!`,
  `IS NULL`, `IS NOT NULL`, `NOT IN` and `NOT BETWEEN` also read, and are deprecated, and I would rather you
  stopped. See [One spelling per operator](#one-spelling-per-operator) below, which is where that is going.
  `LIKE` is **not** supported use `Contains`, `StartsWith`, `EndsWith`.
- **Quote a value** when it contains a space, a delimiter, or looks like a keyword. `'single'` or `"double"`,
  and a literal closes on whichever quote opened it, so the other needs no escaping: `"it's"` and `'say "hi"'`
  both read as written. A backslash escapes a quote or another backslash and **only** those, so `'\w'` is the
  two characters it looks like. `''` and `""` are the empty string.
- **Values need no quotes** when they're simple: numbers, `true`, enum names, GUIDs, ISO dates and times.
- **Values are always parsed with the invariant culture**, so a query means the same thing on every machine, in
  every lair, on every continent.

`ToQuery()` is the inverse, and writes it back out.

### One spelling per operator

*[stands up]*

I have made a mistake, and I am going to own it in front of all of you.

When I built this I accepted every spelling anybody might reach for. `&&` and `AND`. `IsNull` and `IS NULL`.
`ORDER BY` and `OrderBy`. I thought I was being welcoming. What I was actually doing was making sure that no two
people writing the same question would write it the same way, and that *what came back out* depended on which
mood the writer was in, so nothing could be compared, cached by text, or read at a glance by whoever inherits
the console after the volcano is repossessed.

So there is now a third style, and it is called **Native**, and it is the one you want:

```csharp
condition.ToQuery(QueryStyle.Native);      // "(([Pay] > '10000') AND ([IsActive] = 'true'))"
```

**The rule is that an operator is one word.** That is the whole of it, and everything below follows from it.

- **`AND`, `OR`, `NOT`** for the conjunctions. Not `&&`, not `||`, not `!`. Those are punctuation, not words, and
  I am done pretending a wall of ampersands is readable at four in the morning.
- **No operator spelled with a space.** `IS NULL`, `IS NOT NULL`, `NOT IN` and `NOT BETWEEN` are all refused;
  write `IsNull`, `IsNotNull`, `IsNotIn`, `IsNotBetween`.
- **A range is a list.** `IsBetween (1, 5)`, never `IsBetween 1 AND 5` a word that is a separator here and a
  conjunction three tokens later is exactly what I am trying to get rid of.
- **`OrderBy`, not `ORDER BY`**, where a condition and a sort clause travel as
  [one string](#filter-order-and-select-in-one-string). A separator does not get an exemption for being a
  separator.
- **A one word alternate is still one word, so it still reads.** `IN` and `BETWEEN` are read as `IsIn` and
  `IsBetween`; `==` is read as `=`, and `!=` as `<>`. Nothing about them is ambiguous and refusing them would be
  fussiness rather than rigour. **What you get back is always the one form**, because the writer settles it:
  `=`, `<>`, `IsIn`, `IsBetween`. Read whatever they send you, hand back one thing.
- **Case still doesn't matter.** `isnull` and `IsNull` are the same word. This is about words, not about shouting.

**`QueryStyle.Native` is now the default everywhere something is written**, so `ToQuery()` and `ToString()` give
you the above without being asked. `QueryStyle.CSharp` and `QueryStyle.Sql` are marked `[Obsolete]`. They still
write exactly what they always wrote, and the parser still reads everything it always read. Deprecated is not
gone. Deprecated is me telling you where this is going while you still have time.

**Reading is where you opt in.** The parser stays permissive by default, because every spelling this language
ever accepted is sitting in somebody's saved filter and I am not detonating those from a minor version. Pass the
style to get the strict grammar:

```csharp
.ApplyCondition(request.Filter, QueryStyle.Native)          // refuses && and IS NULL
.ApplySorts(request.Sort, DefaultSort, QueryStyle.Native)   // refuses ORDER BY
ConditionFunctions.ParseQuery(text, QueryStyle.Native);
ParsedQuery.Parse(text, defaultSort, QueryStyle.Native);
Sort.Parse(clause, defaultSort, QueryStyle.Native);
transport.Unpack(QueryStyle.Native);
```

**Except [`QueryRequest`](#the-whole-request-in-one-object), which does not opt in because it is already there.**
Text arriving from outside is not text somebody saved before there was a settlement, so it is read as Native and
only as Native, and there is no style to pass it. The saved filters this permissiveness exists for are the ones
*you* hold; a caller typing a query today can be held to the one grammar.

Every refusal names the replacement, because what is being refused worked for years and the person who typed it
is not the one who changed the rules:

```
'&&' at position 9 is not valid in the Native style, write 'AND'
'IS NULL' is not valid in the Native style, write 'IsNull'
'ORDER BY' at position 11 is not valid in the Native style, write 'OrderBy'
```

## Operators

**Written** is what comes out, always. **Also read** is what else goes in and still reads under
`QueryStyle.Native`, being one word. **Refused by Native** is what only the permissive parser takes, being more
than one word. See [One spelling per operator](#one-spelling-per-operator).

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
| `Any` | `Any (...)` | | | | a bound collection, [quantified](#asking-about-all-of-them-at-once) |
| `All` | `All (...)` | | | | true of an empty collection |
| `None` | `None (...)` | | | | true of an empty collection |
| `And` | `AND` | | `&&` | | `ConjunctionCondition` |
| `Or` | `OR` | | `\|\|` | | `ConjunctionCondition` |
| `Not` | `NOT` | | `!` | | `NotCondition` |

A `bool` accepts only the null tests, equality and the `IsIn` family. Ordering is refused up front, rather than 
left to fail six layers down with a complaint about Boolean having no comparison operator. You're welcome.

### The "laser" `IsMatch` and `DoesNotMatch`

Right. These match a string against a regular expression, and they are the **only two operators that do not work
everywhere**, because standard SQL has no regular expression and every provider went off and did its own thing.

| Where | What you get |
|---|---|
| In memory | .NET's own `Regex`, bounded by `Inquiry<T>.MatchTimeout` |
| SQLite | `REGEXP`, which Microsoft.Data.Sqlite implements with .NET's `Regex` so it agrees with memory |
| PostgreSQL | the `~` operator, which is POSIX ARE, **not** .NET |
| SQL Server | **nothing.** The query fails when it is built |

```csharp
.ApplyCondition(@"Name IsMatch '^A\w+ Fox$'")
.ApplyCondition("Alias DoesNotMatch '^Gh'")
.ApplyCondition("Name IsMatch [Alias]")        // the pattern can come from the row
```

Write the pattern the way you'd write it anywhere else. `'\w'`, `'\d+'` and `'\p{Lu}'` all mean what they look
like.

Two things, and I want everyone awake for the second one.

**PostgreSQL is a different language.** Lookarounds, lazy quantifiers and named groups are .NET's, not POSIX's, so
a pattern using them can match different rows there than in memory. Stick to the common subset if the same
condition must answer the same on both.

**A pattern is caller input, and a regular expression can be made to cost far more than it looks.** Matching
`(a+)+$` against a few dozen non-matching characters takes time *exponential* in their number. Every other
operator is bounded by the size of the data. These are the two that let an outsider turn a search box into a
denial of service against *me* using nothing but punctuation.

So where Weequery runs the match, it bounds it. One second by default:

```csharp
Inquiry<Minion>.MatchTimeout = TimeSpan.FromMilliseconds(250);   // Regex.InfiniteMatchTimeout removes the bound
```

It's a static on the generic type, so it's set **per entity type**. Exceeding it raises
`RegexMatchTimeoutException` from wherever the query is being enumerated. The bound applies in memory only 
translated to SQL, the pattern is the database's problem and its own limits apply. It cannot be both: the
`Regex.IsMatch` overload carrying a timeout is not one any provider can translate, so building with it would stop
being a query and become a table scan on the client.

`DoesNotMatch` is the **negative operator**, not a negation. A row with no value matches *neither* it nor
`IsMatch`. `!(Alias IsMatch '^G')` is a different question and brings the null rows back with it. Which brings me
to my favourite subject.

## How nulls behave

**A null satisfies nothing except `IsNull`.**

Every other operator is built as *"the property has a value"* ANDed with the test on that value. So a null is not
caught by the negative operators either it is not "not equal to 5", it is **unknown**, exactly as a database
treats it.

Which means for any column, the rows matching an operator, the rows matching its negation, and the null rows
partition the table between them. One condition gives the same answer whether it runs against a database or in
memory. That is not an accident. That took me *considerable* time, during which nobody brought me coffee.

`Not` is the exception, because it negates the whole test the guard included so the nulls come back:

```
Alias <> 'Ghost'        every minion who has an alias, and it isn't Ghost
NOT (Alias = 'Ghost')   the same, plus every minion with no alias at all
```

Both are useful. Neither is normalised into the other. They are not interchangeable and if you confuse them your
report will be wrong and I *will* find out.

This extends to a property reached *through* a nullable. `BirthDate.Year` on a `DateTime?` is legal, and behaves
as a nullable in its own right even though `Year` is an `int`. `Lair.Name` where the minion has no lair matches
nothing, and `Lair.Name IsNull` asks whether the lair is there. A database answers that through the join, and
guarding it here is what makes the two agree.

## How strings compare

*[wheels in a second whiteboard]*

Every string operator compares **ordinally** by default: the characters that were stored, compared as they were
stored, case-sensitive, no linguistic opinion about any of them. That is the rule where Weequery is the one
comparing, and it is one of the two things a query may decide for itself:

```csharp
var settings = InquirySettings.Default with { StringComparison = StringComparison.CurrentCulture };

context.Minions
    .WithWeequery(settings)
    .BindProperties(MinionBindings)
    .ApplyCondition("Name StartsWith 'Al'")
    .Build();
```

Ordinal is the default for two reasons and the first is the one I actually care about.

**It is the answer a database would have given.** A column comparison happens in the database under the column's
collation, and no collation in common use quietly equates strings that differ in their characters. Ordinal keeps
the two halves of this library saying the same thing, which is the same promise the null handling makes one
section up, and it is not one I will give up for a default nobody asked for.

**And the other is that it is faster by an order of magnitude.** A linguistic comparison walks a collation table;
an ordinal one compares bytes. Over ten thousand rows that is 24us against 190us for the same filter, which you
may read in full in [BENCHMARKS.md](BENCHMARKS.md).

### What routes through it

All of it. This was previously a list of exceptions and now it is not:

| Asking | Built from | Compares how |
|---|---|---|
| `=` and `<>` | `string.Equals` | the setting |
| `<`, `<=`, `>`, `>=` | `string.Compare` | the setting |
| `IsBetween`, `IsNotBetween` | the two above | the setting |
| `StartsWith`, `EndsWith`, `Contains` and their negatives | the matching `string` method | the setting |
| `IsIn`, `IsNotIn` | `Contains` over the list | the setting |
| `IsNull`, `IsNotNull` | a null test | **nothing.** See below |
| `IsMatch`, `DoesNotMatch` | [`Regex`](#the-laser-ismatch-and-doesnotmatch) | its own pattern. `RegexOptions` is not this setting |

**The null tests are not comparisons and neither is an equality against a null.** What those ask is whether the
value is there at all, and no rule about comparing characters has an opinion about that. The guards every string
operator carries are the same shape, so they are left alone too.

**Case-insensitive matching is a comparison like any other**, which is to say it is this setting and not a
separate feature:

```csharp
InquirySettings.Default with { StringComparison = StringComparison.OrdinalIgnoreCase }
```

That is very often what somebody actually wanted when they asked for a culture. It is also the one worth knowing
is not the default: `Name = 'alice fox'` does **not** find Alice Fox until you say so.

### Which half of the line it settles

In memory. That is the whole of it.

Translated to a database this setting says **nothing at all** and cannot: the overloads carrying a
`StringComparison` are not ones any provider translates, so a condition built with them would stop being a query
and become a table scan on the client. The column's collation decides there, as it always did.

Under the default those two agree, which is the point of the default. Ask for anything else and you have
deliberately parted them:

```
Name = 'Alice'   against a stored value holding a soft hyphen, "Al\u00ADice"

  Ordinal        no match. Those are not the same characters and one of them is longer
  CurrentCulture a match. A soft hyphen is ignorable, so it compares as "Alice"
  a database     no match, under any collation you are likely to be using
```

Which is fine, and is sometimes exactly right a filter meant to read the way a person reads should be told to.
Just know which one you asked for, because the report that disagrees with the screen is otherwise very hard to
explain.

> [!WARNING]
> **`BuildExpression` is the translatable form, and compiling it yourself gets you neither rule.** It has to be:
> it is the shape handed to a provider. Compile it and you get the framework's own defaults for each method,
> which are not consistent with each other `StartsWith` and `EndsWith` compare linguistically while `Contains`
> and `==` are ordinal, which is the mess this setting exists to settle. Use
> [`BuildDelegate`](#without-an-iqueryable), which applies the rules, and pass the settings you meant.

## Reaching into a collection

Bind a list, an array or a dictionary, and a caller can ask about one element of it. The brackets say which:

```csharp
.BindProperty(minion => minion.Commendations)     // a List<string>
.BindProperty(minion => minion.Tallies)           // a Dictionary<string, int>
```
```
Commendations[0] StartsWith 'Order of'
Tallies[shark_maintenance] > 5
```

A list and an array index by position, a dictionary by key. The type is read off its interfaces rather than its
class, so a property declared as `IList<T>` or `IDictionary<K,V>` indexes exactly as the concrete one does. The
index is text and is read against the collection's key type when the query is built, the same way every other
value is.

### A missing element is a null. Yes, again.

*[leans forward]*

I know. But this is the part people get wrong, so we are doing it once more.

**An index nothing sits at is not an error and it is not a default value.** It is the absence of a value, and it
behaves exactly as a [nullable property](#how-nulls-behave) does. Three ways to be missing, and all three are the
same answer:

```
Tallies[pears]        the dictionary is there and has no such key
Commendations[9]      the list is there and is shorter than that
Tallies[apples]       there is no dictionary at all
```

So the same partition you already know:

| | matches |
|---|---|
| `Tallies[pears] IsNull` | every row, in all three cases above |
| `Tallies[pears] > 0` | nothing |
| `Tallies[pears] <> 99` | **nothing** the negative operators do not catch a missing element either |
| `NOT (Tallies[pears] = 99)` | **everything** `Not` negates the guard along with the test |

The collection being null is checked before it is asked anything, so reading an element of nothing never happens
rather than happening and throwing. And the collection itself is still a binding in its own right:
`Tallies IsNull` asks whether the dictionary is there at all.

### Four places the brackets can go

The one above is a **condition**, where the caller picks the index. There are three others.

**In a binding path**, where *you* pick it and the path carries on past the element. This is the one for
"whatever is in slot zero", where the caller never sees that there was a collection:

```csharp
.BindProperty("Assignments[0].LairID", "PrimaryLair")
.BindProperty("Tallies[shark_maintenance]", "SharkBudget")
```

Or with a selector, so the compiler checks the whole path for you. A list, an array and a dictionary all index
the way you would write them in any other C#:

```csharp
.BindProperty(minion => minion.Assignments[0].LairID, "PrimaryLair")
.BindProperty(minion => minion.Tallies["shark_maintenance"], "SharkBudget")
.BindProperty(minion => minion.Commendations[0], "TopCommendation")
```

**Give it a key.** An element is the one binding that cannot name itself: the key it would derive is
`Assignments[0]`, and that is the same text a condition writes to ask for element zero of a binding called
`Assignments`. Nothing could tell the two apart, so both are refused and you say what to call it. You were going
to choose that name anyway, since it is the one the caller sees.

**The index has to be written out.** `minion.Commendations[position]` over a variable is refused: a binding is
made once rather than per row, so it would freeze whatever `position` held and then read as though it followed
it. Write the number, or bind the collection and let the caller name the index.

A period inside brackets is not a separator, so a dictionary key may hold one: `Tallies[a.b]` is one step keyed
`a.b` rather than two steps and a broken key. And in the [segments form](#deciding-what-they-may-ask-about) a
bracketed segment is an index, where a bare one is still a property name: `["Assignments", "[0]", "LairID"]`.

**In an operand**, comparing something against an element:

```
Pay > [Tallies][shark_maintenance]
Tallies[shark_maintenance] > [Threshold]
```

**In a sort**, which orders nulls first as any nullable does:

```
OrderBy Tallies[shark_maintenance] DESC
```

All four end up at the same guarded element access, so they mean the same thing and fail the same way. Written
back out, an index is always the second pair of brackets: `([Tallies][apples] > '5')`, never
`'Tallies[apples]'`, because only the first of those reads back.

On the wire it is a member of its own, and absent where there is none, so a payload written before any of this
existed is byte for byte the payload it always was:

```jsonc
{ "Operator": 6, "Field": "Tallies", "Index": "apples", "Values": ["5"], "Conditions": [] }
```

### Where it runs

Like [`IsMatch`](#the-laser-ismatch-and-doesnotmatch), this is not the same everywhere, so here is the table
before you find out the hard way. It always works in memory.

| Against a database | What happens |
|---|---|
| A **primitive collection** a `List<string>`, an `int[]`, anything EF stores as a JSON array in one column | **Translates.** The guard becomes the provider's own length function and the read becomes its array access: `json_array_length`/`->>` on SQLite, `cardinality`/`[n]` on PostgreSQL, `OPENJSON`/`JSON_VALUE` on SQL Server. Values still go as parameters |
| A **navigation collection**, which is rows in another table | **Translates**, but read the warning below |
| A **`Dictionary<K,V>`** | **Nothing translates, because nothing maps.** EF has no mapping for a dictionary property, so the model will not build with one on it at all. Index a dictionary in memory, where it works exactly as described above, and not against a database |

PostgreSQL counts array positions from one. EF moves the index for you, so `Tags[0]` reaches the first element
there as it does everywhere else, and you do not think about it again.

**The navigation collection warning.** `Assignments[0].LairID` becomes a subquery taking a single row, and that
subquery has **no `ORDER BY`**. There is no such thing as the first row of a set, so "element zero" is whichever
row the database hands back first, which may differ between runs and between providers. It is a real answer to a
question nobody can quite ask. Index a primitive collection, whose order is the order it was stored in, or bind
what you actually meant and give it a name.

## Asking about all of them at once

Everything above picks **one** element. This asks about the elements as a set, which is the question you actually
had: *does this minion have an assignment to a lair called Volcano.*

Bind the collection with `BindCollection`, and say what may be asked about one of its elements:

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

`Any`, `All` and `None`. The parentheses are not decoration: without them the end of the inner condition would be
indistinguishable from the start of whatever follows it.

**Or have it resolved for you**, where writing the inside out by hand is not what you came here for. Leave the
action off and hand it a depth instead, and the element's allow-list is walked out of the element type by the
same [`ResolveBindables`](#binding-all-of-it-against-my-better-judgement) that walks the entity:

```csharp
.BindCollection(minion => minion.LairAssignments, "Assignments")
.ApplyCondition("Assignments Any (Lair.Name = 'Volcano')")
```

**The depth is counted from the element**, exactly as the other one counts from the entity, and it defaults to
the same 1. On a link table that is usually what you want: `0` binds the element's own columns, which for a row
that exists to join two things is a pair of ids and the two things they point at, where the default reaches
through to the far side and gives you the second hop for nothing:

```csharp
.BindCollection(minion => minion.LairAssignments, "Assignments", 0)   // LairID, MinionID, Lair, Minion
.BindCollection(minion => minion.LairAssignments, "Assignments")      // ...and Lair.Name, Lair.Capacity
```

And **the warning above applies here twice over**, because this opens the element type and, at the default depth,
the one past it. Everything either can reach becomes nameable inside the brackets. Read what you got, or declare
the inside by hand where the element is anything you would not publish. There is no `BindingUse` to set, an
element having none: it is tested, never sorted on and never read back.


**The inside is its own allow-list.** Binding the collection exposes nothing within it. `Assignments` on its own
is not a field a caller can compare, and `LairName` is not a field they can name outside the brackets. The two
lists cannot leak into each other, and a key cannot be a property on one and a collection on the other.

**The condition is scoped to one element**, and that is the whole point of it being a condition rather than a
list of tests. These are different questions and only the first one is usually what was meant:

```
Assignments Any (LairID = 5 AND IsPrimary = true)          one assignment that is both
Assignments Any (LairID = 5) AND Assignments Any (IsPrimary = true)   two assignments will do
```

Inside the brackets it is the ordinary language: `AND`, `OR`, `NOT`, every operator, nesting, the lot.

**It is never unknown.** This is the one bound condition a null cannot defeat. An empty collection and a missing
one are the same answer, because they are: no elements means no element matches.

| | empty collection | no collection at all |
|---|---|---|
| `Assignments Any (...)` | false | false |
| `Assignments All (...)` | true | true |
| `Assignments None (...)` | true | true |

So a quantifier and its negation partition your rows, rather than leaving some out of both. If you have read
[How nulls behave](#how-nulls-behave) you will appreciate how rare that is around here.

**In SQL this is the good case.** Indexing a collection asks a database for one element, which is awkward
everywhere; quantifying over one asks the question SQL was built to answer. It becomes an `EXISTS` subquery, and
the elements never leave the database:

```sql
SELECT "m"."MinionID", "m"."Name", "m"."Pay", ...        -- and the rest of the entity
FROM "Minions" AS "m"
WHERE EXISTS (
    SELECT 1
    FROM "LairAssignments" AS "l"
    WHERE "m"."MinionID" = "l"."MinionID" AND "l"."LairID" = @Value)
```

`All` and `None` come out as `NOT EXISTS`, which is also why they need no guard: a subquery that finds no rows is
false, so its negation is true, whether the collection was empty or absent. Verified on SQLite, PostgreSQL and
SQL Server.

On the wire it is the shape a condition already had, an operator, a field and one child, so nothing about the
payload format changed:

```jsonc
{ "Operator": 23, "Field": "Assignments", "Values": [],
  "Conditions": [ { "Operator": 6, "Field": "LairID", "Values": ["5"], "Conditions": [] } ] }
```

**What it will not do yet.** The elements have to be objects with properties to bind. There is no way to name the
element *itself*, so a `List<string>` cannot be quantified over: `Tags Any (Tag = 'urgent')` has nothing to call
`Tag`. Index it instead, or ask for the feature.

### The join you were going to write

Two shapes come up often enough to write out, because both arrive as a `JOIN` in your head and neither leaves as
one.

**A child table, filtered on one of its own columns.**

```sql
SELECT * FROM Minions
JOIN LairAssignments ON (Minions.MinionID = LairAssignments.MinionID) AND (LairAssignments.LairID = @id)
```

That `ON` clause is two halves and you write only the second. The first is the navigation, which the model
already knows about:

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

`MinionID` is never bound on the assignment and never named. It says how the two tables relate, which is not a
thing a caller is choosing.

**Two hops, through a link table, to the far side of it.**

```sql
SELECT * FROM Minions
JOIN LairAssignments ON (Minions.MinionID = LairAssignments.MinionID)
JOIN Lairs ON (Lairs.LairID = LairAssignments.LairID) AND (Lairs.Name = @name)
```

Still one condition. The second hop is a property path bound *inside* the collection, which is the `LairName`
binding from the top of this section and nothing more:

```csharp
    .BindCollection(minion => minion.LairAssignments, "Assignments", inner => inner
        .BindProperty(assignment => assignment.LairID)
        .BindProperty(assignment => assignment.Lair.Name, "LairName"))
    .ApplyCondition("Assignments Any (LairName = 'Volcano')")
```

Both of your joins turn up inside the `EXISTS`, the link table and the lair it reaches through to:

```sql
WHERE EXISTS (
    SELECT 1
    FROM "LairAssignments" AS "l"
    INNER JOIN "Lairs" AS "l0" ON "l"."LairID" = "l0"."LairID"
    WHERE "m"."MinionID" = "l"."MinionID" AND "l0"."Name" = @Value)
```

`"l0"` is the second table your `JOIN Lairs` was reaching for. Bind `LairID` beside `LairName`, because a caller
matching a lair by id needs no `Lairs` at all and gets a query one join shorter for it, which is the first
statement above.

**The same again, where both ends are the same table.**

A link table does not have to lead anywhere new. A minion who runs other minions is those same two hops with
`Minions` on both ends of them:

```csharp
class Minion { Guid MinionID; string Name; List<Report> Reports; }
class Report { Guid BossID; Minion Boss; Guid UnderlingID; Minion Underling; }
```

```sql
SELECT * FROM Minions
JOIN Reports ON (Minions.MinionID = Reports.BossID)
JOIN Minions AS Underlings ON (Underlings.MinionID = Reports.UnderlingID) AND (Underlings.Name = @name)
```

Nothing about the binding changes. The second hop is a property path inside the collection, and it does not care
that where it lands is the type it set out from:

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

`"m0"` is your `Underlings` alias and `"m"` is the minion it is being asked about, which is the case this shape
exists to keep straight. The two allow-lists do that for you: `Name` outside the brackets is the boss, and
`UnderlingName` is nameable only inside them, so one condition can ask about both and mean a different row each
time.

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

The one thing to watch is EF's rather than this library's: a self reference has to have both its navigations
configured, since nothing can guess which of two `Minion` properties on `Report` is the inverse of the
collection. And an `Include` down a self reference loads the same entity type as both parent and child into one
change tracker, which is a graph rather than a list and worth knowing before you serialize it.

**Each minion comes back once**, however many of their assignments point at something that matches. Two
assignments to lairs called Volcano and the `JOIN` hands you that minion twice; this hands them to you once. With
a link table in the middle that is not an edge case, it is the usual shape, and the `DISTINCT` you were going to
have to remember is not needed.

**What you do not get is the other table's columns.** `SELECT *` across a join reads the joined rows too, and an
`Inquiry<Minion>` reads minions. That half is EF's, and it composes:

```csharp
context.Minions
    .Include(minion => minion.LairAssignments)
    .ThenInclude(assignment => assignment.Lair)
    .WithWeequery()
    ...
```

The filtering stays an `EXISTS` and the `Include` adds its own join beside it. It loads every assignment of a
matching minion rather than only the matching ones, so `Include(minion => minion.LairAssignments.Where(...))`
where that is what you meant.

## Reading back only some of it

Everything so far decides **which rows**. This decides **which columns**, and I want you to notice that those
are two entirely different questions:

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

**The allow-list is the same one.** I do not keep a second list. Anything bound is projectable, under the same
keys and the same case-insensitive matching, and a field nobody bound is refused exactly as it is in a
condition. There is nothing extra for you to declare, and this grants nothing that filtering had not already
granted.

**It is a narrower SELECT**, not a whole row hauled across the wire and then thrown away, which is the entire
point and the only reason I bothered:

```sql
SELECT "m"."Name", "m"."Pay"
FROM "Minions" AS "m"
WHERE "m"."IsActive" = @Value
```

Verified on SQLite, PostgreSQL and SQL Server, because I do not take a provider's word for anything. Three
columns of a wide table, over a page of twenty, is a very different amount of work from twenty whole rows, and
the difference is yours to keep.

### Asking for all of them, or all of one branch

`*` reads every field the caller may read, and `Prefix.*` reads every one under that prefix:

```csharp
.ApplyProjection("*")                    // everything this caller is allowed
.ApplyProjection("Lair.*")               // Lair.Name, Lair.Capacity, Lair.Address.City
.ApplyProjection("Name, Lair.*")         // one field and a branch, in that order
```

**A wildcard names the allow-list. It does not go around it.** Whatever it expands to is still only what grants
`Projection`, so a field bound `Test`-only is no more readable through `*` than it is by name. Nobody gets in
through the star. A key caught twice, by name and by a wildcard, is read once, in the place it was first asked
for.

**The prefix keeps its dot.** `Lair.*` matches `Lair.Name` and does not match `Lairyard.Capacity`, which is
precisely the sort of thing that is otherwise discovered in production, at speed, by someone else.

A prefix matching nothing behaves like any other unbound field: refused, or dropped and recorded where
[`IgnoreUnboundFields`](#forgetting-a-field-instead-of-refusing-it) asked for that.

```
'Gizmo.*' matches nothing: no binding under 'Gizmo.' can be projected
```

> [!NOTE]
> `*` is not the same as sending no projection at all. Both read every allowed field, but naming one is still
> naming one, so the DTO companions still refuse it as [two answers to one
> question](#if-you-would-rather-have-your-own-dto), and `AppliedProjection.IsEmpty` is false.

The same two wildcards work in [`Weequery.OData`](#or-against-an-odata-service) and
[`Weequery.Elasticsearch`](#the-same-filter-against-an-index), where they expand against the field set you
declared rather than against bindings. A declared field is a readable one out there, there being no
`BindingUse` on that side of the wire.

Now. A few things you are going to want to know, and which I would rather tell you here than explain later:

| | |
|---|---|
| **Keys are the binding's spelling** | Ask for `name`, `NAME` or `Name` and all three come back as `Name`. Two callers typing it differently get the same shape, which is what anything deserializing it needs |
| **Order is the order asked** | `"Pay, Name"` and `"Name, Pay"` differ in the order the entries go in |
| **A field named twice is kept once** | A dictionary holds each key once, so there is nothing a duplicate could possibly mean. Assemble the list from checkboxes and stop worrying about it |
| **Values are boxed** | `object?`, whatever the property held, and null where the value is null or the path to it runs through one. A nullable value type boxes to null rather than to its default |
| **Nothing asked for is everything allowed** | `BuildProjected()` with no projection reads every bound field, which is the allow-list's own answer to "all of it" |
| **A constant projects its value** | The same for every row, see [Values I supply](#values-i-supply-that-they-merely-name) |
| **A collection does not project** | It holds many values and a column holds one. Ask about its elements with [a quantifier](#asking-about-all-of-them-at-once) instead |

An indexed field projects, so `Tallies[apples]` reads that one element and comes back keyed `Tallies[apples]`. An
index nothing sits at is a null, [as always](#a-missing-element-is-a-null-yes-again). I am consistent about this
to the point of tedium.

`ApplyProjection` will also take the keys directly, for the caller who has them in hand rather than in a string:

```csharp
.ApplyProjection(["Name", "Pay"])
.ApplyProjection(Projection.Parse(request.Fields))
```

Called twice, the last one wins. A projection is one list of columns rather than something that accumulates, so
two calls asking for different columns can only mean the second one changed its mind, and I side with the more
recent decision.

**Paged, both halves work together:**

```csharp
var (page, total) = query.WithWeequery()
    .BindProperties(MinionBindings)
    .ApplyCondition(request.Filter)
    .ApplySorts(request.Sort, DefaultSort)
    .ApplyPagination(request.PageSize, request.Page)
    .ApplyProjection(request.Fields)
    .BuildPagedProjected();
```

Only the page is projected. The count is over rows rather than over what is read off them, so it gives the same
total either way: a projection decides what a row *says*, not which rows there are. Those are, once again, two
different questions.

### What a binding is *for*

*[turns to the whiteboard]*

A binding grants three separable things: filter on it, sort on it, read it back. They are very much not always
wanted together, so say which:

```csharp
.BindProperty(minion => minion.Name)                                       // all three, the default
.BindProperty(minion => minion.Notes,  "Notes",  BindingUse.Projection)    // read it back, and nothing else
.BindProperty(minion => minion.OwnerID,"Tenant", BindingUse.Test)     // filter on it, never show it
.BindProperty(minion => minion.Morale, "Morale", BindingUse.Test | BindingUse.Sort)
```

| | `Notes` | `Tenant` | `Morale` | `Name` |
|---|---|---|---|---|
| `Notes Contains 'shark'` | refused | | | |
| `OrderBy Notes` | refused | refused | | |
| `ApplyProjection("...")` | | refused | refused | |
| `Name = [Notes]` | **refused** | | | |

Two cases this exists for, and both of them have cost somebody dearly. A free text note that nobody should be
running `Contains` against, because it is unindexed and the table is large: worth returning, not worth
interrogating. And a tenant or owner column that **must** filter and must **never** be handed back.

The operand row is the one to point at. `Name = [Notes]` looks like a comparison and is in truth a **read** of
`Notes`. Allow it and a patient caller learns the column by bisection, one query at a time, which is exactly how
I would do it. So `Test` grants both sides of an operator or neither. There is no way to split them and no
sane reason to want to.

What a narrowed key is not is **unbound**. It works perfectly well somewhere else, so the refusal says what it
is actually for rather than sending you off hunting for a typo that was never there:

```
'Notes' cannot be used in a condition: it is bound for Projection
'Morale' cannot be projected: it is bound for Test, Sort
```

`BindingUse` is a bitflag: `None`, `Test`, `Sort`, `Projection`, and `All` for the three together, which is the
default everywhere. It rides on `BindProperty` in all its forms, on `BindConstant`, and on `BindingRequest` for
a whole set:

```csharp
new BindingRequest(nameof(Minion.Notes), "Notes", BindingUse.Projection)
```

### Broad for reading, narrow for asking

Here is the shape this is really for, and the one I would use. `BindResolve` takes a use as well, so you may let
the whole model be *read* and then grant the handful of fields a caller may filter and sort on:

```csharp
.BindResolve(use: BindingUse.Projection)                              // everything readable
.BindProperty(minion => minion.Name, use: BindingUse.Test)       // ...these two also askable
.BindProperty(minion => minion.Pay,  use: BindingUse.Test | BindingUse.Sort)
```

`Name` ends up `Projection | Test`, and `Pay` ends up all three. **Binding the same property twice adds to what
it may be used for rather than replacing it**, so the second call grants and the order of the two does not
matter in the slightest. Everything the second pass did not name stays readable and stays unaskable, which is
exactly where I want it.

> [!WARNING]
> **A use is only ever added, never taken away.** That is what makes the pattern above compose, and it cuts the
> other way too: a narrow binding does not survive a broad call after it.
>
> ```csharp
> .BindProperty(minion => minion.Pay, use: BindingUse.Projection)
> .BindResolve()                        // grants All to everything, including Pay
> ```
>
> Pay is now filterable. If that is not what you meant, narrow the resolve with `BindResolve(use:
> BindingUse.Projection)`, put it first, or take the use back off afterwards with
> [`RemoveBinding`](#and-taking-one-back-off), which is the only thing in this entire library that revokes.

**A [`ValueConverter`](#normalising-what-gets-compared) is granted the same way a use is.** A bind that never
mentioned one is not asking for there to be none, so the call that names it wins, and it does so either way
round. That is what lets a resolved set be given one afterwards, `BindResolve` carrying no converters of its
own:

```csharp
.BindResolve(use: BindingUse.Projection)
.BindProperty(minion => minion.Name, convert: Upper, use: BindingUse.Test)   // Name is now folded
```

**Two different converters are the exception, and they are refused.** There is no merging `TOUPPER` with
`TOLOWER`, and picking one quietly would leave you reading values that had been through a conversion nobody
asked for, which is how people end up doubting their own data:

```
'Name' is already bound with a different ValueConverter. One key cannot mean two normalisations of the same
property, so bind it once with the converter it should have
```

> [!NOTE]
> Different means *not the same object*, not "does not do the same thing". A converter wraps a delegate, so two
> built from the same lambda cannot be shown to agree and count as two. Hold the converter in a field and pass
> that, rather than writing `ValueConverter.For<string>(...)` twice and hoping.

A projection that names nothing reads everything granting `Projection`, and nothing that does not. "All of it"
means all of what **this caller** may read, which is a rather smaller thing than all of it, and deliberately so.

`BindConstant` grants `Test | Projection`, since a constant is the same for every row and there is nothing there
to order by. A sort naming one is dropped rather than refused, and says *"it is a constant and has one value for
every row"* in [`DroppedFields`](#forgetting-a-field-instead-of-refusing-it), the order being identical with the
clause or without it.

**On the wire** it is one more member on `TransportCondition`, absent where nobody asked, so a payload written
before any of this existed is the payload it always was. I do not break things that were working:

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

Sorts apply in the order given, each breaking ties in the one before. Sort fields must be bound. Nullable
properties sort nulls first. A field with no ordering of its own a collection, a navigation property is
**dropped** rather than refused: it can be bound and tested for null, but there is no such thing as its
first row, so there was never an order for the clause to impose. It turns up in
[`DroppedFields`](#forgetting-a-field-instead-of-refusing-it) with the type named.

Sort on *one element* of it and there is: `OrderBy Tallies[shark_maintenance] DESC` orders by a number, and the
rows with no such element sort as the nulls they are. See
[reaching into a collection](#reaching-into-a-collection).

### Or send me a string, if constructing objects is beyond you

Sorts have their own little language, separate from the condition one, so a caller sends the two apart and
neither has to know about the other:

```csharp
.ApplySorts("Salary DESC, Name")
```

A comma separated list of fields, each optionally followed by a direction. Leave the direction off and it runs
ascending, as it does in SQL. `Asc`, `Ascending`, `Desc` and `Descending` are all accepted, case is ignored, and
you may open with `OrderBy` if it makes you feel more professional:

```
Salary                              ascending, which is what a field on its own means
Salary DESC
Salary Descending, Name             several, applied in the order written
OrderBy Salary DESC, Name ASC       the same clause, opened with the separator
[Lair.Capacity] DESC                a field written the way a condition writes one
```

`ORDER BY` is two words, so `QueryStyle.Native` refuses it and takes only `OrderBy`, the same rule it applies to
the operators. Pass the style to hold a caller to it:

```csharp
.ApplySorts(request.Sort, DefaultSort, QueryStyle.Native)
Sort.Parse(clause, defaultSort, QueryStyle.Native);
```

A field genuinely named `Order` is unaffected either way, since it was only ever the two words *together* that
made a prefix.

**The second argument is the one that saves you.** It is what to sort by when the caller asked for nothing:

```csharp
.ApplySorts(request.Sort, [new Sort("MinionID", SortDirection.Ascending)])
```

`Sort.Parse(clause, defaults)` does the same thing without a query attached, if you want the list itself.

**It writes back out**, so a clause survives a round trip intact. `ToQuery()` settles on one spelling the field
bracketed, the direction always stated, always short and unlike a condition the trip is *exact*: a sort carries
no value to be read back against a property's type, so what goes out comes back identical.

```csharp
sorts.ToQuery();                    // "[Salary] DESC, [Name] ASC"
```


**Page with a sort.** Paging without one is accepted and the contents are arbitrary, which means page two may
contain the same henchman as page one, and that henchman may be a *plant*. Sort on something that breaks every
tie:

```csharp
.ApplySorts([
    new Sort("Salary", SortDirection.Descending),
    new Sort("MinionID", SortDirection.Ascending),  // nothing ties on this, so the order is total
])
.ApplyPagination(pageSize: 20, page: 2)
```

### "Showing 21 to 40 of 387"

The 387 is not something a page can be asked for. It is the size of the set the window was cut out of, so it is
a second query, and `BuildPaged()` builds it for you alongside the first:

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

What comes back is a **`PagedQuery<T>`**, which is two queries and a deconstructor:

```csharp
public record PagedQuery<T>(IQueryable<T> Page, IQueryable<int> Total);
```

`Page` is exactly what `Build()` would have handed you: every condition, then every sort, then the window.
`Total` is how many rows the conditions matched, as a query of one number. It carries the filter and nothing
else, because nothing about a count depends on the order and asking a database to sort rows it is about to throw
away is work performed for *no one*. Take them apart with `var (page, total) =` as above, or keep the record and
read the two off it.

**The second query asks for a number, not for rows.** No column of the entity is anywhere in it, so nothing of a
row crosses the wire:

```sql
SELECT COUNT(*)
FROM (
    SELECT 1 AS "Key"
    FROM "Minions" AS "m"
    WHERE "m"."Pay" > @Value
) AS "m0"
GROUP BY "m0"."Key"
```

The nesting is not decoration, and it is not something I would have chosen. LINQ has no scalar query: `Count()`
is a terminal operation, so there is no `IQueryable` that *is* a count, and nothing to hold back and hand to you.
A grouping on a constant is the only count that stays a query, every row lands in the one group, and every
provider tested folds it into a single `COUNT` over the filtered set.

**Read it with `FirstOrDefaultAsync`.** This is the part to get right, because the wrong endings compile:

| | |
|---|---|
| `FirstOrDefaultAsync()` | the total, and `0` where nothing matched, which is the right answer |
| `SingleAsync()` | the total, but it throws where nothing matched: a grouping over no rows is no group, so the query comes back with no row at all |
| `CountAsync()` | `1`. It counts the rows of the count query, and it is the one to watch for: it compiles, it looks like the obvious thing to write, and the number it gives back is wrong quietly |

**Neither of them has run.** I do not execute them for you, and I want to be clear that this is a decision and
not an oversight. The method that reads a number without tying up a thread is `FirstOrDefaultAsync`, which
belongs to Entity Framework Core, and this library depends on nothing. Not one thing. I am not surrendering that
to save you a line. So you get two queries, you execute them with whatever you were going to execute them with,
and the promise `Build()` makes, that nothing happens until you enumerate, survives intact.

Read `total`. Not the length of `page`. The length of the page tells you how long the page is, which you knew,
because you asked for it.

**It is two round trips**, and there is no arrangement of this that makes it one. A `COUNT` and a `SELECT` are two
statements whoever writes them. What you are buying with the second one is the total; if a screen does not show a
total, do not ask for one, use `Build()` and be done.

**The page still composes.** It has not run, so it takes a `Select` on the way to whatever you are actually
returning:

```csharp
var (page, total) = query.BuildPaged();

var matched = await total.FirstOrDefaultAsync();
var rows    = await page.Select(minion => new MinionSummary(minion.Name, minion.Pay)).ToListAsync();
```

`Total` does not, and that is the trade. It is an aggregate over the filtered rows rather than the rows
themselves, so there is nothing left in it to compose against. Where the count wants a different filter from the
page, that is a second Inquiry.

**Without `ApplyPagination` there is no window**, so `Page` is the whole filtered, sorted result and the count
agrees with its length. That is not an error, but it is a round trip asking a question the rows in your hand
already answer.

### A size for the ones who name none

*[taps the whiteboard twice]*

The sentence above is the problem. "Without `ApplyPagination` there is no window" means a caller who leaves the
page size off their request gets **every row in the table**, and that is a thing they can do by accident, on a
phone, on a train, against the henchman roster of a multinational criminal enterprise.

So set a default and the sense of it inverts:

```csharp
var settings = InquirySettings.Default with { DefaultPageSize = 50 };

context.Minions
    .WithWeequery(settings)
    .BindProperties(MinionBindings)
    .ApplyCondition(request.Filter)
    .Build();                              // fifty rows. Nothing here mentioned paging
```

**Every query built off those settings is windowed**, including the one that never called `ApplyPagination` at
all. That is the whole point: a caller cannot ask for the table by omitting a field.

A caller who *does* name a size still gets it, so this is the floor under the ones who say nothing rather than a
ceiling over the ones who speak up. And a caller may now name a page and leave the size to you:

```csharp
.ApplyPagination(pageSize: null, page: 2)   // page two, fifty at a time
.ApplyPagination(pageSize: 0,    page: 2)   // the same. A page of nothing is not a request, it is a blank field
.ApplyPagination(pageSize: 200,  page: 2)   // page two, two hundred, because they asked
```

**A size that could not hold a page is no size at all.** Null, zero and a negative all mean the same thing, and
that thing is "the caller did not say". They arrive here off a query string, where a field left out and a field
left at zero are the *same accident*, and answering one with a page and the other with a 400 would be a
distinction the person typing it never knew was there.

**And a page behind the first one is the first one**, for the same reason and from the same direction. There is
nothing back there to be asked for, so `page: -1` is the front of the list rather than an exception:

```csharp
.ApplyPagination(pageSize: 50, page: -1)    // the first page. It was always going to be the first page
```

Between them that is every value either argument can take, and none of them throws. Which leaves exactly one
refusal in this whole business, and it is not a value, it is a **pair**:

```csharp
.ApplyPagination(pageSize: 1000, page: int.MaxValue)   // throws
```

A thousand rows a page, two billion pages in, is a row number that will not fit in the `int` a `Skip` takes.
Unchecked it wraps to a negative skip and *quietly answers with the first page*, which is the worst of all
possible outcomes: a wrong answer wearing a right one's clothes. And there is nowhere sensible to fold it to
the first page is not what was asked for, and the last page is not knowable without running the query so it is
the one thing here that is refused rather than read charitably.

> [!WARNING]
> **It is not a cap.** `PageSize = 1000000` is a caller asking for a million rows and it will get a million rows.
> If that matters and it usually does clamp the number on the way in, somewhere you can say why in the
> refusal. A default is for the request that forgot; a limit is for the request that did not.

`null` is the default default, which is the behaviour every query had before this existed: no window, all the
rows, exactly as described above.

**The one place zero is still refused is the setting itself**, and that is not an inconsistency, it is the two
ends of the same number being written by two different people. `DefaultPageSize = 0` is *you*, once, in your own
startup, where a zero is a typo and you want to hear about it in the first second of the first run. `pageSize=0`
is a stranger, on the internet, filling in a form. One of those deserves an exception. The other deserves a page
of minions.

## Sending a condition across the wire

Two ways to move a condition between processes. Or continents. Or hollowed-out volcanoes.

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

Both round trips preserve **meaning, not types**: values travel as text and come back as strings, which the
expression builder parses against the bound property's type when the query is built. Formatting is invariant and
round-trippable, so a `DateTime` keeps its sub-second precision and its `Kind`.

Every operand says whether it is a value or the key of a bound property. A value is written as the value alone,
and only a key carries a `Source` so an ordinary condition costs nothing extra:

```jsonc
// Name IsIn ('Alice Fox', [Alias])
{ "Operator": 10, "Field": "Name", "Conditions": [],
  "Values": [ "Alice Fox", { "Source": 1, "Value": "Alias" } ] }

// Name IsIn ('Alice Fox', 'Bob Samuelson')
{ "Operator": 10, "Field": "Name", "Conditions": [],
  "Values": [ "Alice Fox", "Bob Samuelson" ] }
```

Nothing is guessed from the text. The two are told apart by the **shape** they arrive in a string against an
object which no value can be mistaken for whatever it happens to spell. There is nowhere for a key to arrive as
bare text and be compared against as though it were one. I thought of everything. I always think of everything.

### Filter, order and select in one string

For the callers who cannot manage three fields on a form, `ParsedQuery` carries all of them, each part
introduced by its own word:

```csharp
var (condition, sorts, fields) = ParsedQuery.Parse("Pay > 10000 OrderBy Pay DESC Select Name, Pay", defaultSort);

query.ApplyCondition(condition).ApplySorts(sorts).ApplyProjection(fields);
```

It deconstructs, as you see, into three, or into two where you never asked about a projection. Every part may be
left out: no separator at all means the whole string is a condition, a leading separator means that part and no
filtering, and the `defaultSort` stands in wherever no sorts were named.

```
Pay > 10000 OrderBy Pay DESC Select Name, Pay   all three
Pay > 10000 Select Name, Pay                    a condition and a projection, and no sorting
OrderBy Pay DESC Select Name                    sorts and a projection, and no filtering at all
Select Name, Pay                                a projection, and nothing else
Pay > 10000                                     a condition, and whatever default sort was given
```

It writes back out too:

```csharp
parsed.ToQuery();       // "([Pay] > '10000') OrderBy [Pay] DESC Select [Name], [Pay]"
```

**The order is fixed**, condition then sorts then projection, because where a part sits is the only thing saying
which part it is. `Select Name OrderBy Pay` is refused rather than quietly put back in order.

If you are typing a combined query yourself, the separators are mandatory. Not encouraged. Not a nice touch.
Mandatory. They are the only thing standing between your filter, your sort and your columns, and without them
there is no line, there is just one long condition and a very expensive silence.

Under `QueryStyle.Native` the sort separator is `OrderBy` and only `OrderBy`, one word like everything else, and
that is also the spelling `ToQuery(QueryStyle.Native)` writes so a combined string survives its own round trip.
`Select` is one word in every style, so there was never a second spelling of it to refuse.

**A `Select` with nothing after it is refused**, rather than read as the projection that names no fields. That is
a real thing and it is what leaving the word off already says, so somebody who typed the word and then stopped
meant to name a column and should hear about it.

A binding may not be named `Select`, for the same reason none may be named `OrderBy`: where a part could begin,
the word is read as the separator. It is in the [reserved words](#the-rules-for-keys) with the rest.

And where each split falls is decided by *reading* the part before it and seeing where it stops, not by hunting
the text for the words. So `Name = 'Select'` is one comparison and no projection, which is the sort of detail
that separates a working scheme from a *smouldering crater*.

## The whole request, in one object

A query arrives as two things: what to ask, and which page of the answer to take. `QueryRequest` is both, and
every member of it is a scalar, so the whole thing binds off a query string without you writing a model
binder or a parameter per part:

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

| Member | Is | Reads with |
|---|---|---|
| `Query` | the whole query as [one string](#filter-order-and-select-in-one-string), filter then sorts then fields | `Unpack()` |
| `Page` | which page, counting from zero | `ApplyPagination` |
| `PageSize` | how many rows it holds | `ApplyPagination` |

```
?query=Pay > 10000 OrderBy Pay DESC Select Name,Pay&page=0&pageSize=20
```

**One string, not three members.** The filter, the sorts and the fields travel together in the grammar
[`ParsedQuery`](#filter-order-and-select-in-one-string) reads, which is the same text somebody would have typed
into one box and the same text a saved view is stored as. There is no combination of members to be wrong about,
because there is only the one.

**The window is not in it**, and that is the one thing deliberately kept out. A page is not something the query
language says, and it is not something a caller should be able to say by typing into the filter box.

**It is read as [`Native`](#one-spelling-per-operator) and only as Native**, so there is no style to pass and
none to get wrong. `&&` is refused, `IS NULL` is refused, `ORDER BY` is refused, and each refusal names the
spelling to write instead. The permissive parser exists for text saved before there was a settlement, which is
text *you* hold; a caller sending a query today can be held to the one grammar, and holding them to it is the
point of there being one.

**A condition sent as an object graph goes on a
[`TransportCondition`](#sending-a-condition-across-the-wire)** rather than here. A packed tree is not something
a query string can carry, so a type whose whole point is binding off one has no use for it, and a JSON body
that wants to send one has a type of its own to send it on.

`ApplyRequest` is the calls it stands in for, in the order they have to happen. Nothing more. If you would
rather make them yourself, make them yourself `Unpack()` hands back all three parts at once, and
`UnpackCondition()`, `UnpackSorts()` and `UnpackProjection()` hand back one each; the DTO is no cleverer than
you are.

**It grants nothing.** *Nothing.* A request **names** fields; the bindings decide whether it may have them. Every
allow-list rule in this document applies to a request exactly as it applies to a condition you built by hand, and
a `QueryRequest` naming `PasswordHash` gets the same refusal a string naming it would. Do not read "it binds off
the query string" as "the query string is in charge". It never was.

**A member nobody filled in says nothing**, and what that means is whatever it already meant:

| Left empty | Means |
|---|---|
| no filter in `Query` | no condition added. One the Inquiry already had stays, conditions being the thing here that accumulates |
| no `OrderBy` in `Query` | the `defaultSort` you passed to `ApplyRequest` |
| no `Select` in `Query` | **clears any projection already applied**, and reads every field the caller may read |
| `PageSize` | [`DefaultPageSize`](#a-size-for-the-ones-who-name-none), and no window where there is none. **So does `0`, and so does a negative** a size that could not hold a page is the same as no size |
| `Page` | the first one. **So does a negative**, there being nothing behind the first to ask for |

The `Select` row is the odd one out and it is odd on purpose. A projection is one list rather than something
that accumulates, so a request naming no columns is the caller asking for all of the ones they are allowed,
which is the allow-list's own answer to "all of it". See [what a binding is for](#what-a-binding-is-for).

**Nothing is parsed until it is applied.** The members are the text as it arrived, so building one of these and
reading it back costs nothing and refuses nothing. Malformed text becomes a `WeequeryException` at
`ApplyRequest`, which is somewhere a request handler can answer with a 400. Or ask first.

## Asking whether it will work, before finding out

*[leans back]*

A caller fills in three boxes and gets all three wrong. You throw on the first one. They fix it, send it again,
and you throw on the second. This happens three times, they file a ticket about it, and the ticket lands on
*my* desk.

`Validate()` answers with all of it at once, and throws none of it:

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

Each problem carries the `WeequeryError` to branch on, the message to show, and a `Part` saying which half of the
query it came from `Condition`, `Sort`, `Projection`, or `None` for the request itself, which is
[the one paging refusal](#a-size-for-the-ones-who-name-none) there is. So a message can go beside the box that
caused it rather than at the top of the form.

**Two overloads, and the difference matters.**

```csharp
inquiry.Validate()                          // what is applied, resolved against the bindings
inquiry.Validate(request, DefaultSort)      // that, plus reading the request's text in the first place
```

`ApplyCondition(string)` parses when you call it, so a malformed filter has already thrown long before the
no-argument `Validate()` could look at it. The request overload reads the text itself, in a `try`, and reports
what will not parse **as well as** what parses and names nothing bound. That is two passes, so one half can
report one problem from each: a sort clause that will not parse is one finding, and a sort clause that parses
and names a field nobody bound is another.

The request overload works on a copy, so **asking changes nothing**. The Inquiry you go on to build is the one
you had, and you may ask as many times as you like.

**One problem per half per pass**, not every problem in it. A condition naming two unbound fields reports the
first. Getting the second means fixing the first and asking again and yes, I have heard myself say the thing I
opened this section by complaining about. The difference is that reporting every fault inside one condition
means the expression builder carrying on past a field it could not resolve and building the rest of a tree
around a hole, and I am not shipping that to save a round trip.

Three more things, and then you can go.

**Nothing executes.** It builds what it needs to see whether it can be built, and throws the result away. The
cost is the cost of a build.

**It fills `DroppedFields`**, exactly as a build does, so what a query quietly [drops](#forgetting-a-field-instead-of-refusing-it)
and what it refuses outright arrive at the same moment. That is the one mark it leaves, and the one you want.

**Valid means it will build.** It does not mean it will return rows, and it does not mean the database will
accept it: an [`IsMatch`](#the-laser-ismatch-and-doesnotmatch) against SQL Server validates here and fails there,
which is between the provider and the query, some considerable distance past me.

### Telling it what your backend cannot do

The paragraph above is a hole, and this closes the half of it that can be closed.

"Valid" used to mean the allow-list is happy: every field named is bound, every value parses, the expression
builds. What it could not mean is that the thing on the other end can *run* it.
[`IsMatch`](#the-laser-ismatch-and-doesnotmatch) is the standing example. SQL Server has no regular expressions,
so a filter naming a bound field with a legal operator sails through validation and is refused by the provider,
a long way from the request that carried it and with a message written for nobody.

So say what your backend cannot do, once, where you say everything else about it:

```csharp
var minions = context.Minions.WithWeequery(InquirySettings.Default with
{
    Operators = OperatorSupport.Without(Operator.IsMatch, Operator.DoesNotMatch),
});
```

The refusal now happens where every other refusal happens:

```csharp
inquiry.ApplyCondition("Name IsMatch '^A'").Validate();
// test: 'Name' is tested with IsMatch, which this data source does not support

inquiry.ApplyCondition("Name IsMatch '^A'").Build();
// throws WeequeryException(NotTranslatable, ... the same message)
```

`Build()` throws it and `Validate()` reports it, which is the contract validation has always had and which a
validation-only check would have quietly broken. The code is `WeequeryError.NotTranslatable`, the same one the
[OData](#or-against-an-odata-service) and [Elasticsearch](#the-same-filter-against-an-index) translators already
throw when an operator has no representation on their side.

**Three ways to say it, and one of them is the one you want:**

| | |
|---|---|
| `OperatorSupport.Without(...)` | everything except these. A backend is nearly always everything minus two or three things, so this is both the shorter way to describe one and the honest one |
| `OperatorSupport.Supporting(...)` | exactly these and nothing else. A literal list, which means naming `And`, `Or` and `Not` where your conditions use them |
| `OperatorSupport.Everything` | the default. Refuses nothing, which is how this library has always behaved and how it still behaves unless you say otherwise |

**Every operator counts, including the ones you do not think of as operators.** `And`, `Or` and `Not` are
operators, the quantifiers are operators, and something has to evaluate all of them. That is why `Supporting` is
literal rather than quietly topping itself up with the conjunctions: a set that silently added things back would
be a set you could not trust to mean what it says.

**And the ones inside a quantifier count as much as the ones outside it.** This is the one place the operator
walk differs from the field walk, deliberately. A quantifier's children are a different *allow-list*, which is
why fields stop at the boundary; they are not a different *backend*, so operators do not:

```
Assignments Any (LairName IsMatch '^V')      still an IsMatch, still refused
```

`condition.OperatorsUsed()` hands you the list if you would rather ask the question than have it answered.

**It does not tell you the query will translate.** It tells you the query does not use an operator you said was
missing, which is a smaller claim, and the only one a list of operators can make. A bound property nothing maps,
a comparison the provider cannot express, a collection it will not reach through: all still between you and the
provider. [`Weequery.EntityFrameworkCore`](Weequery.EntityFrameworkCore/README.md) answers the first of those,
and `Build().ToQueryString()` answers the rest.

## Without an IQueryable

Sometimes there is no database. Sometimes there is only a list, sitting in memory, and a question you want asked
of it. Fine. Take the predicate and go:

```csharp
Expression<Func<Minion, bool>> predicate =
    Inquiry<Minion>.BuildExpression(MinionBindings, condition);

Func<Minion, bool> compiled =
    Inquiry<Minion>.BuildDelegate(MinionBindings, condition, settings);
```

`BuildDelegate` runs in memory by definition, so I apply the [string comparison rules](#how-strings-compare) and
the regex timeout for you, and take the settings that decide the first of them. Leave the argument off and I use
`InquirySettings.Default`, which is what I would have chosen anyway. `BuildExpression` is the translatable form
and applies neither, which matters enormously if you compile it yourself. I warned you about that in its own
section. I will not be warning you again.

## Types I accept

I am not an unreasonable man. I accept a great many things:

`bool`, `byte`, `sbyte`, `char`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`,
`DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid`, `string`, and any `enum`.

The `Nullable<>` form of any of them, naturally. An unsupported reference type may still be bound, but all it
will ever answer is `IsNull` and `IsNotNull`, which is frankly more than it deserves.

## Things you will get wrong

*[picks up a laser pointer, aims it at the list]*

**An Inquiry is immutable, and the way you will get that wrong is by ignoring what I hand back.** Every `Apply`
and every `Bind` leaves the one you called it on exactly as it was and returns a *new* one carrying the change.
A chain composes, the way a LINQ chain does. What does nothing whatsoever is a call whose result you throw on
the floor:

```csharp
var inquiry = query.WithWeequery().BindProperties(MinionBindings);

inquiry.ApplyCondition("IsActive = true");   // does nothing. The filtered one went nowhere
var all = inquiry.Build();                   // every row, and no complaint
```

Nothing will tell you. A discarded return value is perfectly legal C#, which I consider one of the great
oversights of our age. Keep the chain, or keep the result:

```csharp
var active = inquiry.ApplyCondition("IsActive = true").Build();
var paid   = inquiry.ApplyCondition("Pay > 10000").Build();   // paid, and only paid
```

Which is also precisely what makes one configured Inquiry worth keeping and branching off as often as you like.
No branch can reach another. Nothing accumulates where you did not put it. A copy copies the lists rather than
what is in them, and a binding is immutable once built and is shared, so a copy costs a dictionary and two
lists. That is why I can afford to make one on every single call, and why you will stop complaining about it.

**Conditions may only nest 16 levels deep.** `Pack()`, `Unpack()` and `ToQuery()` refuse to go further and
throw. `ToString()` writes `<nested too deep>` where it stopped and hands back what it has, because a
`ToString` that throws makes debugging worse, and I have suffered enough.

The limit follows what a query *means*, not how it was punctuated. `(((Salary > 1)))` is three levels of text
and no nesting whatsoever, while `A AND B OR C AND D` has not a parenthesis in sight and is a tree two deep,
because precedence nests it whether you intended it or not.

**Values are parameterized.** Filter values reach the database as parameters and are never written into the
SQL, because I am not an animal. The same condition shape gives one statement and one query plan whatever the
values:

```sql
-- Name = 'Alice Fox' and Name = 'Someone Else' both produce:
WHERE "m"."Name" = @Value
```

**An `IsIn` list is capped at 1000 values.** The list becomes parameters and a provider will only take so many.
I check when the condition is built and refuse it naming the operator and the count, rather than let the
database reject it halfway through the scheme, which is the worst possible moment to discover anything. Every
route in is held to it.

**String matching is case-sensitive, and the database half of it is not yours to set.** In memory every string
operator compares ordinally unless the query says otherwise, so `Name = 'alice fox'` does not find Alice Fox.
Ask for `OrdinalIgnoreCase` if that is what you meant. Against a database the column's collation decides
instead, including whether the match is case-sensitive, and nothing you pass me changes that. I have made my
peace with it. See [How strings compare](#how-strings-compare), where I have put the whole of this subject in
one place so that you need only be disappointed once.

**An enum orders by its values, not its names.** With `enum Rank { Low = 1, High = 2 }`, `Rank > Low` finds
`High`, and renaming the members changes precisely nothing.

**Keys are your allow-list.** Auto-generated keys are the property path, so `BindProperty(x => x.Name)` puts the
property name on the wire. If your model's names are not something you want the world reading, pass explicit
keys. I cannot stress this enough. This is how they find the volcano.

**Errors are `WeequeryException`.** Parse failures, unbound fields, unsupported operators and bad values all
throw it. Most of it is some *caller's* input rather than your mistake, so a request handler will normally catch
it and answer with a bad request rather than let it become an incident, then a conversation, and eventually a
meeting.

## The other end of the wire

*[gestures at a second whiteboard]*

Somebody has to build the filter, and it is usually a browser. There is a TypeScript package in
[`js/`](js/README.md) that does it: the same condition tree, the same query language, the same packed JSON, and
the same operator numbers, which are the wire format and are pinned by a test on both sides so that neither can
drift without the other noticing.

```ts
import { and, BindingSet, gt, eq, toQuery, pack, validateQuery } from 'weequery';

toQuery(and(gt('Salary', 10000), eq('IsActive', true)));
// "(([Salary] > 10000) AND ([IsActive] = True))"
```

The part worth having is that it validates. Hand it the same binding list you declared here and it predicts what
I will say, so an unbound field, a `Contains` on a number or a sort on a constant becomes a message beside the
input box rather than a round trip and a 400. It grants nothing. **Your** binding list is still the only
allow-list, and a drifted copy on the client merely makes worse predictions, which is its own punishment. No
dependencies there either.

## If you would rather have your own DTO

[`Reading back only some of it`](#reading-back-only-some-of-it) hands you a dictionary, because there the caller
picked the columns. When *you* pick them, and the shape is a type you already have, I have provided three
separate packages, one per mapper, doing the same job with the same two methods:

```csharp
var (page, total) = context.Minions
    .WithWeequery()
    .BindProperties(MinionBindings)
    .ApplyCondition(request.Filter)
    .ApplySorts(request.Sort, DefaultSort)
    .ApplyPagination(request.PageSize, request.Page)
    .ProjectToPaged<Minion, MinionSummary>(configuration);
```

| Package | Mapper | How the mapping is handed over |
|---|---|---|
| [`Weequery.AutoMapper`](Weequery.AutoMapper/README.md) | AutoMapper | an `IConfigurationProvider` or an `IMapper` |
| [`Weequery.Mapster`](Weequery.Mapster/README.md) | Mapster | a `TypeAdapterConfig`, or nothing at all, Mapster mapping by convention |
| [`Weequery.Mapperly`](Weequery.Mapperly/README.md) | Mapperly | the generated projection method itself, there being no runtime to ask |

I decide which rows. The mapper decides what a row looks like. Only the DTO's columns ever leave the database,
and only the page is projected, a count being of rows rather than of what you read off them.

Take the one you already use. They are separate packages so that installing me never drags a mapper in, and
picking one never drags in the other two. I do not believe in collateral damage. Not of that sort.

**The AutoMapper one is the only one with a licence to think about.** AutoMapper is dual licensed from v15: free
for individuals, for non commercial use and for organisations under a revenue threshold, and a paid key above
it. That package is on 15.1.3 because v14, the last MIT release, carries an unpatched high-severity DoS
([CVE-2026-32933](https://github.com/advisories/GHSA-rvv3-g6hj-g44x)) that will never be fixed in the MIT line.
I am in the denial-of-service business myself, and even I would not ship that. Mapster and Mapperly are MIT with
nothing to inherit.

I still have no dependencies, and I never will.

## The same filter, against an index

A condition is a tree, not SQL, so a database is by no means the only thing it can become.
[`Weequery.Elasticsearch`](Weequery.Elasticsearch/README.md) turns one into Elasticsearch Query DSL:

```csharp
ElasticQuery.ToJson(ConditionFunctions.ParseQuery("IsActive = true AND Pay > 10000"), fields);
```
```jsonc
{ "bool": { "filter": [ { "term": { "active": true } },
                        { "range": { "salary": { "gt": 10000 } } } ] } }
```

There is no entity to walk here and no mapping to read, so the allow-list is declared rather than derived: a
key, the field it means in the index, and what that field holds. Sorts, the window and the projection come along
too, as `sort`, `from`/`size` and `_source`. I leave nothing behind.

**The nulls survive the trip**, which is the part worth having and the part everybody else gets wrong.
Elasticsearch agrees with [the rules above](#how-nulls-behave) free of charge on the positive operators, and
flatly refuses to on the negative ones, a bare `must_not` matching documents that have no such field at all. So
I write those with an `exists` beside them. `Alias <> 'Ghost'` and `NOT (Alias = 'Ghost')` still answer
differently, exactly as they do here, because a filter that changes its mind when it changes backend is worse
than no filter at all.

Quantifiers become `nested` queries, and `All` becomes "no element fails it", which is the only way to say it
over nested documents. Inelegant. Correct.

No dependencies there either: the Query DSL is JSON, so nothing pins you to a client, a generation or a licence.
It works against OpenSearch for exactly the same reason.

## Or against an OData service

And if what you are pointed at is an OData service, [`Weequery.OData`](Weequery.OData/README.md) writes the same
condition as a `$filter`:

```csharp
ODataFilter.Write(ConditionFunctions.ParseQuery("IsActive = true AND Pay > 10000"), fields);
// "(Active eq true and Salary gt 10000)"
```

Sorts, the window and the projection come along as `$orderby`, `$top`/`$skip` and `$select`, and quantifiers
become lambdas: `Assignments/any(d1: d1/LairID eq 5)`.

**The nulls survive this trip too**, and here the disagreement is written down for once, which I appreciate:
OData's specification says a null is *"not equal to any other value"*, so `Alias ne 'Ghost'` returns the records
with no alias where [mine does not](#how-nulls-behave). Every negative operator therefore carries its guard,
`(Alias ne null and Alias ne 'Ghost')`, while `NOT` deliberately does not.

The one thing OData does that neither SQL nor a search index manages with any grace is comparing two properties:
`Name = [Alias]` becomes `Name eq Alias`. Credit where it is due. Briefly.

No dependencies there either. A `$filter` is text, and the OData client libraries are enormous and versioned
against their own model, which is somebody else's problem and shall remain so.

## What all this costs you

Fair question. Fair. I had it measured, and the numbers are in [BENCHMARKS.md](BENCHMARKS.md).

The claim comes in two halves, and only one of them needed a stopwatch.

**Execution is free, and that is proven rather than timed.** For the comparison operators I hand the provider,
byte for byte, the statement a hand written `Where` produces. The test suite asserts it, on every build, by
comparing the SQL. A server given identical SQL cannot run it more slowly. No benchmark could make that point
better, because a benchmark would only ever tell you about the machine it happened to run on.

**What is left is the work on this side of the wire**, and that is what [`Weequery.Benchmarks`](Weequery.Benchmarks/README.md)
measures: parsing the filter your caller sent, resolving the allow-list, and building the expression tree. Time
and allocations, per operation, with the CPU written above the table so that nobody can pretend otherwise.

```bash
dotnet run -c Release --project Weequery.Benchmarks
```

It also measures the two support libraries, since writing an Elasticsearch query or an OData `$filter` is pure
arithmetic with no database anywhere in it, which makes those the most trustworthy figures in the set.

**Read the baseline before you read the ratios**, because the obvious comparisons are both wrong, in opposite
directions, and I refuse to be misrepresented in either.

Filtering a `List` through `AsQueryable` makes LINQ to Objects compile the expression tree *on every call*,
which costs about a third of a millisecond and swamps everything either side of it. A ratio taken there would
**flatter** me, and I will not have it said that I needed the help.

Comparing my compiled predicate against a hand written lambda **slanders** me instead. Anything that comes out
of `Expression.Compile` is a `DynamicMethod`, and the JIT will not inline one into the loop that calls it; a
lambda your compiler wrote is an ordinary method, and it will. That is a fact about .NET and it applies to
*your* expression tree exactly as it applies to mine. So the tables run the same predicate three ways, as a
lambda, as a compiled expression, and through me, and you may see for yourself which part of the gap belongs to
the runtime and which part belongs to me. On a condition needing no null guards, mine is the smaller half by
some distance.

None of which applies to a database, where EF Core caches its plans and is handed the same SQL either way.

## Building and testing

Two commands. Try to keep up:

```bash
dotnet build
```

```bash
dotnet test Tests/Tests.csproj
```

The suite runs against SQLite with no setup at all, because I am not going to stand here while you install
something. PostgreSQL and SQL Server are opt-in: set `WEEQUERY_TEST_POSTGRES` or `WEEQUERY_TEST_SQLSERVER` to a
connection string and those tests start running instead of reporting as skipped. The throughput comparisons want
`WEEQUERY_TEST_THROUGHPUT`.

## Credit where it is due

*[long pause]*

Sit back down. There is one more thing.

It has come to my attention that someone got here first. **[Superfilter](https://github.com/Ibramadi75/Superfilter)** 
dynamic filtering, sorting and pagination over `IQueryable`, in C#, mapping textual filter criteria onto strongly
typed expressions. Which is, and I want you all to appreciate what it costs me to say this out loud, *the same
idea*. It is good work. It is MIT licensed. It is sitting right there on the internet where anyone can see it.

Weequery went its own way on a number of things the allow-list, null support, the query language, the
shape it travels in, but the idea was Superfilter's first, and I have been informed that pretending otherwise
constitutes what is called "a reputational exposure."

So. Go and look at it. Thank the man. Then come back here and never speak of this again.

*[exhale]*

## License

MIT. Take it. Do as you like with it. I have larger concerns.

Now get out. All of you. Not you!
