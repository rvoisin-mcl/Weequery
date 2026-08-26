# Weequery

*[steeples fingers]*

Gentlemen. Welcome. Sit down. **Sit. Down.**

You've all been asking me the same question. "Boss, how do I let the field agents filter the henchman
roster without me shipping a new build of the doomsday console every time somebody wants to sort by pay?"

Well. I've spent the last thirty years in a cryogenic freezer thinking about exactly this, and I've built a
"library" — called **Weequery**.

It turns a filter that arrives as *data* — from a form, from a query string, from a client, from a fax, I don't
care — into a real `IQueryable` expression. You declare which properties may be asked about. Everyone else asks
about only those. It's an allow-list. It's beautiful.

- **Targets .NET 8 and .NET 10.** The eight is a courtesy to those of you still thawing out.
- **Works with Entity Framework Core.** Your conditions become SQL. Values go as parameters, not glued into the
  statement like some kind of *animal*.
- **Works in memory.** The same condition filters a `List<T>`.
- **Verified against SQLite, PostgreSQL and SQL Server.**

Scott, put your hand down.

## Contents

- [The plan](#the-plan)
- [Deciding what they may ask about](#deciding-what-they-may-ask-about)
- [Conditions](#conditions)
- [The query language](#the-query-language)
- [Operators](#operators)
- [How nulls behave](#how-nulls-behave)
- [Sorting and paging](#sorting-and-paging)
- [Sending a condition across the wire](#sending-a-condition-across-the-wire)
- [Without an IQueryable](#without-an-iqueryable)
- [Types I accept](#types-i-accept)
- [Things you will get wrong](#things-you-will-get-wrong)

## The plan

```csharp
using Weequery;

var minions = context.Minions
    .WithWeequery()
    .BindProperty(minion => minion.Name)
    .BindProperty(minion => minion.Pay)
    .BindProperty(minion => minion.IsActive)
    .ApplyCondition("(Pay > 10000) && (IsActive == true)")
    .ApplySort(new Sort("Pay", SortDirection.Descending))
    .ApplyPagination(pageSize: 20, page: 0)
    .Build()
    .ToList();
```

`WithWeequery()` begins the scheme. `Build()` hands back an `IQueryable<T>` with everything applied. Nothing
executes until you enumerate it, so it composes with whatever else you had planned.

In a controller, the condition arrives from the caller — a stranger, on the internet, typing. That is the 
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

## Deciding what they may ask about

This is the important part, so put down the shark food and *listen*.

A **binding** maps a **key** — the name outsiders use — to a property on your entity. Only bound properties can
be filtered or sorted on. Nothing else. Ever. If it isn't bound, asking for it gets a `WeequeryException` and a
very disappointed look from me.

```csharp
.BindProperty(minion => minion.Name)              // key defaults to "Name"
.BindProperty(minion => minion.Pay, "Salary")     // key is "Salary"
.BindProperty("Lair.Capacity", "LairCapacity")    // a path, given as a string
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

Why not just write `minion => minion.BirthDate.Year`? Because C# won't compile it against a `DateTime?`, and
`minion.BirthDate!.Value.Year` means something *different* — naming `Value` **unwraps**, so you get a plain `int`
that has no null of its own and `IsNull` stops applying to it. Segments reach *through*. My father would have
found this fascinating. He invented the question mark. The details are unimportant.

### Values I supply, that they merely name

A binding can stand for a constant **your** code provides, under a name the caller may refer to but cannot set:

```csharp
.BindConstant("Threshold", payThreshold)
.ApplyCondition("Pay > [Threshold]")        // they name it, I decide what it is
```

Do you see it? *Do you see it?* The caller writes the *shape* of the question. I fill in the part they must not
see — the per-tenant limit, the cutoff date, today's date, the number I am not telling them.

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

`BindProperties` resolves a given set once for the life of the process and keeps it, so calling it per request —
which is the normal thing to do — costs a copy rather than a property lookup each time.

### The rules for keys

**They must be valid unquoted SQL names.** An ASCII letter or underscore, then letters, digits or underscores.

```csharp
WeequeryException.IsSqlName("Total_2");   // true
WeequeryException.IsSqlName("my field");  // false
```

**They're matched without regard to case.** `pay`, `Pay` and `PAY` all find a property bound as `Pay`. Which
means two keys differing only in case are the *same key*, and binding both is a duplicate — refused when the
second one is created.

**They may not be named after an operator.** A key is written as a bare field name, so a key called `Contains`
makes a query that reads two ways, and I did not claw my way out of a Belgian orphanage to write an *ambiguous
parser*.

```csharp
.BindProperty(minion => minion.Notes, "Contains")    // throws. Obviously it throws.
```

The reserved words are the operator names — `IsNull`, `IsNotNull`, `IsIn`, `IsNotIn`, `IsBetween`,
`IsNotBetween`, `StartsWith`, `DoesNotStartWith`, `EndsWith`, `DoesNotEndWith`, `Contains`, `DoesNotContain`,
`IsMatch`, `DoesNotMatch` — and the words SQL spells its operators with: `AND`, `OR`, `NOT`, `IS`, `IN`,
`BETWEEN`, `NULL`. Case does not matter.

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
| `OneValueCondition<T>` | `==`, `!=`, `<`, `<=`, `>`, `>=`, the substring family, `IsMatch`, `DoesNotMatch` |
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

Any operand of any shape can be one, and they mix — `Pay IsIn (8000, [Cap])` is a list holding a value *and* a
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
(Pay > 10000) && (IsActive == true)
Name StartsWith 'Al'
Pay IsBetween (8000, 12000)
Alias IsNull
Pay > [Threshold]                     brackets name a bound property
Pay IsIn (8000, [Ceiling])            and they mix
```

- **`&&` binds tighter than `||`**, matching SQL and C#. Parentheses group freely.
- **Keywords, operator names and field names are case-insensitive.** `AND`, `and` and `&&` are the same word.
- **Alternate spellings**: `=` for `==`, `<>` for `!=`, `AND`/`OR`/`NOT`, and SQL's own `IN`, `NOT IN`,
  `IS NULL`, `IS NOT NULL`, `BETWEEN`, `NOT BETWEEN`. A range can be written `Pay BETWEEN 1 AND 5` or
  `Pay IsBetween (1, 5)`. `LIKE` is **not** supported — use `Contains`, `StartsWith`, `EndsWith`.
- **Quote a value** when it contains a space, a delimiter, or looks like a keyword. `'single'` or `"double"`,
  and a literal closes on whichever quote opened it, so the other needs no escaping: `"it's"` and `'say "hi"'`
  both read as written. A backslash escapes a quote or another backslash — and **only** those, so `'\w'` is the
  two characters it looks like. `''` and `""` are the empty string.
- **Values need no quotes** when they're simple: numbers, `true`, enum names, GUIDs, ISO dates and times.
- **Values are always parsed with the invariant culture**, so a query means the same thing on every machine, in
  every lair, on every continent.

`ToQuery()` is the inverse, and writes it back out.

## Operators

| Operator | Query text | Values | Notes |
|---|---|---|---|
| `IsNull` | `IsNull`, `IS NULL`, `== null` | 0 | needs a nullable property |
| `IsNotNull` | `IsNotNull`, `IS NOT NULL`, `!= null` | 0 | needs a nullable property |
| `Equals` | `==`, `=` | 1 | |
| `NotEqual` | `!=`, `<>` | 1 | |
| `LessThan` | `<` | 1 | strings order too |
| `LessThanOrEqual` | `<=` | 1 | |
| `GreaterThan` | `>` | 1 | |
| `GreaterThanOrEqual` | `>=` | 1 | |
| `IsBetween` | `IsBetween`, `BETWEEN` | 2 | inclusive of both ends |
| `IsNotBetween` | `IsNotBetween`, `NOT BETWEEN` | 2 | |
| `IsIn` | `IsIn`, `IN` | 0–1000 | an empty list matches nothing |
| `IsNotIn` | `IsNotIn`, `NOT IN` | 0–1000 | |
| `StartsWith` | `StartsWith` | 1 | strings only |
| `DoesNotStartWith` | `DoesNotStartWith` | 1 | strings only |
| `EndsWith` | `EndsWith` | 1 | strings only |
| `DoesNotEndWith` | `DoesNotEndWith` | 1 | strings only |
| `Contains` | `Contains` | 1 | strings only |
| `DoesNotContain` | `DoesNotContain` | 1 | strings only |
| `IsMatch` | `IsMatch` | 1 | strings only, **not on every provider** |
| `DoesNotMatch` | `DoesNotMatch` | 1 | strings only, **not on every provider** |
| `And` | `&&`, `AND` | | `ConjunctionCondition` |
| `Or` | `\|\|`, `OR` | | `ConjunctionCondition` |
| `Not` | `!`, `NOT` | | `NotCondition` |

A `bool` accepts only the null tests, equality and the `IsIn` family. Ordering is refused up front, rather than 
left to fail six layers down with a complaint about Boolean having no comparison operator. You're welcome.

### The "laser" — `IsMatch` and `DoesNotMatch`

Right. These match a string against a regular expression, and they are the **only two operators that do not work
everywhere**, because standard SQL has no regular expression and every provider went off and did its own thing.

| Where | What you get |
|---|---|
| In memory | .NET's own `Regex`, bounded by `Inquiry<T>.MatchTimeout` |
| SQLite | `REGEXP`, which Microsoft.Data.Sqlite implements with .NET's `Regex` — so it agrees with memory |
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
denial of service — against *me* — using nothing but punctuation.

So where Weequery runs the match, it bounds it. One second by default:

```csharp
Inquiry<Minion>.MatchTimeout = TimeSpan.FromMilliseconds(250);   // Regex.InfiniteMatchTimeout removes the bound
```

It's a static on the generic type, so it's set **per entity type**. Exceeding it raises
`RegexMatchTimeoutException` from wherever the query is being enumerated. The bound applies in memory only —
translated to SQL, the pattern is the database's problem and its own limits apply. It cannot be both: the
`Regex.IsMatch` overload carrying a timeout is not one any provider can translate, so building with it would stop
being a query and become a table scan on the client.

`DoesNotMatch` is the **negative operator**, not a negation. A row with no value matches *neither* it nor
`IsMatch`. `!(Alias IsMatch '^G')` is a different question and brings the null rows back with it. Which brings me
to my favourite subject.

## How nulls behave

**A null satisfies nothing except `IsNull`.**

Every other operator is built as *"the property has a value"* ANDed with the test on that value. So a null is not
caught by the negative operators either — it is not "not equal to 5", it is **unknown**, exactly as a database
treats it.

Which means for any column, the rows matching an operator, the rows matching its negation, and the null rows
partition the table between them. One condition gives the same answer whether it runs against a database or in
memory. That is not an accident. That took me *considerable* time, during which nobody brought me coffee.

`Not` is the exception, because it negates the whole test — the guard included — so the nulls come back:

```
Alias != 'Ghost'        every minion who has an alias, and it isn't Ghost
!(Alias == 'Ghost')     the same, plus every minion with no alias at all
```

Both are useful. Neither is normalised into the other. They are not interchangeable and if you confuse them your
report will be wrong and I *will* find out.

This extends to a property reached *through* a nullable. `BirthDate.Year` on a `DateTime?` is legal, and behaves
as a nullable in its own right even though `Year` is an `int`. `Lair.Name` where the minion has no lair matches
nothing, and `Lair.Name IsNull` asks whether the lair is there. A database answers that through the join, and
guarding it here is what makes the two agree.

## Sorting and paging

```csharp
.ApplySorts([
    new Sort("IsActive", SortDirection.Ascending),
    new Sort("Salary", SortDirection.Descending),   // breaks ties within IsActive
])
.ApplyPagination(pageSize: 20, page: 2)             // page is zero based
```

Sorts apply in the order given, each breaking ties in the one before. Sort fields must be bound. Nullable
properties sort nulls first. A field with no ordering of its own — a collection, a navigation property — is
refused: it can be bound and tested for null, but there is no such thing as its first row.

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

## Sending a condition across the wire

Two ways to move a condition between processes. Or continents. Or hollowed-out volcanoes.

**As a query string**, with `ToQuery()`:

```csharp
string text = condition.ToQuery();     // "(([Salary] > 10000) && ([Name] Contains 'temp'))"
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
and only a key carries a `Source` — so an ordinary condition costs nothing extra:

```jsonc
// Name IsIn ('Alice Fox', [Alias])
{ "Operator": 10, "Field": "Name", "Conditions": [],
  "Values": [ "Alice Fox", { "Source": 1, "Value": "Alias" } ] }

// Name IsIn ('Alice Fox', 'Bob Samuelson')
{ "Operator": 10, "Field": "Name", "Conditions": [],
  "Values": [ "Alice Fox", "Bob Samuelson" ] }
```

Nothing is guessed from the text. The two are told apart by the **shape** they arrive in — a string against an
object — which no value can be mistaken for whatever it happens to spell. There is nowhere for a key to arrive as
bare text and be compared against as though it were one. I thought of everything. I always think of everything.

## Without an IQueryable

For a predicate rather than a query — for `Where`, for `Any`, for filtering objects already in memory:

```csharp
Expression<Func<Minion, bool>> predicate =
    Inquiry<Minion>.BuildExpression(MinionBindings, condition);

Func<Minion, bool> compiled =
    Inquiry<Minion>.BuildDelegate(MinionBindings, condition);
```

## Types I accept

`bool`, `byte`, `sbyte`, `char`, `short`, `ushort`, `int`, `uint`, `long`, `ulong`, `float`, `double`, `decimal`,
`DateTime`, `DateTimeOffset`, `DateOnly`, `TimeOnly`, `TimeSpan`, `Guid`, `string`, and any `enum`.

The `Nullable<>` form of any of them works too. An unsupported reference type can still be bound, but supports
only `IsNull` and `IsNotNull`.

## Things you will get wrong

**Conditions may only nest 16 levels deep.** `Pack()`, `Unpack()` and `ToQuery()` refuse to go further and throw.
`ToString()` writes `<nested too deep>` where it stopped and returns what it has, because a `ToString` that throws
makes debugging worse and I have suffered enough.

The limit follows what a query *means*, not how it was punctuated. `(((Salary > 1)))` is three levels of text and
no nesting at all, while `A && B || C && D` has no parentheses and is a tree two deep, because precedence nests
it.

**Values are parameterized.** Filter values go to the database as parameters, never written into the SQL. The
same condition shape gives one statement and one query plan whatever the values:

```sql
-- Name == 'Alice Fox' and Name == 'Someone Else' both produce:
WHERE "m"."Name" = @Value
```

**An `IsIn` list is capped at 1000 values.** The list becomes parameters and a provider will only take so many.
Checked when the condition is built, and refused naming the operator and the count, rather than left for the
database to reject mid-scheme. Every route in is held to it.

**String matching varies by whoever evaluates it.** The substring operators are built from the framework's own
string methods, so the rules come from wherever the query runs. In memory `StartsWith` and `EndsWith` are
culture-sensitive while `Contains` is ordinal; against a database the column's collation decides, including
whether the match is case-sensitive. If you need answers that agree everywhere, normalise both sides first, or
pick a matching collation.

**An enum orders by its values, not its names.** With `enum Rank { Low = 1, High = 2 }`, `Rank > Low` finds
`High`, and renaming the members changes nothing.

**Keys are your allow-list.** Auto-generated keys are the property path, so `BindProperty(x => x.Name)` puts the
property name on the wire. If your model's names are not something you want the world reading, pass explicit
keys. I cannot stress this enough. This is how they find the volcano.

**Errors are `WeequeryException`.** Parse failures, unbound fields, unsupported operators and bad values all
throw it. Most of it is *caller* input rather than your mistake, so a request handler will normally catch it and
answer with a bad request rather than let it become an incident.

## Building and testing

```bash
dotnet build
```

```bash
dotnet test Tests/Tests.csproj
```

The suite runs against SQLite with no setup at all. PostgreSQL and SQL Server are opt-in — set
`WEEQUERY_TEST_POSTGRES` or `WEEQUERY_TEST_SQLSERVER` to a connection string and those tests start running
instead of reporting as skipped. The throughput comparisons need `WEEQUERY_TEST_THROUGHPUT`.

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

MIT.

Now get out. All of you. Not you!
