# Weequery.Elasticsearch

Translates a [Weequery](https://github.com/rvoisin-mcl/Weequery) condition into Elasticsearch Query DSL, so the
filter a caller sends to a database can be sent to a search index unchanged, with the same meaning. A condition is a
tree rather than SQL, so a database is not the only backend it can target.

## Installation

```bash
dotnet add package Weequery.Elasticsearch
```

**The package has no dependencies.** The Query DSL is JSON and `System.Text.Json` ships with the framework, so the
package is not tied to a client generation (NEST, then `Elastic.Clients.Elasticsearch`, and any successor) or to its
licence. The output goes in the `query` slot of a search body, sent by any client or directly over HTTP. It works
against OpenSearch for the same reason.

## Example

```csharp
var fields = new ElasticFieldSet
{
    new("Name",     "name",          ElasticFieldKind.Text),
    new("Alias",    "alias.keyword"),
    new("Pay",      "salary",        ElasticFieldKind.Number),
    new("IsActive", "active",        ElasticFieldKind.Boolean),
};

var condition = ConditionFunctions.ParseQuery("IsActive = true AND Pay > 10000");

ElasticQuery.ToJson(condition, fields);
```
```jsonc
{ "bool": { "filter": [ { "term": { "active": true } },
                        { "range": { "salary": { "gt": 10000 } } } ] } }
```

## Declaring fields

There is no entity to walk and no mapping to read, so the application declares what each key means. An
**`ElasticField`** has these members:

| Member | Description |
|---|---|
| **Key** | The name a caller writes, matched case-insensitively |
| **Field** | Its path in the index, often different from the caller's name |
| **Kind** | `Keyword`, `Text`, `Number`, `Boolean`, `Date` |
| **Nested** | The `nested` path it lives under, for quantifiers |
| **HoldsValues** | An array of values rather than one, quantified over as `Value`; see `ElasticField.ForValues` |

A condition naming an undeclared key is refused, as an unbound field is. The kind determines two things:

- How a value is written: a `Number` becomes a JSON number, not the text it arrived as.
- Which operators the field accepts: `StartsWith` on a `Number` is refused up front.

> [!TIP]
> A `term` against an analysed field looks for the whole string among its tokens and finds nothing. Equality on a
> `Text` field is therefore written as `match_phrase`, and the analyser decides what counts as equal. For exact
> matching, index a `keyword` sub-field and declare **that** path.

## Null semantics

Weequery's operators carry a null guard: a comparison is true only where the property has a value, so a null
satisfies nothing except `IsNull`, and **the negative operators do not match it either**. Elasticsearch agrees on
the positive operators, since a document missing a field matches no `term` and no `range`. It does **not** agree on
the negative ones: a bare `must_not` matches documents that do not have the field.

Negative operators are therefore written with an `exists` beside them:

```jsonc
// Alias <> 'Ghost'
{ "bool": { "filter":   [ { "exists": { "field": "alias.keyword" } } ],
            "must_not": [ { "term":   { "alias.keyword": "Ghost" } } ] } }
```

`NOT` is deliberately **not** guarded, for the same reason as in the core: negating a condition negates its guard,
so `NOT (Alias = 'Ghost')` is *meant* to return the documents with no alias, and a bare `must_not` is correct.

The distinction between `Alias <> 'Ghost'` and `NOT (Alias = 'Ghost')` is described in the core README under
[null behaviour](../README.md#null-semantics), and survives translation intact.

## Operators

| Weequery | Query DSL |
|---|---|
| `=` | `term`, or `match_phrase` on a `Text` field |
| `<` `<=` `>` `>=` `IsBetween` | `range` |
| `IsNull` / `IsNotNull` | `must_not exists` / `exists` |
| `IsIn` | `terms`. An empty list matches nothing, on both sides |
| `StartsWith` | `prefix` |
| `EndsWith` / `Contains` | `wildcard`. A wildcard **in the value** is escaped, so `Contains '*'` means the character |
| `IsMatch` | `regexp` |
| `AND` | `bool.filter`, a filter rather than a `must`, since nothing here asks how well a document matched |
| `OR` | `bool.should` with `minimum_should_match: 1`, without which a `should` is optional and matches everything |
| `NOT` and every negative | `bool.must_not`, plus the `exists` above where it is an operator |

> [!WARNING]
> `EndsWith` and `Contains` become leading-wildcard queries, which cannot use the index and are scanned. This is a
> property of the Query DSL; Elasticsearch recommends an ngram analyser where substring search matters.

`IsMatch` passes the pattern through unchanged. Elasticsearch regular expressions are their own dialect and are
anchored to the whole value rather than searched, so a pattern that works in .NET may mean something different
there. Test patterns against the index.

## Quantifiers over nested documents

Quantifiers become nested queries:

```csharp
new("LairName", "assignments.lair", ElasticFieldKind.Keyword, Nested: "assignments")
```
```
Assignments Any (LairName = 'Volcano')
```
```jsonc
{ "nested": { "path": "assignments", "query": { "term": { "assignments.lair": "Volcano" } } } }
```

| Quantifier | Query DSL |
|---|---|
| `Any` | a `nested` query |
| `None` | `must_not` of a `nested` query |
| `All` | **`must_not nested(must_not inner)`**: no element fails the condition |

The `All` form is the only way to express "every element passes" over nested documents. It also makes `All` true of
a document with no elements, which matches the meaning of `All` over an empty collection in the core.

> [!IMPORTANT]
> Elasticsearch flattens objects in an array unless the mapping declares them `nested`. A flattened array cannot
> answer "one element that is **both** of these", which is why a Weequery quantifier holds one condition rather
> than several separate tests. If the index does not map the path as `nested`, the query still runs and silently
> answers the weaker question. The package cannot detect this; check the index mapping.

### An array of values

For arrays of plain values rather than objects, such as tags or scores. Elasticsearch has no array type: a field
holds one value or several under the same mapping, so there is no `nested` path to declare or map. Declare the field
with `ElasticField.ForValues`:

```csharp
ElasticField.ForValues("Tags",   "tags"),                             // keyword by default
ElasticField.ForValues("Scores", "scores", ElasticFieldKind.Number),
```

The element is named **`Value`**. A query against the field already asks whether *any* value matches, which is
exactly `Any` for a single test; the other forms are derived from it:

| You write | You get |
|---|---|
| `Tags Any (Value = 'red')` | `term`, against the field itself |
| `Tags Any (Value = 'red' OR Value StartsWith 'b')` | `should`, some value passing either being either passing |
| `Scores Any (Value IsBetween (6, 9))` | one `range`, which one value has to fall inside |
| `Tags None (...)` | `must_not` of the `Any` |
| `Scores All (Value >= 50)` | `must_not` `range lt 50`: no value fails it |
| `Scores All (Value > 1 AND Value < 5)` | two of those, every value passing both being every value passing each |

The following are refused:

| Refused | Reason | Use instead |
|---|---|---|
| `AND` inside `Any`, as in `Scores Any (Value > 5 AND Value < 10)` | Each clause against an array field is answered by whichever value suits it, so this would be true for `[3, 12]` | `IsBetween` |
| A negative test inside `Any` | `must_not` means *no* value matches, not *some value fails* | `None` with the positive test |
| Inside `All`: the substring operators, a list, or an `OR` | `All` needs the failing test written as a query, which exists only for a range, `IsBetween`, and `=` on anything but analysed text | `None` with the equivalent test |
| `Value IsNull` | Nulls in arrays are not indexed (see below) | |
| Comparing the array directly, as in `Tags = 'red'` | It is not silently treated as `Any` | `Tags Any (Value = 'red')` |

> [!NOTE]
> Elasticsearch does not index a null inside an array. `Value IsNull` is refused, since there is nothing to find,
> and an array holding a null answers `All` as though the null were absent, where the core would have the null fail
> the test.

## What it refuses

| Refused | Reason |
|---|---|
| A key nobody declared | As for an unbound field |
| An operator that does not fit the declared kind | For example `StartsWith` on a number, ordering on a boolean |
| A value that does not parse as the declared kind | |
| Comparing two fields (`Pay > [Salary]`) | The Query DSL cannot do it without a script, and the package does not write scripts |
| An index (`Tags[0]`) | An array is flattened into the field, so there is no element zero. Declare the element's own path as a separate field |
| A quantifier inside a quantifier, or a field from outside a nested path used inside it | A nested query addresses one path at a time |
| Sorting on a nested field | A nested sort must say which of the many values to order by, and a Weequery `Sort` does not carry that |

## Search body

`ElasticSearchBody.ToJson` writes a whole search body. Sorts, the window and the projection are held to the same
allow-list as the condition:

```csharp
var parsed = ParsedQuery.Parse(request.Query);

ElasticSearchBody.ToJson(fields, parsed.Condition, parsed.Sorts,
    Projection.Parse(request.Fields), pageSize: 20, page: 2);
```
```jsonc
{ "query": { "term": { "active": true } },
  "sort": [ { "salary": { "order": "desc" } } ],
  "from": 40, "size": 20,
  "_source": { "includes": [ "name", "alias.keyword" ] } }
```

Anything not requested is omitted, so a body with no sorts, window or projection is just a `query`.

> [!NOTE]
> `_source` filtering is **not** equivalent to a narrower SELECT: Elasticsearch reads the whole `_source` and
> returns part of it, so it saves bytes on the wire, not the read.

> [!WARNING]
> `from`/`size` paging is limited by `index.max_result_window`, 10,000 by default. Beyond that, raise the index
> setting or use `search_after`; the package makes neither choice.

## Licence

MIT, as Weequery.
