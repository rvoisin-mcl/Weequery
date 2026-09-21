import assert from 'node:assert/strict';
import { describe as suite, test } from 'node:test';

import {
  BindingSet,
  and,
  binding,
  contains,
  eq,
  gt,
  isBindingKey,
  isNull,
  isQualifiedSqlName,
  isSqlName,
  SortDirection,
  toQuery,
  validateCondition,
  validateParsedQuery,
  validateQuery,
  validateSorts,
  WeequeryError,
  type Binding,
} from '../src/index.js';

const MINIONS: Binding[] = [
  { key: 'Name', type: 'string' },
  { key: 'Salary', type: 'number' },
  { key: 'IsActive', type: 'boolean', nullable: false },
  { key: 'Alias', type: 'string', nullable: true },
  { key: 'Threshold', type: 'number', constant: true },
  { key: 'LairAssignments', type: 'other', sortable: false },
];

const bindings = new BindingSet(MINIONS);

const codes = (result: { diagnostics: readonly { code: string }[] }) => result.diagnostics.map((entry) => entry.code);

suite('the binding set', () => {
  test('it matches keys without regard to case, as the server does', () => {
    assert.ok(bindings.has('name'));
    assert.ok(bindings.has('NAME'));
    assert.equal(bindings.get('salary')?.type, 'number');
  });

  test('two keys differing only in case are the same key', () => {
    assert.throws(() => new BindingSet(['Pay', 'pay']), WeequeryError);
  });

  test('a key the language claims is refused', () => {
    assert.throws(() => new BindingSet(['Contains']), WeequeryError);
    assert.throws(() => new BindingSet(['AND']), WeequeryError);
    assert.throws(() => new BindingSet(['OrderBy']), WeequeryError);
  });

  test('a key that is not a valid SQL name is refused', () => {
    assert.throws(() => new BindingSet(['my field']), WeequeryError);
    assert.throws(() => new BindingSet(['2Fast']), WeequeryError);
    assert.throws(() => new BindingSet(['minion-name']), WeequeryError);
  });

  test('a period is legal between two names, so a nested path is a key', () => {
    const nested = new BindingSet(['Lair.Name', 'Lair.Capacity', 'A.B.C']);

    assert.ok(nested.has('lair.name'));
    assert.deepEqual(nested.keys, ['Lair.Name', 'Lair.Capacity', 'A.B.C']);
  });

  test('a period on its own, doubled, or on an end is not', () => {
    for (const key of ['.', '..', '.Name', 'Name.', 'Lair..Name', 'Lair.1Name', 'Lair. Name']) {
      assert.throws(() => new BindingSet([key]), WeequeryError, `expected '${key}' to be refused`);
    }
  });

  test('the two predicates part company on exactly the period', () => {
    assert.equal(isSqlName('Lair.Name'), false);
    assert.equal(isQualifiedSqlName('Lair.Name'), true);
    assert.equal(isBindingKey('Lair.Name'), true);

    assert.equal(isSqlName('Name'), true);
    assert.equal(isBindingKey('Name'), true);
  });

  test('a segment spelling a keyword is not the keyword, since a dotted key is one word', () => {
    assert.equal(isBindingKey('Lair.And'), true);
    assert.equal(isBindingKey('Not.Contains'), true);

    // and a whole key that spells one is still refused
    assert.equal(isBindingKey('And'), false);
    assert.equal(isBindingKey('Contains'), false);
  });

  test('a dotted key is used in a query unquoted, and validates against the allow-list', () => {
    const nested = new BindingSet([{ key: 'Lair.Name', type: 'string' }]);

    const result = validateQuery("Lair.Name = 'Volcano'", { bindings: nested });

    assert.equal(result.ok, true);
    assert.equal(toQuery(result.condition!), "([Lair.Name] = 'Volcano')");
  });
});

