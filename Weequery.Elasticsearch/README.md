# Weequery.Elasticsearch

Turns a [Weequery](https://github.com/rvoisin-mcl/Weequery) condition into Elasticsearch Query DSL, so the same
filter a caller sends to your database can be sent to your index.

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

**No dependencies.** The Query DSL is JSON and `System.Text.Json` is in the framework, so nothing here pins you
to a client generation (NEST, then `Elastic.Clients.Elasticsearch`) or to its licence. What comes out goes in the
`query` slot of a search body, whichever client puts it there, or straight over HTTP. It works against OpenSearch
for the same reason.

## The allow-list is declared, not derived

There is no entity to walk and no mapping to read, so you say what a key means:

| | |
|---|---|
| **Key** | the name a caller writes, matched without regard to case |
| **Field** | its path in the index, which is often not what the caller calls it |
| **Kind** | `Keyword`, `Text`, `Number`, `Boolean`, `Date` |
| **Nested** | the `nested` path it lives under, for quantifiers |

A condition naming a key nobody declared is refused, exactly as an unbound field is. The kind decides two things:
how a value is written (a `Number` becomes a JSON number, not the text it arrived as) and which operators the
field will take, so `StartsWith` on a `Number` is refused up front rather than answered oddly.

`Text` is worth calling out. A `term` against an analysed field looks for the whole string among its tokens and
finds nothing, which is the classic Elasticsearch mistake. Equality on a `Text` field becomes `match_phrase`
instead, and the analyser decides what counts as equal. Where you want exactness, index a `keyword` sub-field and
name **that**.

## Nulls are translated, not assumed

This is the part worth having, and the part a hand-rolled translation usually gets wrong.

Weequery's operators carry a guard: a comparison is true only where the property has a value, so a null satisfies
nothing except `IsNull`, and **the negative operators do not catch one either**. Elasticsearch agrees for free on
the positive operators, a document missing a field matches no `term` and no `range`. It does **not** agree on the
negative ones: a bare `must_not` matches documents that have no such field at all.

So the negatives are written with an `exists` beside them:

```jsonc
// Alias <> 'Ghost'
{ "bool": { "filter":   [ { "exists": { "field": "alias.keyword" } } ],
            "must_not": [ { "term":   { "alias.keyword": "Ghost" } } ] } }
```

And `NOT` is deliberately **not** guarded, for the same reason it is not in Weequery: negating a condition negates
its guard with it, so `NOT (Alias = 'Ghost')` is meant to bring the documents with no alias back, and a bare
`must_not` is exactly right.

That distinction (`Alias <> 'Ghost'` and `NOT (Alias = 'Ghost')` answering differently) is
[the one Weequery makes a fuss about](../README.md#how-nulls-behave), and it survives the trip.

## The operators

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

`EndsWith` and `Contains` become leading-wildcard queries, which cannot use the index and are scanned. That is
the Query DSL's own trade rather than this library's, and it is why Elasticsearch suggests an ngram analyser
where substring search matters.

`IsMatch` passes the pattern through. Elasticsearch's regular expressions are its own dialect and are anchored
whole rather than searched, so a pattern that works in .NET does not always mean the same thing here.

## Quantifiers become nested queries

```csharp
new("LairName", "assignments.lair", ElasticFieldKind.Keyword, Nested: "assignments")
```
```
Assignments Any (LairName = 'Volcano')
```
```jsonc
{ "nested": { "path": "assignments", "query": { "term": { "assignments.lair": "Volcano" } } } }
```

`Any` is a nested query, `None` is `must_not` of one, and `All` is **`must_not nested(must_not inner)`**, "no
element fails it", which is the only way to say "every element passes" over nested documents. That shape is also
what makes `All` true of a document with no elements at all, which is what `All` means of nothing in Weequery too.

> [!IMPORTANT]
> Elasticsearch flattens objects in an array unless the mapping says `nested`. A flattened array cannot answer
> "one element that is **both** of these", which is the entire reason a Weequery quantifier holds one condition
> rather than several tests. If the index does not map the path `nested`, the query still runs and answers the
> weaker question. Nothing here can detect that; check your mapping.

## What it refuses

- **A key nobody declared**, as ever.
- **An operator that does not fit the declared kind**, `StartsWith` on a number, ordering on a boolean.
- **A value that will not parse** as the declared kind.
- **Comparing two fields** (`Pay > [Salary]`). The Query DSL cannot without a script.
- **An index** (`Tags[0]`). An array is flattened into the field, so there is no element zero to address. Declare
  the element's own path as a field of its own.
- **A quantifier inside a quantifier**, and a field from outside a nested path asked inside it. A nested query
  addresses one path at a time.
- **Sorting on a nested field.** A nested sort has to say which of the many values to order by, and a Weequery
  `Sort` does not carry that.

## The whole search body

Sorts, the window and the projection come along too, all held to the same allow-list:

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

Two things worth knowing. `_source` filtering is **not** the same trade as a narrower SELECT, Elasticsearch
reads the whole `_source` and hands back part of it, so it saves the bytes on the wire rather than the read. And
`from`/`size` paging runs into `index.max_result_window`, ten thousand by default, which is the index's setting to
raise or a reason to reach for `search_after`; neither is something this can decide for you.

Anything left unasked is left off, so a body with no sorts, no window and no projection is just a `query`.

## Licence

MIT, same as Weequery.
