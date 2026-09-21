import assert from 'node:assert/strict';
import { describe as suite, test } from 'node:test';

import {
  allows,
  any,
  asc,
  BindingUse,
  binding as bindingValue,
  contains,
  droppedFields,
  eq,
  gt,
  NO_PROJECTION,
  parseProjection,
  projectedKeys,
  projection,
  projectionToQuery,
  validateCondition,
  validateProjection,
  validateQuery,
  validateSorts,
  useName,
  WeequeryError,
  type TransportCondition,
} from '../src/index.js';
import { expectError } from './helpers.js';

/**
 * Projections: which fields a query reads back.
 *
 * The server decides what a row actually holds. What this side has to get right is the text it writes, the keys
 * it sends, and telling a caller that a field is not one they may read before the round trip does.
 */
suite('reading a projection', () => {
  test('a comma separated list is a list of fields', () => {
    assert.deepEqual(parseProjection('Name, Pay'), ['Name', 'Pay']);
    assert.deepEqual(parseProjection('Name,Pay'), ['Name', 'Pay']);
  });

  test('a field is written the way a condition writes one', () => {
    assert.deepEqual(parseProjection('[Name], Pay'), ['Name', 'Pay']);
    assert.deepEqual(parseProjection("'Total Pay'"), ['Total Pay']);
    assert.deepEqual(parseProjection('Tallies[apples]'), ['Tallies[apples]']);
  });

  test('nothing named is the empty projection', () => {
    assert.deepEqual(parseProjection(null), []);
    assert.deepEqual(parseProjection(''), []);
    assert.deepEqual(parseProjection('   '), []);
    assert.deepEqual(projection(null), []);
    assert.deepEqual(NO_PROJECTION, []);
  });

  test('a field named twice is kept once, in the position it first appeared', () => {
    assert.deepEqual(parseProjection('Name, Pay, name'), ['Name', 'Pay']);
    assert.deepEqual(projection(['Pay', 'PAY', 'Name']), ['Pay', 'Name']);
  });

  test('a malformed list is refused, and says where', () => {
    for (const text of ['Name,', 'Name Pay', 'Name, [Pay', 'Tallies[]']) {
      const error = expectError(() => parseProjection(text));

      assert.ok(error instanceof WeequeryError, text);
    }
  });

  test('a key that is not a string is refused', () => {
    assert.throws(() => projection(['Name', ''] as string[]), WeequeryError);
    assert.throws(() => projection([null as never]), WeequeryError);
  });
});

suite('writing a projection', () => {
  test('it writes back as it reads', () => {
    assert.equal(projectionToQuery(['Name', 'Pay']), '[Name], [Pay]');
    assert.equal(projectionToQuery(['Tallies[apples]']), '[Tallies][apples]');
    assert.equal(projectionToQuery(['Total Pay']), "'Total Pay'");
    assert.equal(projectionToQuery([]), '');
  });

  test('the round trip is stable', () => {
    for (const text of ['Name, Pay', '[Name], [Pay]', 'Tallies[apples], Name', "'Total Pay'"]) {
      const once = projectionToQuery(parseProjection(text));

      assert.equal(once, projectionToQuery(parseProjection(once)), `${text} was not stable`);
    }
  });

  test('projectedKeys strips the index, which is what has to be bound', () => {
    assert.deepEqual(projectedKeys(['Name', 'Tallies[apples]']), ['Name', 'Tallies']);
  });
});

suite('checking a projection against the allow-list', () => {
  const bindings = [
    { key: 'Name', type: 'string' as const },
    { key: 'Pay', type: 'number' as const },
    { key: 'Tallies' },
    { key: 'Assignments', elements: ['LairID'] },
  ];

  test('bound fields pass', () => {
    assert.deepEqual(validateProjection('Name, Pay', { bindings }), []);
    assert.deepEqual(validateProjection(['Name', 'Pay'], { bindings }), []);
  });

  test('an unbound field does not', () => {
    const [problem, ...rest] = validateProjection('Name, Morale', { bindings });

    assert.equal(rest.length, 0);
    assert.equal(problem!.code, 'unbound-field');
    assert.equal(problem!.field, 'Morale');
  });

  test('a collection cannot be projected, and is not reported as unbound', () => {
    const [problem] = validateProjection('Assignments', { bindings });

    assert.equal(problem!.code, 'not-projectable');
    assert.equal(problem!.field, 'Assignments');
  });

  test('an index is checked against the key it is taken on', () => {
    assert.deepEqual(validateProjection('Tallies[apples]', { bindings }), []);
    assert.equal(validateProjection('Nothing[apples]', { bindings })[0]!.code, 'unbound-field');
  });

  test('malformed text comes back as a diagnostic rather than a throw', () => {
    const [problem] = validateProjection('Name,', { bindings });

    assert.equal(problem!.code, 'syntax');
    assert.equal(typeof problem!.position, 'number');
  });

  test('without an allow-list only the text is checked', () => {
    assert.deepEqual(validateProjection('Name, Morale'), []);
    assert.equal(validateProjection('Name,')[0]!.code, 'syntax');
  });

  test('every bad field is reported, not just the first', () => {
    const problems = validateProjection('Morale, Name, Rank', { bindings });

    assert.equal(problems.length, 2);
    assert.deepEqual(problems.map((problem) => problem.field), ['Morale', 'Rank']);
  });
});