suite('checking a tree', () => {
  test('a sound condition has nothing to say', () => {
    const result = validateCondition(and(gt('Salary', 10000), eq('IsActive', true)), { bindings });

    assert.deepEqual(result, []);
  });

  test('an unbound field is an error', () => {
    const result = validateCondition(gt('Bonus', 1), { bindings });

    assert.deepEqual(result.map((entry) => entry.code), ['unbound-field']);
    assert.match(result[0]!.message, /Bonus/);
  });

  test('an operand naming an unbound property is an error too', () => {
    const result = validateCondition(gt('Salary', binding('Bonus')), { bindings });

    assert.deepEqual(result.map((entry) => entry.code), ['unbound-operand']);
  });

  test('every problem is collected rather than only the first', () => {
    const result = validateCondition(and(gt('Bonus', 1), gt('Perks', 2)), { bindings });

    assert.equal(result.length, 2);
  });

  test('a string operator on a number is a type mismatch', () => {
    const result = validateCondition(contains('Salary', 'x'), { bindings });

    assert.deepEqual(result.map((entry) => entry.code), ['type-mismatch']);
  });

  test('a boolean has no ordering', () => {
    const result = validateCondition(gt('IsActive', true), { bindings });

    assert.deepEqual(result.map((entry) => entry.code), ['type-mismatch']);
  });

  test('a null test on something that cannot be null is worth saying', () => {
    const result = validateCondition(isNull('IsActive'), { bindings });

    assert.deepEqual(result.map((entry) => entry.code), ['type-mismatch']);
  });

  test('without a binding set only the structure is checked', () => {
    assert.deepEqual(validateCondition(gt('AnythingAtAll', 1)), []);
  });

  test('an empty conjunction is a warning, not an error', () => {
    const result = validateCondition(and(), { bindings });

    assert.deepEqual(result.map((entry) => entry.severity), ['warning']);
    assert.deepEqual(result.map((entry) => entry.code), ['empty-conjunction']);
  });
});

suite('checking a string', () => {
  test('a sound query comes back with its condition', () => {
    const result = validateQuery('(Salary > 10000) AND (IsActive = true)', { bindings });

    assert.equal(result.ok, true);
    assert.deepEqual(result.diagnostics, []);
    assert.ok(result.condition);
  });

  test('a syntax error comes back as a diagnostic, with a position', () => {
    const result = validateQuery('Salary >', { bindings });

    assert.equal(result.ok, false);
    assert.deepEqual(codes(result), ['syntax']);
    assert.equal(typeof result.diagnostics[0]!.position, 'number');
  });

  test('a spelling the grammar dropped is a syntax diagnostic naming the replacement', () => {
    const result = validateQuery('Salary > 1 && IsActive = true', { bindings });

    assert.equal(result.ok, false);
    assert.deepEqual(codes(result), ['syntax']);
    assert.match(result.diagnostics[0]!.message, /AND/);
  });

  test('an empty query is fine and has no condition', () => {
    const result = validateQuery('', { bindings });

    assert.equal(result.ok, true);
    assert.equal(result.condition, null);
  });

  test('an unbound field in text is caught the same as one in a tree', () => {
    const result = validateQuery('Bonus > 1', { bindings });

    assert.equal(result.ok, false);
    assert.deepEqual(codes(result), ['unbound-field']);
  });
});

suite('checking sorts', () => {
  test('a bound, orderable field is fine', () => {
    assert.deepEqual(validateSorts([{ field: 'Salary', direction: SortDirection.Descending }], { bindings }), []);
  });

  test('a constant cannot be sorted on', () => {
    const result = validateSorts([{ field: 'Threshold', direction: SortDirection.Ascending }], { bindings });

    assert.deepEqual(result.map((entry) => entry.code), ['sort-on-constant']);
  });

  test('something with no ordering of its own cannot be sorted on', () => {
    const result = validateSorts([{ field: 'LairAssignments', direction: SortDirection.Ascending }], { bindings });

    assert.deepEqual(result.map((entry) => entry.code), ['not-sortable']);
  });

  test('paging with no sort is a warning', () => {
    const result = validateSorts([], { bindings, paged: true });

    assert.deepEqual(result.map((entry) => entry.code), ['paging-without-sort']);
    assert.deepEqual(result.map((entry) => entry.severity), ['warning']);
  });

  test('a combined string is checked on both halves at once', () => {
    const result = validateParsedQuery('Bonus > 1 OrderBy Threshold', { bindings });

    assert.equal(result.ok, false);
    assert.deepEqual(codes(result).sort(), ['sort-on-constant', 'unbound-field']);
  });
});
