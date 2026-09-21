import assert from 'node:assert/strict';
import { describe as suite, test } from 'node:test';

import {
  all,
  and,
  any,
  eq,
  fieldsUsed,
  gt,
  none,
  not,
  Operator,
  pack,
  parseQuery,
  quantify,
  startsWith,
  toQuery,
  unpack,
  validateCondition,
  validateQuery,
  WeequeryError,
  type PackedCondition,
} from '../src/index.js';
import { expectError } from './helpers.js';

/**
 * Quantified predicates: `Any`, `All` and `None` over a bound collection.
 *
 * The semantics belong to the server and are not reproduced here. What this side has to get right is the shape
 * it writes, the shape it sends, and which allow-list a field inside the brackets is checked against.
 */
suite('building a quantifier', () => {
  test('the three builders write what they mean', () => {
    assert.equal(toQuery(any('Assignments', gt('LairID', 5))), '([Assignments] Any ([LairID] > 5))');
    assert.equal(toQuery(all('Assignments', gt('LairID', 5))), '([Assignments] All ([LairID] > 5))');
    assert.equal(toQuery(none('Assignments', gt('LairID', 5))), '([Assignments] None ([LairID] > 5))');
  });

  test('the inner condition can be any shape', () => {
    assert.equal(
      toQuery(any('Assignments', and(gt('LairID', 5), startsWith('LairName', 'V')))),
      "([Assignments] Any (([LairID] > 5) AND ([LairName] StartsWith 'V')))",
    );
  });

  test('a negation inside gets the parentheses it needs to read back', () => {
    const written = toQuery(any('Assignments', not(eq('LairID', 5))));

    assert.equal(written, '([Assignments] Any (NOT ([LairID] = 5)))');
    // Parsed back, every value is text, so it comes back quoted; the shape is what this is about
    assert.equal(toQuery(parseQuery(written)!), "([Assignments] Any (NOT ([LairID] = '5')))");
  });

  test('only the three operators quantify', () => {
    assert.throws(() => quantify(Operator.Equals as never, 'Assignments', gt('LairID', 5)), WeequeryError);
  });

  test('a quantifier needs a collection and a condition', () => {
    assert.throws(() => any('', gt('LairID', 5)), WeequeryError);
    assert.throws(() => any('Assignments', null as never), WeequeryError);
  });

  test('fieldsUsed stops at the collection, because inside is a different allow-list', () => {
    const condition = and(gt('Pay', 1), any('Assignments', gt('LairID', 5)));

    assert.deepEqual(fieldsUsed(condition), ['Pay', 'Assignments']);
  });
});

suite('reading a quantifier back', () => {
  test('the operator is read and the parenthesised condition is its own', () => {
    const condition = parseQuery('Assignments Any (LairID = 5)');

    assert.ok(condition && condition.kind === 'quantified');
    assert.equal(condition.operator, Operator.Any);
    assert.equal(condition.field, 'Assignments');
    assert.equal(condition.condition.kind, 'comparison');
  });

  test('all three spellings read, in any case', () => {
    for (const [query, operator] of [
      ['Assignments Any (LairID = 5)', Operator.Any],
      ['Assignments ALL (LairID = 5)', Operator.All],
      ['Assignments none (LairID = 5)', Operator.None],
    ] as const) {
      const condition = parseQuery(query);

      assert.ok(condition && condition.kind === 'quantified');
      assert.equal(condition.operator, operator);
    }
  });

  test('a quantifier combines with the rest of the query', () => {
    const condition = parseQuery("Name = 'Alice' AND Assignments Any (LairID = 5)");

    assert.ok(condition && condition.kind === 'conjunction');
    assert.equal(condition.conditions.length, 2);
    assert.equal(condition.conditions[1]!.kind, 'quantified');
  });

  test('the parentheses are required', () => {
    const error = expectError(() => parseQuery('Assignments Any LairID = 5'));

    assert.match(error.message, /'\('/);
    assert.match(error.message, /Assignments/);
  });

  test('an indexed field cannot be quantified', () => {
    const error = expectError(() => parseQuery('Assignments[0] Any (LairID = 5)'));

    assert.match(error.message, /Assignments\[0\]/);
  });

  test('what is written reads back the same', () => {
    for (const query of [
      'Assignments Any (LairID = 5)',
      'Assignments All (LairID > 1 AND IsPrimary = true)',
      'Assignments None (LairName StartsWith \'V\')',
      'NOT (Assignments Any (LairID = 5))',
      "Name = 'Alice' OR Assignments Any (LairID IsIn (1, 2))",
      'Assignments Any (NOT (LairID = 5))',
    ]) {
      const once = toQuery(parseQuery(query)!);

      assert.equal(once, toQuery(parseQuery(once)!), `${query} was not stable`);
    }
  });
});

