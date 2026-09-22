# Weequery.Elasticsearch

*[swivels to face the second monitor]*

A condition is a tree. A tree is not SQL. And once you understand that, gentlemen, a database stops being the
only thing you can point one at.

This turns a [Weequery](https://github.com/rvoisin-mcl/Weequery) condition into Elasticsearch Query DSL, so the
very same filter a caller sends to your database can be sent to your index, unchanged, and mean the same thing
when it arrives.

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

**No dependencies.** None. The Query DSL is JSON and `System.Text.Json` comes with the framework, so nothing here
chains you to a client generation (NEST, and then `Elastic.Clients.Elasticsearch`, and then whatever they think
of next) or to its licence. What comes out goes in the `query` slot of a search body, by whichever client you
like, or straight over HTTP like a civilised person. It works against OpenSearch for exactly the same reason.

## The allow-list is declared, not derived

There is no entity to walk here and no mapping to read, so **you** tell me what a key means:

| | |
|---|---|
| **Key** | the name a caller writes, matched without regard to case |
| **Field** | its path in the index, which is very often not what the caller calls it |
| **Kind** | `Keyword`, `Text`, `Number`, `Boolean`, `Date` |
| **Nested** | the `nested` path it lives under, for quantifiers |

A condition naming a key nobody declared is refused, exactly as an unbound field is refused everywhere else in
this operation. The kind decides two things: how a value is written (a `Number` becomes a JSON number, not the
text it arrived as) and which operators the field will tolerate, so `StartsWith` on a `Number` is refused up
front rather than answered oddly and left to haunt you.

`Text` deserves a special mention. A `term` against an analysed field goes looking for the whole string among its
tokens and finds nothing at all, which is the classic Elasticsearch humiliation. So equality on a `Text` field
becomes `match_phrase` instead, and the analyser decides what counts as equal. Where you want exactness, index a
`keyword` sub-field and name **that**. I can only protect you from so much.

## Nulls are translated, not assumed

This is the part worth having, and the part a hand-rolled translation gets wrong every single time.

My operators carry a guard: a comparison is true only where the property has a value, so a null satisfies nothing
except `IsNull`, and **the negative operators do not catch one either**. Elasticsearch agrees with me free of
charge on the positive operators, a document missing a field matching no `term` and no `range`. It does **not**
agree on the negative ones, where a bare `must_not` cheerfully matches documents that have no such field at all.

So I write the negatives with an `exists` beside them:

```jsonc
// Alias <> 'Ghost'
{ "bool": { "filter":   [ { "exists": { "field": "alias.keyword" } } ],
            "must_not": [ { "term":   { "alias.keyword": "Ghost" } } ] } }
```

And `NOT` is deliberately **not** guarded, for precisely the reason it is not guarded in the core: negating a
condition negates its guard along with it, so `NOT (Alias = 'Ghost')` is *meant* to bring the documents with no
alias back, and a bare `must_not` is exactly right.

That distinction, `Alias <> 'Ghost'` and `NOT (Alias = 'Ghost')` answering differently, is
[the one I make a fuss about](../README.md#how-nulls-behave), and it survives the trip intact. A filter that
changes its mind when it changes backend is worse than no filter at all.

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
the Query DSL's own bargain rather than anything I arranged, and it is why Elasticsearch suggests an ngram
analyser where substring search actually matters. Take that up with them.

`IsMatch` passes the pattern straight through. Elasticsearch's regular expressions are its own dialect and are
anchored whole rather than searched, so a pattern that behaves in .NET does not always mean the same thing once
it gets there. Test it. Do not assume. Assumption is how people end up in tanks.

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

`Any` is a nested query, `None` is `must_not` of one, and `All` is **`must_not nested(must_not inner)`**, which
is to say "no element fails it", which is the only way anybody has ever found to say "every element passes" over
nested documents. Inelegant. Correct. That shape is also what makes `All` true of a document with no elements
whatsoever, which is what `All` means of nothing in the core too, so at least we are consistently peculiar.

> [!IMPORTANT]
> Elasticsearch flattens objects in an array unless the mapping says `nested`. A flattened array cannot answer
> "one element that is **both** of these", which is the entire reason a Weequery quantifier holds one condition
> rather than several separate tests. If the index does not map the path `nested`, the query still runs and
> answers the weaker question, quietly, with a straight face. Nothing here can detect that. Check your mapping.
> Check it now.

## What it refuses

- **A key nobody declared**, as ever, and as it ever shall be.
- **An operator that does not fit the declared kind**, `StartsWith` on a number, ordering on a boolean.
- **A value that will not parse** as the declared kind.
- **Comparing two fields** (`Pay > [Salary]`). The Query DSL cannot manage it without a script, and I do not do
  scripts.
- **An index** (`Tags[0]`). An array is flattened into the field, so there is no element zero to address.
  Declare the element's own path as a field of its own.
- **A quantifier inside a quantifier**, and a field from outside a nested path asked inside it. A nested query
  addresses one path at a time, and one is all it is getting.
- **Sorting on a nested field.** A nested sort has to say which of the many values to order by, and a Weequery
  `Sort` does not carry that. I refuse to guess.

## The whole search body

Sorts, the window and the projection come along too, all of them held to the same allow-list, because there is
only ever the one allow-list:

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

Two things worth knowing before you get excited. `_source` filtering is **not** the same bargain as a narrower
SELECT: Elasticsearch reads the whole `_source` and hands you back part of it, so what you save is bytes on the
wire rather than the read itself. And `from`/`size` paging walks straight into `index.max_result_window`, ten
thousand by default, which is either the index's setting to raise or a reason to reach for `search_after`.
Neither is a decision I am prepared to make on your behalf.

Anything left unasked is left off entirely, so a body with no sorts, no window and no projection is simply a
`query`. I do not pad.

## Licence

MIT, same as the rest of the operation.
