# Weequery.OData

Turns a [Weequery](https://github.com/rvoisin-mcl/Weequery) condition into an OData `$filter`, so the same filter
a caller sends to your database can be sent to an OData service.

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

**No dependencies.** A `$filter` is text, and the OData client libraries are large, versioned against their own
EDM model, and not needed to write one. What comes out goes on a query string.

## The allow-list is declared, not derived

There is no metadata document to read from here, so you say what a key means:

| | |
|---|---|
| **Key** | the name a caller writes, matched without regard to case |
| **Field** | the property's path, slash separated: `Lair/Address/City` |
| **Kind** | `String`, `Number`, `Boolean`, `Guid`, `Date`, `DateTimeOffset`, `TimeOfDay`, `Duration`, `Enum`, `Collection` |
| **Collection** | the collection this field lives inside, for quantifiers |
| **EnumType** | the qualified type name, for `Enum` |

A condition naming a key nobody declared is refused, exactly as an unbound field is. The kind matters more here
than in most places, because **OData writes almost every type differently and gets it wrong loudly**:

```
Name eq 'Alice'                                     String, quoted
Salary eq 10000                                     Number, bare
Active eq true                                      Boolean, lower case
HiredOn eq 2024-01-15                               Date, bare
MinionID eq 0f8fad5b-d9cb-469f-a165-70867728950e    Guid, bare, the v3 quotes are gone
Classification eq Lair.Model.Rank'High'             Enum, qualified where you named the type
ShiftLength eq duration'PT8H'                       Duration
```

Hand a service the wrong shape and it answers 400, not nothing. A value that would end the expression early, a
quote or a comma in what should be a bare literal, is refused here instead, where the message can say why.

## Nulls are translated, not assumed

This is the part worth having. **OData disagrees with Weequery about a null, and says so in the specification:**
*"null values are equal to null and not equal to any other value"*. So `Alias ne 'Ghost'` is **true** of a record
with no alias. In Weequery it is false, because every comparison carries a guard.

So every negative operator is written with its guard beside it:

```
Alias <> 'Ghost'      -> (Alias ne null and Alias ne 'Ghost')
```

And `NOT` is deliberately **not** guarded, for the same reason it is not in Weequery: negating a condition negates
its guard with it, so `NOT (Alias = 'Ghost')` is meant to bring the records with no alias back.

```
NOT (Alias = 'Ghost')  ->  not Alias eq 'Ghost'
```

That distinction is [the one Weequery makes a fuss about](../README.md#how-nulls-behave), and it survives the
trip. Services vary in how much of this they get right alone, particularly for the string functions; a guard a
service would have applied anyway costs a few characters, and depending on it would cost a different answer per
service.

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

**One thing OData does that neither SQL nor Elasticsearch manage easily:** comparing two properties.
`Name = [Alias]` becomes `Name eq Alias`, and it just works.

## Versions

`ODataVersion.V401` by default, which is what most services have spoken for years. Pass `V4` and two things
change: `in` is expanded into `(f eq a or f eq b)`, and `IsMatch` is refused rather than emitting a function 4.0
does not have.

Worth knowing that the expansion makes a long list a long URL, a thousand values, which is what Weequery will
carry, is not something every server accepts on a query string however it is spelled.

`matchesPattern` is ECMAScript syntax by specification and is one of the more thinly implemented parts of 4.01,
so a service may answer 501 to a filter that is perfectly legal.

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

The whole inner condition goes inside the lambda, which is exactly what Weequery's quantifier means, one element
satisfying all of it, not several elements between them. `all` over an empty collection is true and `any` over
one is false, which is what the quantifiers mean of nothing in Weequery too, so the three line up with no help.

A field declared inside a collection is only reachable within a quantifier over it, and one declared outside is
not reachable inside. The two scopes do not leak, exactly as they do not in Weequery.

## What it refuses

- **A key nobody declared**, as ever.
- **An operator that does not fit the declared kind**, `StartsWith` on a number, ordering on a boolean.
- **A value that cannot be written as its kind**, including one that would end the expression early.
- **A collection compared** rather than quantified, and a non-collection quantified.
- **An index** (`Tags[0]`). A `$filter` has no way to address one element of a collection.
- **A quantifier inside a quantifier.** OData can nest lambdas, but a Weequery collection declares no collections
  of its own, so there is nothing that could have been meant.
- **Sorting on or selecting a field inside a collection.** `$orderby` would have to say which of the many values,
  and reading one is `$expand` with a select of its own.

## The whole set of query options

```csharp
var parsed = ParsedQuery.Parse(request.Query);

ODataQuery.ToQueryString(fields, parsed.Condition, parsed.Sorts,
    Projection.Parse(request.Fields), pageSize: 20, page: 2);
```
```
$filter=Active eq true&$orderby=Salary desc,Name asc&$top=20&$skip=40&$select=Name,Alias
```

`Build` gives the same thing as a dictionary if you would rather place the options yourself. The order is fixed,
so the same query gives the same string every time, which is what makes one comparable and cacheable. The first
page writes no `$skip`, since skipping nothing is what not saying so already means.

> [!IMPORTANT]
> **Nothing here is percent encoded.** Encoding is the job of whatever builds the URL, and doing it here would
> mean a caller who does it properly encodes it twice.

## Licence

MIT, same as Weequery.