suite('a quantifier on the wire', () => {
  test('it packs into the shape the format already had', () => {
    const packed = pack(any('Assignments', gt('LairID', 5))) as PackedCondition;

    assert.equal(packed.Operator, 23);
    assert.equal(packed.Field, 'Assignments');
    assert.deepEqual(packed.Values, []);
    assert.equal(packed.Conditions.length, 1);
    assert.equal(packed.Conditions[0]!.Field, 'LairID');
  });

  test('and unpacks back to the same query', () => {
    const condition = any('Assignments', and(gt('LairID', 5), startsWith('LairName', 'V')));
    const round = unpack(JSON.parse(JSON.stringify(pack(condition))));

    assert.ok(round.kind === 'quantified');
    assert.equal(round.operator, Operator.Any);
    assert.equal(round.field, 'Assignments');
  });

  test('a quantifier with no child is refused rather than read as empty', () => {
    assert.throws(() => unpack({ Operator: 23, Field: 'Assignments', Values: [], Conditions: [] }), WeequeryError);
  });

  test('a quantifier with no field is refused', () => {
    assert.throws(
      () => unpack({ Operator: 24, Field: '', Values: [], Conditions: [{ Operator: 2, Field: 'X', Values: ['1'], Conditions: [] }] }),
      WeequeryError,
    );
  });
});

/**
 * The part this side exists for. The server's inner allow-list is mirrored by a binding's `elements`, and a
 * field inside the brackets is checked against that rather than against the entity's own list.
 */
suite('checking a quantifier against the allow-list', () => {
  const bindings = [
    { key: 'Name', type: 'string' as const },
    { key: 'Assignments', elements: ['LairID', { key: 'LairName', type: 'string' as const }] },
  ];

  test('a field the inner list bound is accepted', () => {
    assert.deepEqual(validateCondition(any('Assignments', gt('LairID', 5)), { bindings }), []);
  });

  test('a field it did not bind is not', () => {
    const [problem, ...rest] = validateCondition(any('Assignments', gt('Capacity', 5)), { bindings });

    assert.equal(rest.length, 0);
    assert.equal(problem!.code, 'unbound-field');
    assert.equal(problem!.field, 'Capacity');
  });

  test('the two lists do not leak into each other', () => {
    // Name is bound on the entity and not inside, and LairID is bound inside and not on the entity
    assert.equal(validateCondition(any('Assignments', eq('Name', 'Alice')), { bindings })[0]!.code, 'unbound-field');
    assert.equal(validateCondition(gt('LairID', 5), { bindings })[0]!.code, 'unbound-field');
  });

  test('a property cannot be quantified', () => {
    const [problem] = validateCondition(any('Name', gt('LairID', 5)), { bindings });

    assert.equal(problem!.code, 'not-a-collection');
    assert.equal(problem!.field, 'Name');
  });

  test('a collection cannot be compared', () => {
    const [problem] = validateCondition(eq('Assignments', 'x'), { bindings });

    assert.equal(problem!.code, 'collection-not-comparable');
    assert.equal(problem!.field, 'Assignments');
  });

  test('a collection nobody bound is refused', () => {
    const [problem] = validateCondition(any('Jobs', gt('LairID', 5)), { bindings });

    assert.equal(problem!.code, 'unbound-field');
    assert.equal(problem!.field, 'Jobs');
  });

  test('one bad field inside does not bury the rest of the query', () => {
    const problems = validateCondition(
      and(eq('Name', 'Alice'), any('Assignments', and(gt('LairID', 5), gt('Capacity', 1)))),
      { bindings },
    );

    assert.equal(problems.length, 1);
    assert.equal(problems[0]!.field, 'Capacity');
  });

  test('a query string is checked the same way', () => {
    const result = validateQuery("Name = 'Alice' AND Assignments Any (LairName StartsWith 'V')", { bindings });

    assert.equal(result.ok, true);
    assert.deepEqual(result.diagnostics, []);
  });

  test('and a bad one says which field and where', () => {
    const result = validateQuery('Assignments Any (Capacity > 1)', { bindings });

    assert.equal(result.ok, false);
    assert.equal(result.diagnostics[0]!.code, 'unbound-field');
  });
});
