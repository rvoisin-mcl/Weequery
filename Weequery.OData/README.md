# Weequery.OData

Translates a [Weequery](https://github.com/rvoisin-mcl/Weequery) condition into an OData `$filter`, so the filter a
caller sends to a database can be sent to an OData service unchanged.

## Installation

```bash
dotnet add package Weequery.OData
```

**The package has no dependencies.** A `$filter` is text; the OData client libraries are large, versioned against
their own EDM model, and not needed to write one. The output goes on a query string.

## Example

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

## Declaring fields

There is no metadata document to derive the allow-list from, so the application declares what each key means. An
**`ODataField`** has these members:

| Member | Description |
|---|---|
| **Key** | The name a caller writes, matched case-insensitively |
| **Field** | The property's path, slash separated: `Lair/Address/City` |
| **Kind** | `String`, `Number`, `Boolean`, `Guid`, `Date`, `DateTimeOffset`, `TimeOfDay`, `Duration`, `Enum`, `Collection` |
| **Collection** | The collection this field lives inside, for quantifiers |
| **ElementKind** | What each element holds, for a collection of values; see `ODataField.ForValues` |
| **EnumType** | The qualified type name, for `Enum` |

A condition naming an undeclared key is refused, as an unbound field is. The kind matters because **OData writes
almost every type differently, and a service rejects the wrong form**:

```
Name eq 'Alice'                                     String, quoted
Salary eq 10000                                     Number, bare
Active eq true                                      Boolean, lower case
HiredOn eq 2024-01-15                               Date, bare
MinionID eq 0f8fad5b-d9cb-469f-a165-70867728950e    Guid, bare, the v3 quotes are gone
Classification eq Lair.Model.Rank'High'             Enum, qualified where you named the type
ShiftLength eq duration'PT8H'                       Duration
```

A service sent the wrong form answers 400. A value that would end the expression early, such as a quote or a comma
in what should be a bare literal, is refused by the package instead, with a message that says why.

## Null semantics

**OData's null semantics differ from Weequery's, and the specification states them:** *"null values are equal to
null and not equal to any other value"*. Under OData, `Alias ne 'Ghost'` is **true** of a record with no alias;
under Weequery it is false, because every comparison carries a null guard.

To preserve Weequery's meaning, every negative operator is written with its guard:

```
Alias <> 'Ghost'      -> (Alias ne null and Alias ne 'Ghost')
```

`NOT` is deliberately **not** guarded, for the same reason as in the core: negating a condition negates its guard,
so `NOT (Alias = 'Ghost')` is *meant* to return the records with no alias.

```
NOT (Alias = 'Ghost')  ->  not Alias eq 'Ghost'
```

The distinction is described in the core README under [null behaviour](../README.md#null-semantics), and survives
translation.

> [!NOTE]
> Services differ in how much of this they handle unaided, particularly for the string functions. An unnecessary
> guard costs a few characters; relying on the service would give a different answer per service.

## Operators

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

Comparing two properties is supported directly: `Name = [Alias]` becomes `Name eq Alias`.

## OData versions

The default is `ODataVersion.V401`. Passing `V4` changes two things:

- `in` is expanded into `(f eq a or f eq b)`.
- `IsMatch` is refused, rather than emitting a function 4.0 does not define.

> [!WARNING]
> The expansion turns a long list into a long URL. A thousand values, the maximum Weequery accepts, may exceed what
> a server accepts on a query string however it is written.

`matchesPattern` uses ECMAScript syntax by specification and is thinly implemented in 4.01, so a service may answer
501 to a filter that is legal.

## Quantifiers over collections

Quantifiers become lambdas:

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

The whole inner condition goes inside the lambda, matching the meaning of a Weequery quantifier: *one* element
satisfying all of it, not several elements each satisfying part.

- `all` over an empty collection is true and `any` over one is false, as for the quantifiers in the core.
- A field declared inside a collection is reachable only within a quantifier over that collection, and a field
  declared outside is not reachable inside one. The two scopes do not mix.

### A collection of values

A `Collection(Edm.String)`, or a collection of numbers, dates or enums, has no element fields to declare. Declare it
with `ODataField.ForValues`, giving the kind each element holds. Inside the quantifier the element is named
**`Value`**, and it is written as the lambda's variable itself:

```csharp
ODataField.ForValues("Tags",   "Tags",   ODataFieldKind.String),
ODataField.ForValues("Scores", "Scores", ODataFieldKind.Number),
ODataField.ForValues("Ranks",  "Ranks",  ODataFieldKind.Enum, "Lair.Model.Rank"),
```
```
Tags   Any  (Value = 'red')                  Tags/any(d1: d1 eq 'red')
Scores All  (Value >= 50)                    Scores/all(d1: d1 ge 50)
Scores Any  (Value > 5 AND Value < 10)       Scores/any(d1: (d1 gt 5 and d1 lt 10))
```

A lambda binds everything inside it to one element, so the whole language works inside it, `AND` included. `Value`
is the only name available inside one, and means nothing outside it.

## What it refuses

| Refused | Reason |
|---|---|
| A key nobody declared | As for an unbound field |
| An operator that does not fit the declared kind | For example `StartsWith` on a number, ordering on a boolean |
| A value that cannot be written as its kind | Including one that would end the expression early |
| A collection compared rather than quantified, or a non-collection quantified | |
| An index (`Tags[0]`) | A `$filter` cannot address one element of a collection |
| A quantifier inside a quantifier | OData can nest lambdas, but a Weequery collection declares no collections of its own, so nothing could be meant |
| Sorting on or selecting a field inside a collection | `$orderby` would have to say which of the many values; reading one is `$expand` with its own select |

## Query options

`ODataQuery.ToQueryString` writes the full set of query options:

```csharp
var parsed = ParsedQuery.Parse(request.Query);

ODataQuery.ToQueryString(fields, parsed.Condition, parsed.Sorts,
    Projection.Parse(request.Fields), pageSize: 20, page: 2);
```
```
$filter=Active eq true&$orderby=Salary desc,Name asc&$top=20&$skip=40&$select=Name,Alias
```

- `Build` returns the same options as a dictionary, for placing them individually.
- The order of options is fixed, so the same query always gives the same string, which makes it comparable and
  cacheable.
- The first page writes no `$skip`.

> [!IMPORTANT]
> **Nothing is percent encoded.** Encoding is the job of whatever builds the URL; encoding here would make a caller
> that encodes correctly encode everything twice.

## Licence

MIT, as Weequery.
