import assert from 'node:assert/strict';
import { describe as suite, test } from 'node:test';

import {
  at,
  BindingSet,
  eq,
  gt,
  isNull,
  isIn,
  pack,
  parseParsedQuery,
  parseQuery,
  parseSorts,
  sortsToQuery,
  splitIndex,
  toQuery,
  unpack,
  validateQuery,
  WeequeryError,
  type PackedCondition,
} from '../src/index.js';
import { expectError } from './helpers.js';

/**
 * Testing one element of a bound collection, which a condition names with brackets after the field:
 * `Tallies[apples] > 5`.
 *
 * The semantics belong to the server and are not reproduced here: a missing element behaves as a null. What this
 * side has to get right is the shape it writes and the shape it sends, so the server reads back what was meant.
 */
suite('an index on a condition', () => {
  test('at() puts an index on any comparison', () => {
    assert.equal(toQuery(at(gt('Tallies', 5), 'apples')), "([Tallies][apples] > 5)");
    assert.equal(toQuery(at(isNull('Items'), '0')), '([Items][0] IsNull)');
    assert.equal(toQuery(at(isIn('Items', ['a', 'b']), '0')), "([Items][0] IsIn ('a', 'b'))");
  });

  test('a condition with no index is the object it always was', () => {
    assert.equal(Object.hasOwn(gt('Pay', 5), 'index'), false);
    assert.equal(at(gt('Pay', 5), '0').index, '0');
  });

  test('an empty index is refused', () => {
    assert.throws(() => at(gt('Tallies', 5), ''), WeequeryError);
  });

  test('an index is quoted when it needs to be', () => {
    assert.equal(toQuery(at(eq('Tallies', 1), 'two words')), "([Tallies]['two words'] = 1)");
  });
});

suite('reading an index back', () => {
  test('the brackets after a field are an index', () => {
    const condition = parseQuery('Tallies[apples] > 5');

    assert.ok(condition && condition.kind === 'comparison');
    assert.equal(condition.field, 'Tallies');
    assert.equal(condition.index, 'apples');
  });

  test('a bracketed field takes an index too', () => {
    const condition = parseQuery('[Tallies][apples] > 5');

    assert.ok(condition && condition.kind === 'comparison');
    assert.equal(condition.index, 'apples');
  });

  test('an index survives being written and read', () => {
    for (const query of [
      'Tallies[apples] > 5',
      'Items[0] = "a"',
      'Scores[2] IsNull',
      'Tallies[apples] IsBetween (1, 9)',
      'NOT (Tallies[apples] > 5)',
    ]) {
      const once = toQuery(parseQuery(query)!);

      assert.equal(once, toQuery(parseQuery(once)!), `${query} was not stable`);
    }
  });

  test('an index is still read under the strict grammar', () => {
    assert.ok(parseQuery('Tallies[apples] > 5', 'native'));
  });

  test('a field with no index is unaffected', () => {
    const condition = parseQuery('Pay > 5');

    assert.ok(condition && condition.kind === 'comparison');
    assert.equal(condition.index, undefined);
  });

  test('a malformed index is refused', () => {
    assert.throws(() => parseQuery('Tallies[] > 5'), WeequeryError);
    assert.throws(() => parseQuery('Tallies[apples > 5'), WeequeryError);
  });
});

suite('an index in an operand', () => {
  test('an operand can name an indexed element', () => {
    const condition = parseQuery('Pay > [Tallies][apples]');

    assert.ok(condition && condition.kind === 'comparison');
    assert.equal(condition.values[0]!.value, 'Tallies[apples]');
  });

  test('it is written as two bracket pairs, which is the shape that reads back', () => {
    const written = toQuery(parseQuery('Pay > [Tallies][apples]')!);

    // "[Tallies[apples]]" would not read back, so it is not what is written
    assert.equal(written, '([Pay] > [Tallies][apples])');
    assert.equal(written, toQuery(parseQuery(written)!));
  });
});

