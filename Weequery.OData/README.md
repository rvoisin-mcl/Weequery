# Weequery.OData

And if what you are pointed at is an OData service, this turns a
[Weequery](https://github.com/rvoisin-mcl/Weequery) condition into a `$filter`, so the same filter a caller sends
to your database can be sent somewhere else entirely without anybody rewriting a thing.

```csharp
var fields = new ODataFieldSet
{
    new("Name",     "Name"),
    new("Alias",    "Alias"),
    new("Pay",      "Salary", ODataFieldKind.Number),
    new("IsActive", "Active", ODataFieldKind.Boolean),
    new("City",     "Lair/Address/City"),
};

ODataFilter.Write(ConditionFunctions.ParseQuery("IsActive = true AND Pay > 10000"), fields);
// "(Active eq true and Salary gt 10000)"
```

**No dependencies.** A `$filter` is text. The OData client libraries are enormous, versioned against their own
EDM model, and entirely unnecessary for writing one. What comes out goes on a query string. That is the whole
arrangement.

## The allow-list is declared, not derived

There is no metadata document to read from here, so **you** say what a key means:

| | |
|---|---|
| **Key** | the name a caller writes, matched without regard to case |
| **Field** | the property's path, slash separated: `Lair/Address/City` |
| **Kind** | `String`, `Number`, `Boolean`, `Guid`, `Date`, `DateTimeOffset`, `TimeOfDay`, `Duration`, `Enum`, `Collection` |
| **Collection** | the collection this field lives inside, for quantifiers |
| **EnumType** | the qualified type name, for `Enum` |

A condition naming a key nobody declared is refused, exactly as an unbound field is. The kind matters rather more
here than in most places, because **OData writes almost every type differently and gets it wrong loudly**:

```
Name eq 'Alice'                                     String, quoted
Salary eq 10000                                     Number, bare
Active eq true                                      Boolean, lower case
HiredOn eq 2024-01-15                               Date, bare
MinionID eq 0f8fad5b-d9cb-469f-a165-70867728950e    Guid, bare, the v3 quotes are gone
Classification eq Lair.Model.Rank'High'             Enum, qualified where you named the type
ShiftLength eq duration'PT8H'                       Duration
```

Hand a service the wrong shape and it answers 400, not nothing, which at least has the virtue of being loud. A
value that would end the expression early, a quote or a comma in what ought to be a bare literal, I refuse here
instead, where the message can say why rather than leaving some stranger's server to say "no" and nothing else.

## Nulls are translated, not assumed

This is the part worth having. **OData disagrees with me about a null, and has the nerve to say so in the
specification:** *"null values are equal to null and not equal to any other value"*. So `Alias ne 'Ghost'` is
**true** of a record with no alias. With me it is false, because every comparison I write carries a guard.

Since one of us has to give way and it is not going to be me, every negative operator goes out with its guard
beside it:

```
Alias <> 'Ghost'      -> (Alias ne null and Alias ne 'Ghost')
```

And `NOT` is deliberately **not** guarded, for precisely the reason it is not guarded in the core: negating a
condition negates its guard along with it, so `NOT (Alias = 'Ghost')` is *meant* to bring the records with no
alias back.

```
NOT (Alias = 'Ghost')  ->  not Alias eq 'Ghost'
```

That distinction is [the one I make a fuss about](../README.md#how-nulls-behave), and it survives the trip.
Services vary in how much of this they manage unaided, particularly for the string functions. A guard a service
would have applied anyway costs you a few characters; depending on one would cost you a different answer per
service, which is not a trade I would make and not one I will make for you.

## The operators

| Weequery | OData |
|---|---|
| `=` `<>` `<` `<=` `>` `>=` | `eq` `ne` `lt` `le` `gt` `ge` |
| `IsNull` / `IsNotNull` | `eq null` / `ne null` |
| `IsBetween` | `(f ge a and f le b)`, inclusive both ends |
| `IsIn` | `in (...)`, or a chain of `or` for 4.0. An empty list is `false` |
| `StartsWith` `EndsWith` `Contains` | `startswith` `endswith` `contains` |
| `IsMatch` | `matchesPattern`, **4.01 only**, and thinly implemented |
| `AND` `OR` `NOT` | `and` `or` `not`, parenthesised so a tree reads back as the tree it was |
| `Any` `All` `None` | `nav/any(d1: ...)`, `nav/all(d1: ...)`, `not nav/any(d1: ...)` |

**One thing OData does that neither SQL nor a search index manages with any grace:** comparing two properties.
`Name = [Alias]` becomes `Name eq Alias`, and it simply works. Credit where it is due. I shall not mention it
again.

## Versions

`ODataVersion.V401` by default, which is what most services have spoken for years. Pass `V4` and two things
change: `in` is expanded into `(f eq a or f eq b)`, and `IsMatch` is refused outright rather than emitting a
function 4.0 has never heard of.

Worth knowing that the expansion turns a long list into a long URL. A thousand values, which is what I will
carry, is not something every server will accept on a query string however it is spelled, and discovering that
in production is nobody's idea of an afternoon.

`matchesPattern` is ECMAScript syntax by specification and is one of the more thinly implemented corners of 4.01,
so a service may well answer 501 to a filter that is perfectly, provably legal. Not my doing.

## Quantifiers become lambdas

```csharp
new("Assignments", "Assignments", ODataFieldKind.Collection),
new("LairName",    "Lair/Name",   Collection: "Assignments"),   // path relative to the element
```
```
Assignments Any (LairID = 5 AND LairName = 'Volcano')
```
```
Assignments/any(d1: (d1/LairID eq 5 and d1/Lair/Name eq 'Volcano'))
```

The whole inner condition goes inside the lambda, which is exactly what my quantifier means: *one* element
satisfying all of it, not several elements splitting the work between them. `all` over an empty collection is
true and `any` over one is false, which is what the quantifiers mean of nothing in the core as well, so the three
of us line up with no assistance whatsoever. It is almost touching.

A field declared inside a collection is only reachable within a quantifier over it, and one declared outside is
not reachable inside. The two scopes do not leak. They do not leak here for the same reason nothing leaks
anywhere else in this operation.

## What it refuses

- **A key nobody declared**, as ever.
- **An operator that does not fit the declared kind**, `StartsWith` on a number, ordering on a boolean.
- **A value that cannot be written as its kind**, including one that would end the expression early.
- **A collection compared** rather than quantified, and a non-collection quantified.
- **An index** (`Tags[0]`). A `$filter` has no way to address one element of a collection, and I will not
  pretend otherwise.
- **A quantifier inside a quantifier.** OData can nest lambdas perfectly well, but a Weequery collection
  declares no collections of its own, so there is nothing that could possibly have been meant.
- **Sorting on or selecting a field inside a collection.** `$orderby` would have to say which of the many
  values, and reading one is `$expand` with a select of its own. Neither is a guess I am willing to make.

## The whole set of query options

```csharp
var parsed = ParsedQuery.Parse(request.Query);

ODataQuery.ToQueryString(fields, parsed.Condition, parsed.Sorts,
    Projection.Parse(request.Fields), pageSize: 20, page: 2);
```
```
$filter=Active eq true&$orderby=Salary desc,Name asc&$top=20&$skip=40&$select=Name,Alias
```

`Build` hands you the same thing as a dictionary, if you would rather place the options yourself. The order is
fixed, so the same query gives the same string every single time, which is what makes one comparable and
cacheable. The first page writes no `$skip`, since skipping nothing is what not saying so already means, and I do
not pad.

> [!IMPORTANT]
> **Nothing here is percent encoded.** Encoding is the job of whatever builds the URL, and doing it here would
> mean a caller who does it properly encodes the whole thing twice, which is the sort of quiet catastrophe I
> refuse to be responsible for.

## Licence

MIT, same as the rest of the operation.
