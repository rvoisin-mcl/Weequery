# Weequery for TypeScript

*[taps the glass]*

Is this on? Good.

The [C# library](../README.md) turns a filter that arrives as data into an `IQueryable`. This is the other end
of the wire: it builds the filter, and it tells you what is wrong with it **before** you spend a round trip
finding out.

No dependencies. Not one. I have standards, and they survived the port.

```bash
npm install weequery
```

## The plan

Build a condition as a tree:

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

Or read one from a string, which is what arrives from a form or a query string:

```ts
import { parseQuery, toQuery } from 'weequery';

const condition = parseQuery("(Salary > 10000) AND (IsActive = true)");

toQuery(condition!);   // writes it back out, in the one canonical spelling
```

The two are the same thing. A tree writes to a string, a string parses to a tree, and either packs to the JSON
the server reads.

## Telling them what is wrong before you send it

This is the part worth having. Give it the same binding list the server declares, and it predicts what the
server will say:

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

It collects everything rather than stopping at the first, which is what a form wants, and a syntax error comes
back carrying the index it happened at so an editor can point at the character.

What it catches:

| Code | What it means |
|---|---|
| `syntax` | the text is not a query, and `position` says where |
| `unbound-field` | a field no binding claimed |
| `unbound-operand` | `Pay > [Bonus]` where Bonus is not bound |
| `value-count` | too few or too many operands for the operator |
| `list-too-long` | an `IsIn` list past 1000 |
| `nesting` | nested past 16 levels |
| `type-mismatch` | `Contains` on a number, an ordering on a bool, a null test on something that cannot be null |
| `not-sortable` | a collection or navigation property, which has no ordering of its own |
| `sort-on-constant` | a value your code supplies, which is the same for every row |
| `empty-conjunction` | a warning: it matches everything or nothing, and has no query string form |
| `paging-without-sort` | a warning: page two may repeat a row from page one |

A key is one or more unquoted SQL names separated by periods, so a nested property can be bound under the path it
already has:

```ts
new BindingSet(['Name', 'Lair.Capacity', 'Lair.Name']);
```

Each segment is held to the whole rule, so `.Name`, `Name.`, `Lair..Name` and `Lair.1Name` are all refused.
`isSqlName` is still one name with no period in it; `isBindingKey` is the rule a key is held to. A period was
never a delimiter here, so a dotted key reads and writes unquoted like any other.

**Nothing here grants anything.** The server's binding list is the one that decides. This only predicts it, and a
`BindingSet` that has drifted from the server's just makes worse predictions.

Everything in `Binding` except the key is optional. Without a `type` the type rules are skipped and the field is
still checked for being bound at all, which is the part that matters.

## One grammar, and no way to ask for another

There is no `QueryStyle` here and nothing to pass. Everything this reads and everything it writes is the Native
spelling: one form per operator, so two conditions that mean the same thing are written the same way and can be
compared as text.

```ts
toQuery(condition);          // "(([Pay] > 10000) AND ([IsActive] = True))"
parseQuery(text);            // the same grammar, read back
```

The rule is that an operator is one word. `AND`, `OR`, `NOT`, `=`, `<>`, and every named operator as its own
name. `IN`, `BETWEEN`, `==` and `!=` are one word too, so they read; what comes back out is always the one form.
What is refused is anything with a space in it, plus the three conjunction symbols:

```ts
parseQuery('A = 1 && B = 2');
// WeequeryError: '&&' at position 6 is not valid in the Native style, write 'AND'

parseQuery('Alias IS NULL');
// WeequeryError: 'IS NULL' is not valid in the Native style, write 'IsNull'

parseSorts('ORDER BY Pay DESC');
// WeequeryError: 'ORDER BY' at position 0 is not valid in the Native style, write 'OrderBy'
```

Every refusal names the replacement, because what is being refused worked for years and the person who typed it
is not the one who changed the rules. A refusal that only said "no" would be the worse tool.

**The C# side still reads the old spellings in places**, and that is the one thing to know. Its `QueryRequest`
is Native only, like this; `ApplyCondition(text)` on a bare string is not, and defaults to reading everything it
ever read. Point this at a server that takes a request and the two agree exactly. Point it at one that takes a
bare condition string and this is the stricter of the two, which shows up as a form refusing `&&` that the
server would have taken.

## Sorting, projecting, and all three in one string

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

Every part is optional and the order is fixed, condition then sorts then projection, because where a part sits
is the only thing saying which part it is:

```
Salary > 10000 OrderBy Salary DESC Select Name   all three
Salary > 10000 Select Name                       a condition and a projection, and no sorting
OrderBy Salary DESC Select Name                  sorts and a projection, and no filtering at all
Select Name, Salary                              a projection, and nothing else
```

`ORDER BY` is two words, so it is refused and only `OrderBy` is taken. `Select` had only ever one spelling, so
there was never a second to refuse, and a `Select` with nothing after it is refused rather than read as the
projection that names no fields.

Where each split falls is decided by reading the part before it and seeing where it stops, not by hunting the
text, so `Name = 'ORDER BY'` is one comparison and no sorts, and `Name = 'Select'` is one comparison and no
projection. Neither word may be a binding key, for the same reason.

## Values, and what happens to them

Every operand travels as text and is read against the bound property's type on the server. A `number`, `boolean`,
`bigint` or `Date` handed to a builder is formatted invariantly on the way in, so a query means the same thing on
every machine.

A `Date` becomes an ISO 8601 string in UTC, which .NET reads back as a `DateTime` with millisecond precision.
Where you need more than that, or a specific `Kind` or offset, pass the text yourself.

One quirk worth knowing, and it is the C# side's quirk too: a condition built from **strings** writes its values
quoted, and one built from **numbers** writes them bare. So `toQuery(parseQuery(toQuery(x)))` can differ from
`toQuery(x)` by the quoting, and is stable from there on. Compare parsed conditions, or the rows they select,
rather than the original string.

## Reaching into a collection

Where the server has bound a list, an array or a dictionary, a condition can name one element of it. `at()` puts
the index on any comparison:

```ts
import { at, gt, isNull, toQuery } from 'weequery';

toQuery(at(gt('Tallies', 5), 'apples'));   // "([Tallies][apples] > 5)"
toQuery(at(isNull('Items'), '0'));         // "([Items][0] IsNull)"
```

One function rather than an extra argument on all sixteen builders: the index means the same thing under every
operator, and a trailing optional argument on `isBetween(field, low, high, index)` is a thing to miscount.

It reads and writes in all three positions the server accepts. In a **condition**, an **operand**, and a **sort**:

```ts
parseQuery('Tallies[apples] > 5');       // { field: 'Tallies', index: 'apples', ... }
parseQuery('Pay > [Tallies][apples]');   // the operand carries it in its own text
parseSorts('Tallies[apples] DESC');      // and so does the sort field
```

Written back out, an index is always the **second** bracket pair, `[Tallies][apples]`, never
`[Tallies[apples]]`, because only the first of those reads back. On the wire it is a member of its own, and
absent where there is none, so a payload naming no index is byte for byte the payload it always was:

```jsonc
{ "Operator": 6, "Field": "Tallies", "Values": ["5"], "Conditions": [], "Index": "apples" }
```

**A missing element is a null**, and that is the server's rule rather than this library's: an index nothing sits
at satisfies nothing except `IsNull`, the negative operators do not catch it, and `not()` brings it back. See
[the C# README](../README.md#reaching-into-a-collection), which also has the table of what each database can
translate, and the warning that a navigation collection has no defined order.

**Validation skips the type rules on an indexed field.** A `BindingSet` records the *collection's* type, and the
question is about one element of it, so `Tallies[apples] StartsWith 'x'` is left alone rather than reported as a
mismatch. The key still has to be bound.

## Asking about all of them at once

Everything above picks **one** element. `any`, `all` and `none` ask about the elements as a set, which is
usually the question you had:

```ts
import { and, any, eq, gt, none, startsWith, toQuery } from 'weequery';

toQuery(any('Assignments', and(gt('LairID', 5), startsWith('LairName', 'V'))));
// "([Assignments] Any (([LairID] > 5) AND ([LairName] StartsWith 'V')))"

parseQuery('Assignments None (LairID = 5)');
// { kind: 'quantified', operator: Operator.None, field: 'Assignments', condition: { ... } }
```

The parentheses in the text are required. Without them the end of the inner condition would be indistinguishable
from the start of whatever follows it, and the two readings ask different questions.

**The inside is its own allow-list.** Mirror the server's `BindCollection` with a binding's `elements`, and a
field inside the brackets is checked against that rather than against the entity's own list:

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

Two more diagnostics come with it. `not-a-collection` where a quantifier names a key bound as an ordinary
property, and `collection-not-comparable` where a comparison names one bound as a collection: a key answers
either the quantifiers or everything else, never both.

`fieldsUsed()` stops at the collection and does not report the names inside, for the same reason: they are not
bound on the entity, and a flat list saying otherwise would be wrong. Reach for `condition.condition` and call
it again if you want them.

On the wire it is the shape a condition already had, so nothing about the payload format changed:

```jsonc
{ "Operator": 23, "Field": "Assignments", "Values": [],
  "Conditions": [ { "Operator": 6, "Field": "LairID", "Values": ["5"], "Conditions": [] } ] }
```

**A quantifier is never unknown**, which is the server's rule and worth knowing here: an empty collection and a
missing one are the same answer, so `Any` is false of both and `All` and `None` are true of both. See
[the C# README](../README.md#asking-about-all-of-them-at-once).

## Reading back only some of it

A condition decides which rows; a projection decides which columns. It is a comma separated list of bound field
names, written the same way a condition and a sort write one:

```ts
import { parseProjection, projectionToQuery, validateProjection } from 'weequery';

parseProjection('Name, Pay');                 // ['Name', 'Pay']
projectionToQuery(['Name', 'Tallies[apples]']);   // "[Name], [Tallies][apples]"
```

Send it alongside the condition, and the server hands back one entry per key rather than whole entities:

```ts
const body: TransportCondition = {
  Query: 'IsActive = true',
  Projection: projectionToQuery(selectedColumns),
};

// [ { "Name": "Alice Fox", "Pay": 12000 }, ... ]
```

**The allow-list is the same one**, so `validateProjection` catches a bad field before the round trip:

```ts
validateProjection('Name, Morale', { bindings });
//  unbound-field: Morale

validateProjection('Assignments', { bindings });
//  not-projectable: Assignments is a collection, so it has no single value to read
```

A field named twice is kept once, matched without regard to case, so a list assembled from checkboxes needs no
deduplicating first. Malformed text comes back as a `syntax` diagnostic with a position rather than as a throw,
like everything else here, so it renders beside the input box. Nothing named is the empty projection, which the
server reads as "every bound field".

**`*` and `Prefix.*` name the allow-list rather than a field.** `*` is every field the caller may read, and
`Lair.*` is every one under that prefix, which is the difference between a checkbox list and a "take the lot"
button:

```ts
validateProjection('*', { bindings });          // no diagnostics
validateProjection('Name, Lair.*', { bindings });   // no diagnostics

validateProjection('Gizmo.*', { bindings });
//  unbound-field: 'Gizmo.*' matches nothing: no binding under 'Gizmo.' can be projected
```

The prefix keeps its dot, which is the point of it: `Lair.` cannot sweep in a key called `Lairyard.Capacity`.
Neither is expanded here, because what it expands to is the binding list and the server has the one that
decides; what this checks is that a prefix stands for something, which is the same refusal the server gives.

`Projection` is left off the payload entirely where nothing was asked, so one written before any of this existed
is the payload it always was. See [the C# README](../README.md#reading-back-only-some-of-it) for what a row
actually holds and how the keys are spelled.

### What a binding is *for*

A binding grants three separable things (filter, sort, project) and the server says which with `BindingUse`.
Mirror it here and this side refuses the same things it will:

```ts
const bindings = [
  { key: 'Name' },                                                    // all three, the default
  { key: 'Notes',  type: 'string', use: BindingUse.Projection },
  { key: 'Tenant', type: 'number', use: BindingUse.Condition },
  { key: 'Morale', type: 'number', use: BindingUse.Condition | BindingUse.Sort },
];

validateProjection('Name, Notes', { bindings });                //  fine
validateCondition(contains('Notes', 'shark'), { bindings });    //  projection-only
validateCondition(eq('Name', binding('Notes')), { bindings });  //  projection-only, the operand back door
validateSorts([asc('Notes')], { bindings });                    //  projection-only
validateProjection('Tenant', { bindings });                     //  not-projectable
```

`BindingUse` is a bitmask (`None`, `Condition`, `Sort`, `Projection`, `All`) and leaving `use` off means all
three, matching the server's default. Two helpers come with it: `allows(use, wanted)`, which treats "nothing
said" as all three, and `useName(use)` for a message that has to name the combination.

The diagnostic is `projection-only` rather than `unbound-field`, because the key is not unbound, it works
somewhere else. Telling a caller it does not exist would send them looking for a typo. The message names what it
is actually for: *"'Notes' cannot be used in a condition: it is bound for Projection"*.

### When the server forgets rather than refuses

Where the server is set to `IgnoreUnboundFields`, pass `ignoreUnboundFields` and this side agrees with it:
every `unbound-field` and `unbound-operand` comes back as a **warning** instead of an error, so the result is
`ok` and a form can say which part of a saved filter no longer applies rather than refusing to submit.

```ts
const result = validateQuery("Name = 'Alice' AND Gizmo = 3", { bindings, ignoreUnboundFields: true });

result.ok;                        // true
result.diagnostics[0].severity;   // 'warning'
result.diagnostics[0].message;    // "Unbound field: 'Gizmo', so it will be dropped from the query"
```

It covers conditions, operands, sorts, projections, quantified collections and the fields inside them. Nothing
else softens, a narrowed binding, a type mismatch and malformed text are errors either way, exactly as they are
on the server.

`droppedFields` reads the names straight off the diagnostics, which is the same answer the server's
`DroppedFields` gives from the other end of the round trip, here before anything is sent, there after the query
was built:

```ts
for (const field of droppedFields(result.diagnostics)) {
  warn(`'${field}' no longer applies and has been removed from your saved view`);
}
```

Each name once, index stripped, in the order it was met. Empty unless `ignoreUnboundFields` was set, since only
then are these warnings rather than errors.

**Mirror whatever the server is set to.** Left off here while the server is lenient, a caller is told their saved
filter is broken when it would have run; set here while the server is strict, they are told it will run when it
is about to be refused.

## Comparing against another property

An operand can name a bound property instead of carrying a value, which is what the brackets say:

```ts
import { binding, isIn, lt, toQuery } from 'weequery';

toQuery(lt('HireDate', binding('FireDate')));   // "([HireDate] < [FireDate])"
toQuery(isIn('Pay', [8000, binding('Ceiling')])); // "([Pay] IsIn (8000, [Ceiling]))"
```

Both sides must be bound, so this exposes nothing new. On the wire a value travels as itself and only a key
carries a source, so the two are told apart by shape rather than by anything read out of the text:

```jsonc
{ "Operator": 10, "Field": "Pay", "Conditions": [],
  "Values": [ "8000", { "Source": 1, "Value": "Ceiling" } ] }
```

## The wire format

`pack` writes PascalCase, which is System.Text.Json's own default. Where your API is configured with
`JsonNamingPolicy.CamelCase`, ask for it:

```ts
pack(condition, 'camel');
```

`unpack` reads either, without regard to case, and checks what it reads: the operator has to be a known one, the
operand count has to match, and the tree has to be inside the nesting limit. Use `validatePacked` where you would
rather have the problems back than have it throw.

**The operator numbers are the wire format.** They are the declaration order of the C# enum, and neither side is
free to reorder them. `test/packed.test.ts` pins them for exactly that reason.

## Nulls

Worth reading the [C# README's section on this](../README.md#how-nulls-behave), because the semantics are the
server's and this library only writes them down. The short version: a null satisfies nothing except `IsNull`, so
the negative operators do not catch it, and `not()` is a different question from the negative operator because it
negates the null guard along with everything else.

```ts
ne('Alias', 'Ghost')          // every minion who has an alias, and it isn't Ghost
not(eq('Alias', 'Ghost'))     // the same, plus every minion with no alias at all
```

Neither is normalised into the other. If you confuse them your report will be wrong.

## Building and testing

```bash
npm install
npm test
```

TypeScript and `node --test`, and nothing else. `npm test` builds first; `npm run typecheck` is the build without
the output.

## License

MIT, the same as the rest of it.
