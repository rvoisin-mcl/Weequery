# Weequery for TypeScript

The [C# library](../README.md) turns a filter that arrives as data into an `IQueryable`. This package is the client
side of that exchange: it builds the filter, and it reports what is wrong with it **before** a round trip to the
server. It has no dependencies.

```bash
npm install weequery
```

## Building and parsing conditions

A **condition** is a tree. Build one from the exported functions:

```ts
import { and, contains, eq, gt, isNull, not, or, toQuery, pack } from 'weequery';

const condition = and(
  gt('Salary', 10000),
  not(contains('Name', 'temp')),
  or(eq('IsActive', true), isNull('Alias')),
);

toQuery(condition);
// "(([Salary] > 10000) AND (NOT ([Name] Contains 'temp')) AND (([IsActive] = True) OR ([Alias] IsNull)))"

JSON.stringify(pack(condition));
// the shape a TransportCondition's Condition member takes
```

Or parse one from a string, as received from a form or a query string:

```ts
import { parseQuery, toQuery } from 'weequery';

const condition = parseQuery("(Salary > 10000) AND (IsActive = true)");

toQuery(condition!);   // writes it back out, in the one canonical spelling
```

The two forms are interchangeable. A tree writes to a string, a string parses to a tree, and either one packs to the
JSON the server reads.

## Validating against bindings

`validateQuery` takes the same binding list the server declares and predicts the server's response:

```ts
import { BindingSet, validateQuery } from 'weequery';

const bindings = new BindingSet([
  { key: 'Name', type: 'string' },
  { key: 'Salary', type: 'number' },
  { key: 'IsActive', type: 'boolean', nullable: false },
  { key: 'Threshold', type: 'number', constant: true },
  { key: 'LairAssignments', sortable: false },
]);

const result = validateQuery(userTypedThis, { bindings });

if (!result.ok) {
  for (const problem of result.diagnostics) {
    console.log(problem.code, problem.message, problem.position);
  }
}
```

Validation collects every problem rather than stopping at the first, which suits a form. A syntax error carries the
index at which it occurred, so an editor can point at the offending character.

| Code | Meaning |
|---|---|
| `syntax` | The text is not a query; `position` says where. |
| `unbound-field` | A field no binding claims. |
| `unbound-operand` | An operand such as `[Bonus]` in `Pay > [Bonus]` where `Bonus` is not bound. |
| `value-count` | Too few or too many operands for the operator. |
| `list-too-long` | An `IsIn` list longer than 1000. |
| `nesting` | Nesting deeper than 16 levels. |
| `type-mismatch` | `Contains` on a number, an ordering on a boolean, a null test on something that cannot be null. |
| `not-sortable` | A sort on a collection or navigation property, which has no ordering of its own. |
| `sort-on-constant` | A sort on a value supplied by code, which is the same for every row. |
| `empty-conjunction` | Warning: matches everything or nothing, and has no query string form. |
| `paging-without-sort` | Warning: page two may repeat a row from page one. |
| `not-a-collection` | A quantifier names a key bound as an ordinary property. See [Quantifiers over collections](#quantifiers-over-collections). |
| `collection-not-comparable` | A comparison names a key bound as a collection. |
| `not-projectable` | A projection names a field that cannot be projected. See [Projections](#projections). |
| `projection-only` | A condition, operand or sort names a key bound for projection only. See [Binding uses](#binding-uses). |

A **binding key** is one or more unquoted SQL names separated by periods, so a nested property can be bound under its
existing path:

```ts
new BindingSet(['Name', 'Lair.Capacity', 'Lair.Name']);
```

Every segment must satisfy the whole rule, so `.Name`, `Name.`, `Lair..Name` and `Lair.1Name` are refused.
`isSqlName` tests a single name with no period; `isBindingKey` tests a key. A period is not a delimiter, so a dotted
key reads and writes unquoted like any other.

Every member of `Binding` except `key` is optional. Without a `type`, the type rules are skipped, but the field is
still checked for being bound.

> [!IMPORTANT]
> Client-side validation grants nothing. The server's binding list is authoritative; a `BindingSet` only predicts it,
> and one that has drifted from the server's makes inaccurate predictions.

## The Native grammar

There is no `QueryStyle` option. Everything this library reads and writes uses the Native spelling: one form per
operator, so two conditions with the same meaning have the same text and can be compared as strings.

```ts
toQuery(condition);          // "(([Pay] > 10000) AND ([IsActive] = True))"
parseQuery(text);            // the same grammar, read back
```

An operator is one word: `AND`, `OR`, `NOT`, `=`, `<>`, and each named operator under its own name. `IN`, `BETWEEN`,
`==` and `!=` are also single words and are accepted on input; output always uses the canonical form. The parser
refuses any operator containing a space, and the three conjunction symbols:

```ts
parseQuery('A = 1 && B = 2');
// WeequeryError: '&&' at position 6 is not valid in the Native style, write 'AND'

parseQuery('Alias IS NULL');
// WeequeryError: 'IS NULL' is not valid in the Native style, write 'IsNull'

parseSorts('ORDER BY Pay DESC');
// WeequeryError: 'ORDER BY' at position 0 is not valid in the Native style, write 'OrderBy'
```

Every refusal names the replacement spelling.

> [!NOTE]
> The C# side still accepts the older spellings in places. Its `QueryRequest` is Native only, like this library;
> `ApplyCondition(text)` on a bare string is not, and by default reads every spelling it ever accepted. Against a
> server that takes a request, the two sides agree exactly. Against one that takes a bare condition string, this
> library is the stricter of the two: a form may refuse `&&` that the server would have accepted.

## Sorts, projections and combined queries

```ts
import { asc, desc, parseSorts, sortsToQuery, parseParsedQuery, parsedQueryToQuery } from 'weequery';

parseSorts('Salary DESC, Name');            // [{ field: 'Salary', direction: 1 }, { field: 'Name', direction: 0 }]
sortsToQuery([desc('Salary'), asc('Name')]); // "[Salary] DESC, [Name] ASC"

const parsed = parseParsedQuery('Salary > 10000 OrderBy Salary DESC Select Name, Salary');
parsed.condition;    // the filter
parsed.sorts;        // the ordering
parsed.projection;   // ['Name', 'Salary']

parsedQueryToQuery(parsed);
// "([Salary] > '10000') OrderBy [Salary] DESC Select [Name], [Salary]"
```

Every part is optional, and the order is fixed: condition, then sorts, then projection. Position is what identifies
each part.

```
Salary > 10000 OrderBy Salary DESC Select Name   all three
Salary > 10000 Select Name                       a condition and a projection, and no sorting
OrderBy Salary DESC Select Name                  sorts and a projection, and no filtering at all
Select Name, Salary                              a projection, and nothing else
```

- `ORDER BY` is two words and is refused; only `OrderBy` is accepted. `Select` has only ever had one spelling.
- A `Select` with nothing after it is refused rather than read as an empty projection.
- Each split is found by parsing the preceding part and seeing where it ends, not by searching the text for keywords.
  `Name = 'ORDER BY'` is one comparison and no sorts; `Name = 'Select'` is one comparison and no projection.
- Neither `OrderBy` nor `Select` may be a binding key, for the same reason.

## Value formatting

Every operand travels as text and is converted to the bound property's type on the server. A `number`, `boolean`,
`bigint` or `Date` passed to a builder is formatted invariantly, so a query means the same thing on every machine
and in every timezone.

A `Date` becomes an ISO 8601 string in UTC, which .NET reads back as a `DateTime` with millisecond precision. For
more precision, or a particular `Kind` or offset, pass the text directly.

A condition built from **strings** writes its values quoted; one built from **numbers** writes them bare. The C#
side behaves the same way. As a result `toQuery(parseQuery(toQuery(x)))` can differ from `toQuery(x)` in quoting,
and is stable from then on.

> [!TIP]
> Compare parsed conditions, or the rows they select, rather than the original strings.

## Indexing into collections

Where the server binds a list, an array or a dictionary, a condition may name one element of it. `at()` adds an
**index** to any comparison:

```ts
import { at, gt, isNull, toQuery } from 'weequery';

toQuery(at(gt('Tallies', 5), 'apples'));   // "([Tallies][apples] > 5)"
toQuery(at(isNull('Items'), '0'));         // "([Items][0] IsNull)"
```

The index is a separate function rather than an extra argument on every builder, because it means the same thing
under every operator, and a trailing optional argument such as `isBetween(field, low, high, index)` is easy to
miscount.

An index is read and written in all three positions the server accepts: a **condition**, an **operand** and a
**sort**.

```ts
parseQuery('Tallies[apples] > 5');       // { field: 'Tallies', index: 'apples', ... }
parseQuery('Pay > [Tallies][apples]');   // the operand carries it in its own text
parseSorts('Tallies[apples] DESC');      // and so does the sort field
```

On output an index is always the **second** bracket pair, `[Tallies][apples]`, never `[Tallies[apples]]`, because
only the former reads back. On the wire it is a separate member, absent when there is no index, so a payload without
one is byte for byte unchanged:

```jsonc
{ "Operator": 6, "Field": "Tallies", "Values": ["5"], "Conditions": [], "Index": "apples" }
```

- **A missing element is null.** This is the server's rule: an index with no element satisfies nothing except
  `IsNull`, the negative operators do not match it, and `not()` does. See
  [the C# README](../README.md#indexing-into-a-collection), which also lists what each database can translate and
  warns that a navigation collection has no defined order.
- **Validation skips the type rules on an indexed field.** A `BindingSet` records the collection's type, not the
  element's, so `Tallies[apples] StartsWith 'x'` is not reported as a mismatch. The key must still be bound.

## Quantifiers over collections

An index selects **one** element. The **quantifiers** `any`, `all` and `none` test the elements as a set:

```ts
import { and, any, eq, gt, none, startsWith, toQuery } from 'weequery';

toQuery(any('Assignments', and(gt('LairID', 5), startsWith('LairName', 'V'))));
// "([Assignments] Any (([LairID] > 5) AND ([LairName] StartsWith 'V')))"

parseQuery('Assignments None (LairID = 5)');
// { kind: 'quantified', operator: Operator.None, field: 'Assignments', condition: { ... } }
```

The parentheses around the inner condition are required in text. Without them the end of the inner condition cannot
be distinguished from whatever follows it.

### Element bindings

The inner condition has its own allow-list. Mirror the server's `BindCollection` with a binding's `elements`, and
fields inside the parentheses are checked against that list instead of the entity's:

```ts
const bindings = [
  { key: 'Name', type: 'string' },
  { key: 'Assignments', elements: ['LairID', { key: 'LairName', type: 'string' }] },
];

validateQuery("Assignments Any (Capacity > 1)", { bindings });
//  unbound-field: Capacity

validateQuery("LairName StartsWith 'V'", { bindings });
//  unbound-field: LairName        the inner list is not in scope out here
```

Two diagnostics apply: `not-a-collection` where a quantifier names a key bound as an ordinary property, and
`collection-not-comparable` where a comparison names a key bound as a collection. A key serves either the
quantifiers or everything else, with the exception below.

### Collections of values

A **collection of values**, such as a `List<string>` or an `int[]` on the server, has no element properties, so the
element itself is named `Value` (exported as `VALUE_KEY`). Declare it with `values`, giving the element type where
known:

```ts
const bindings = [
  { key: 'Tags',     values: 'string' },
  { key: 'Readings', values: { type: 'number', nullable: true } },
];

validateQuery("Tags Any (Value = 'red')", { bindings });          //  valid
validateQuery("Readings All (Value >= 50)", { bindings });        //  valid
validateQuery("Tags Any (Value > 5)", { bindings });              //  valid; a string has an ordering
validateQuery("Readings Any (Value StartsWith '1')", { bindings }); //  type-mismatch
validateQuery("Tags Any (Name = 'red')", { bindings });           //  unbound-field: only Value is in scope
any('Tags', eq(VALUE_KEY, 'red'));                                //  the same thing, built
```

The server also binds a collection of values as a property, so unlike a collection of objects it can be tested for
null, indexed (`Tags[0] = 'red'`) and projected. It cannot be compared whole (`Tags = 'red'` is
`collection-not-comparable`) or sorted on whole (`not-sortable`).

- A binding has `elements` or `values`; declaring both throws.
- `fieldsUsed()` stops at the collection and does not report the names inside it, since they are not bound on the
  entity. Call it again on `condition.condition` to get them.

On the wire a quantified condition uses the existing condition shape, so the payload format is unchanged:

```jsonc
{ "Operator": 23, "Field": "Assignments", "Values": [],
  "Conditions": [ { "Operator": 6, "Field": "LairID", "Values": ["5"], "Conditions": [] } ] }
```

> [!NOTE]
> A quantifier is never unknown. This is the server's rule: an empty collection and a missing one give the same
> answer, so `Any` is false for both and `All` and `None` are true for both. See
> [the C# README](../README.md#quantifiers-over-collections).

## Projections

A condition selects rows; a **projection** selects columns. It is a comma separated list of bound field names,
written in the same syntax as conditions and sorts:

```ts
import { parseProjection, projectionToQuery, validateProjection } from 'weequery';

parseProjection('Name, Pay');                 // ['Name', 'Pay']
projectionToQuery(['Name', 'Tallies[apples]']);   // "[Name], [Tallies][apples]"
```

Send it with the condition, and the server returns one entry per key instead of whole entities:

```ts
const body: TransportCondition = {
  Query: 'IsActive = true',
  Projection: projectionToQuery(selectedColumns),
};

// [ { "Name": "Alice Fox", "Pay": 12000 }, ... ]
```

The allow-list is the same, so `validateProjection` catches a bad field before the round trip:

```ts
validateProjection('Name, Morale', { bindings });
//  unbound-field: Morale

validateProjection('Assignments', { bindings });
//  not-projectable: Assignments is a collection, so it has no single value to read
```

- A field named twice is kept once, matched case-insensitively, so a list built from checkboxes needs no
  deduplication.
- Malformed text returns a `syntax` diagnostic with a position rather than throwing.
- An empty projection is read by the server as "every bound field".
- `Projection` is omitted from the payload when nothing was requested, so older payloads are unchanged.

See [the C# README](../README.md#projection) for what a row holds and how keys are spelled.

### Wildcards

`*` and `Prefix.*` name the allow-list rather than a field. `*` is every field the caller may read; `Lair.*` is every
such field under that prefix:

```ts
validateProjection('*', { bindings });          // no diagnostics
validateProjection('Name, Lair.*', { bindings });   // no diagnostics

validateProjection('Gizmo.*', { bindings });
//  unbound-field: 'Gizmo.*' matches nothing: no binding under 'Gizmo.' can be projected
```

The prefix keeps its trailing period, so `Lair.` does not match a key such as `Lairyard.Capacity`. Wildcards are not
expanded on the client, because the expansion is the binding list and the server's list is authoritative.
Validation checks only that a prefix matches at least one binding, which is the same check the server makes.

### Binding uses

A binding grants three separable permissions: filter, sort and project. The server specifies which with
`BindingUse`; mirror it here and the client refuses exactly what the server will:

```ts
const bindings = [
  { key: 'Name' },                                                    // all three, the default
  { key: 'Notes',  type: 'string', use: BindingUse.Projection },
  { key: 'Tenant', type: 'number', use: BindingUse.Test },
  { key: 'Morale', type: 'number', use: BindingUse.Test | BindingUse.Sort },
];

validateProjection('Name, Notes', { bindings });                //  valid
validateCondition(contains('Notes', 'shark'), { bindings });    //  projection-only
validateCondition(eq('Name', binding('Notes')), { bindings });  //  projection-only, via an operand
validateSorts([asc('Notes')], { bindings });                    //  projection-only
validateProjection('Tenant', { bindings });                     //  not-projectable
```

The third line shows that an operand is checked too, so a projection-only key cannot be used in a condition through
the operand position.

| Name | Description |
|---|---|
| `BindingUse` | Bitmask with members `None`, `Test`, `Sort`, `Projection`, `All`. Omitting `use` means all three, matching the server's default. |
| `allows(use, wanted)` | Tests a use, treating an unspecified use as all three. |
| `useName(use)` | Names the combination, for messages. |

The diagnostic is `projection-only` rather than `unbound-field`, because the key is bound, only for a different use;
reporting it as unbound would suggest a typo. The message names the permitted use: *"'Notes' cannot be used in a
condition: it is bound for Projection"*.

### Ignoring unbound fields

Where the server is set to `IgnoreUnboundFields`, pass `ignoreUnboundFields` to match it. Every `unbound-field` and
`unbound-operand` is then reported as a **warning** instead of an error, so the result is `ok` and a form can report
which part of a saved filter no longer applies instead of refusing to submit:

```ts
const result = validateQuery("Name = 'Alice' AND Gizmo = 3", { bindings, ignoreUnboundFields: true });

result.ok;                        // true
result.diagnostics[0].severity;   // 'warning'
result.diagnostics[0].message;    // "Unbound field: 'Gizmo', so it will be dropped from the query"
```

The setting covers conditions, operands, sorts, projections, quantified collections and the fields inside them.
Nothing else is softened: a narrowed binding, a type mismatch and malformed text remain errors, as on the server.

`droppedFields` extracts the field names from the diagnostics. It gives the same answer as the server's
`DroppedFields`, but before the request is sent rather than after the query is built:

```ts
for (const field of droppedFields(result.diagnostics)) {
  warn(`'${field}' no longer applies and has been removed from your saved view`);
}
```

Each name appears once, with any index stripped, in the order encountered. The list is empty unless
`ignoreUnboundFields` was set, since only then are these diagnostics warnings.

> [!WARNING]
> Mirror the server's setting exactly. If it is off here while the server is lenient, a saved filter is reported as
> broken when it would have run. If it is on here while the server is strict, the filter is reported as valid until
> the server refuses it.

## Property operands

An operand may name a bound property instead of carrying a value; brackets mark it:

```ts
import { binding, isIn, lt, toQuery } from 'weequery';

toQuery(lt('HireDate', binding('FireDate')));   // "([HireDate] < [FireDate])"
toQuery(isIn('Pay', [8000, binding('Ceiling')])); // "([Pay] IsIn (8000, [Ceiling]))"
```

Both sides must be bound, so property operands expose nothing new. On the wire a value travels as itself and only a
key carries a `Source`, so the two are distinguished by **shape** rather than by parsing text. A key cannot arrive
as bare text and be mistaken for a value:

```jsonc
{ "Operator": 10, "Field": "Pay", "Conditions": [],
  "Values": [ "8000", { "Source": 1, "Value": "Ceiling" } ] }
```

## Wire format

`pack` writes PascalCase, the System.Text.Json default. For an API configured with `JsonNamingPolicy.CamelCase`,
request camelCase:

```ts
pack(condition, 'camel');
```

`unpack` reads either casing, case-insensitively, and validates what it reads: the operator must be known, the
operand count must match, and the tree must be within the nesting limit. `validatePacked` returns the problems as
diagnostics instead of throwing.

> [!IMPORTANT]
> The operator numbers are the wire format. They are the declaration order of the C# enum and must not be reordered
> on either side. `test/packed.test.ts` pins them on both sides so that neither can drift unnoticed.

## Null semantics

The semantics are the server's; see the [C# README's section on this](../README.md#null-semantics). In brief: a
null satisfies nothing except `IsNull`, so the negative operators do not match it. `not()` differs from the negative
operator because it negates the null guard as well.

```ts
ne('Alias', 'Ghost')          // every minion who has an alias, and it isn't Ghost
not(eq('Alias', 'Ghost'))     // the same, plus every minion with no alias at all
```

Neither form is normalised into the other.

> [!WARNING]
> Confusing the two produces reports that are silently wrong.

## Building and testing

```bash
npm install
npm test
```

The build uses only TypeScript and `node --test`. `npm test` builds first; `npm run typecheck` runs the type check
without emitting output.

## License

MIT, the same as the rest of the repository.