/**
 * What a binding may be used for. Mirrors the server's `BindingUse`, so a caller is told before the round trip
 * rather than after.
 */
suite('a narrowed binding', () => {
  const bindings = [
    { key: 'Name', type: 'string' as const },
    { key: 'Notes', type: 'string' as const, use: BindingUse.Projection },
    { key: 'Tenant', type: 'number' as const, use: BindingUse.Condition },
    { key: 'Morale', type: 'number' as const, use: BindingUse.Condition | BindingUse.Sort },
  ];

  test('the helpers agree with the flags', () => {
    assert.equal(allows(undefined, BindingUse.Sort), true, 'nothing said means all three');
    assert.equal(allows(BindingUse.Condition | BindingUse.Sort, BindingUse.Sort), true);
    assert.equal(allows(BindingUse.Condition, BindingUse.Projection), false);

    assert.equal(useName(undefined), 'All');
    assert.equal(useName(BindingUse.None), 'None');
    assert.equal(useName(BindingUse.Condition | BindingUse.Sort), 'Condition, Sort');
  });

  test('condition-only filters and nothing else', () => {
    assert.deepEqual(validateCondition(eq('Tenant', 5), { bindings }), []);
    assert.equal(validateProjection('Tenant', { bindings })[0]!.code, 'not-projectable');
    assert.equal(validateSorts([asc('Tenant')], { bindings })[0]!.code, 'projection-only');
  });

  test('two flags grant exactly those two', () => {
    assert.deepEqual(validateCondition(eq('Morale', 5), { bindings }), []);
    assert.deepEqual(validateSorts([asc('Morale')], { bindings }), []);
    assert.equal(validateProjection('Morale', { bindings })[0]!.code, 'not-projectable');
  });

  test('a refusal says what the key is actually for', () => {
    assert.match(validateCondition(contains('Notes', 'x'), { bindings })[0]!.message, /Projection/);
    assert.match(validateProjection('Morale', { bindings })[0]!.message, /Condition, Sort/);
  });

  test('it projects', () => {
    assert.deepEqual(validateProjection('Name, Notes', { bindings }), []);
  });

  test('it cannot be asked about in a condition', () => {
    const [problem] = validateCondition(contains('Notes', 'shark'), { bindings });

    assert.equal(problem!.code, 'projection-only');
    assert.equal(problem!.field, 'Notes');
  });

  test('it cannot be compared against as an operand', () => {
    const [problem] = validateCondition(eq('Name', bindingValue('Notes')), { bindings });

    assert.equal(problem!.code, 'projection-only');
  });

  test('it cannot be sorted on', () => {
    const [problem] = validateSorts([asc('Notes')], { bindings });

    assert.equal(problem!.code, 'projection-only');
    assert.equal(problem!.field, 'Notes');
  });

  test('an ordinary binding is unaffected', () => {
    assert.deepEqual(validateCondition(contains('Name', 'Alice'), { bindings }), []);
    assert.deepEqual(validateSorts([asc('Name')], { bindings }), []);
  });

  test('a field nobody bound is still reported as unbound', () => {
    assert.equal(validateCondition(contains('Nothing', 'x'), { bindings })[0]!.code, 'unbound-field');
  });
});