suite('an index in a sort', () => {
  test('a sort field can carry one', () => {
    const sorts = parseSorts('Tallies[apples] DESC');

    assert.equal(sorts.length, 1);
    assert.equal(sorts[0]!.field, 'Tallies[apples]');
  });

  test('it round trips', () => {
    const once = sortsToQuery(parseSorts('Tallies[apples] DESC'));

    assert.equal(once, '[Tallies][apples] DESC');
    assert.equal(once, sortsToQuery(parseSorts(once)));
  });

  test('it reads in a combined string', () => {
    const parsed = parseParsedQuery('Pay > 1 OrderBy Tallies[apples] DESC');

    assert.ok(parsed.condition);
    assert.equal(parsed.sorts[0]!.field, 'Tallies[apples]');
  });
});

suite('an index on the wire', () => {
  test('it travels as a member of its own', () => {
    const packed = pack(at(gt('Tallies', 5), 'apples')) as PackedCondition;

    assert.equal(packed.Index, 'apples');
    assert.equal(JSON.stringify(packed), '{"Operator":6,"Field":"Tallies","Values":["5"],"Conditions":[],"Index":"apples"}');
  });

  test('a condition with no index says nothing about one', () => {
    const packed = JSON.stringify(pack(gt('Pay', 5)));

    assert.equal(packed.includes('Index'), false);
  });

  test('camelCase carries it too', () => {
    const packed = pack(at(gt('Tallies', 5), 'apples'), 'camel') as { index?: string };

    assert.equal(packed.index, 'apples');
  });

  test('it survives the round trip, under either casing', () => {
    for (const casing of ['pascal', 'camel'] as const) {
      const json = JSON.stringify(pack(at(gt('Tallies', 5), 'apples'), casing));
      const back = unpack(JSON.parse(json));

      assert.ok(back.kind === 'comparison');
      assert.equal(back.index, 'apples');
      assert.equal(toQuery(back), "([Tallies][apples] > '5')");
    }
  });

  test('an index that is not text is refused', () => {
    assert.throws(() => unpack({ Operator: 6, Field: 'Tallies', Index: 7, Values: ['5'] }), WeequeryError);
  });
});

suite('splitting a field that carries an index', () => {
  test('it comes apart into the key and the index', () => {
    assert.deepEqual(splitIndex('Tallies[apples]'), { key: 'Tallies', index: 'apples' });
    assert.deepEqual(splitIndex('Pay'), { key: 'Pay', index: undefined });
  });

  test('a malformed one is refused', () => {
    assert.throws(() => splitIndex('Tallies[apples'), WeequeryError);
    assert.throws(() => splitIndex('Tallies[]'), WeequeryError);
  });
});

suite('validating an indexed condition', () => {
  const bindings = new BindingSet([
    { key: 'Tallies', type: 'other' },
    { key: 'Pay', type: 'number' },
  ]);

  test('the key is what has to be bound, not the whole field', () => {
    const result = validateQuery('Tallies[apples] > 5', { bindings });

    assert.equal(result.ok, true, JSON.stringify(result.diagnostics));
  });

  test('an unbound key is still caught', () => {
    const result = validateQuery('Bonus[apples] > 5', { bindings });

    assert.equal(result.ok, false);
    assert.deepEqual(result.diagnostics.map((entry) => entry.code), ['unbound-field']);
  });

  test('an indexed operand checks the key it names', () => {
    assert.equal(validateQuery('Pay > [Tallies][apples]', { bindings }).ok, true);
    assert.equal(validateQuery('Pay > [Bonus][apples]', { bindings }).ok, false);
  });

  /**
   * The type recorded is the collection's, and the question is about one element of it, so the type rules are
   * skipped rather than answered wrongly
   */
  test('the type rules do not fire on an indexed field', () => {
    const result = validateQuery("Tallies[apples] StartsWith 'x'", { bindings });

    assert.equal(result.ok, true, JSON.stringify(result.diagnostics));

    // and they still fire where there is no index
    assert.equal(validateQuery("Pay StartsWith 'x'", { bindings }).ok, false);
  });

  test('an unused error path still reports', () => {
    const error = expectError(() => at(gt('Tallies', 5), ''));

    assert.match(error.message, /index/);
  });
});