suite('a projection on the wire', () => {
  test('it rides along on the transport', () => {
    const sent: TransportCondition = { Query: 'IsActive = true', Projection: projectionToQuery(['Name', 'Pay']) };

    const read = JSON.parse(JSON.stringify(sent)) as TransportCondition;

    assert.deepEqual(parseProjection(read.Projection), ['Name', 'Pay']);
  });

  test('a payload naming none is the payload it always was', () => {
    const sent: TransportCondition = { Query: 'IsActive = true' };

    assert.equal(JSON.stringify(sent), '{"Query":"IsActive = true"}');
    assert.deepEqual(parseProjection(sent.Projection), []);
  });
});

/**
 * Mirroring the server's `.IgnoreUnboundFields()`: a field nothing bound is a warning rather than an error, so
 * the result is usable and a form can say which part of a saved filter no longer applies.
 */
suite('ignoring unbound fields', () => {
  const bindings = [
    { key: 'Name', type: 'string' as const },
    { key: 'Notes', type: 'string' as const, use: BindingUse.Projection },
    { key: 'Assignments', elements: ['LairID'] },
  ];

  const lenient = { bindings, ignoreUnboundFields: true };

  test('an unbound field is an error by default', () => {
    const [problem] = validateCondition(eq('Gizmo', 3), { bindings });

    assert.equal(problem!.severity, 'error');
    assert.equal(problem!.code, 'unbound-field');
  });

  test('and a warning when the server is going to drop it', () => {
    const [problem, ...rest] = validateCondition(eq('Gizmo', 3), lenient);

    assert.equal(rest.length, 0);
    assert.equal(problem!.severity, 'warning');
    assert.equal(problem!.code, 'unbound-field');
    assert.match(problem!.message, /dropped/);
  });

  test('so a query naming one still reads as usable', () => {
    const result = validateQuery("Name = 'Alice' AND Gizmo = 3", lenient);

    assert.equal(result.ok, true);
    assert.equal(result.diagnostics.length, 1);
    assert.equal(result.diagnostics[0]!.severity, 'warning');
  });

  test('it covers operands, sorts, projections and quantified collections', () => {
    assert.equal(validateCondition(eq('Name', bindingValue('Gizmo')), lenient)[0]!.severity, 'warning');
    assert.equal(validateSorts([asc('Gizmo')], lenient)[0]!.severity, 'warning');
    assert.equal(validateProjection('Gizmo', lenient)[0]!.severity, 'warning');
    assert.equal(validateCondition(any('Jobs', gt('LairID', 1)), lenient)[0]!.severity, 'warning');
  });

  test('and reaches inside a quantifier, where the inner list decides', () => {
    const [problem] = validateCondition(any('Assignments', gt('Loot', 1)), lenient);

    assert.equal(problem!.severity, 'warning');
    assert.equal(problem!.field, 'Loot');
  });

  /** The server is no more lenient about these, so neither is this */
  test('nothing else softens', () => {
    assert.equal(validateCondition(contains('Notes', 'x'), lenient)[0]!.severity, 'error');
    assert.equal(validateProjection('Assignments', lenient)[0]!.code, 'not-projectable');
    assert.equal(validateQuery('Name =', lenient).ok, false);
  });
});

/**
 * Reading off what a lenient server is going to drop, which is the same answer its `DroppedFields` gives from
 * the other end of the round trip.
 */
suite('what will be dropped', () => {
  const bindings = [
    { key: 'Name', type: 'string' as const },
    { key: 'Notes', type: 'string' as const, use: BindingUse.Projection },
  ];

  const lenient = { bindings, ignoreUnboundFields: true };

  test('it names the fields the warnings are about', () => {
    const result = validateQuery("Name = 'Alice' AND Gizmo = 3 AND Widget IsNull", lenient);

    assert.deepEqual(droppedFields(result.diagnostics), ['Gizmo', 'Widget']);
  });

  test('each name once, whatever its case or index', () => {
    const result = validateQuery('Gizmo = 3 OR gizmo = 4 OR Tallies[apples] > 1', lenient);

    assert.deepEqual(droppedFields(result.diagnostics), ['Gizmo', 'Tallies']);
  });

  test('an operand counts, since it is a field the query named', () => {
    assert.deepEqual(droppedFields(validateCondition(eq('Name', bindingValue('Gizmo')), lenient)), ['Gizmo']);
  });

  test('nothing is dropped where the server would refuse instead', () => {
    assert.deepEqual(droppedFields(validateQuery('Gizmo = 3', { bindings }).diagnostics), []);
  });

  test('and a refusal for some other reason is not a drop', () => {
    assert.deepEqual(droppedFields(validateCondition(contains('Notes', 'x'), lenient)), []);
    assert.deepEqual(droppedFields([]), []);
  });
});
