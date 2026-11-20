import assert from 'node:assert/strict';
import { describe as suite, test } from 'node:test';

import {
  any,
  asc,
  BindingSet,
  eq,
  validateCondition,
  validateProjection,
  validateQuery,
  validateSorts,
  VALUE_KEY,
  type Binding,
} from '../src/index.js';
import { expectError } from './helpers.js';

const CRATES: Binding[] = [
  { key: 'Id', type: 'number' },
  { key: 'Tags', values: 'string' },
  { key: 'Scores', values: { type: 'number' } },
  { key: 'Readings', values: { type: 'number', nullable: false } },
  { key: 'Flags', values: 'boolean' },
  { key: 'Anything', values: {} },
  { key: 'Assignments', elements: ['LairID'] },
];

const bindings = new BindingSet(CRATES);

const codes = (query: string): string[] => validateQuery(query, { bindings }).diagnostics.map((entry) => entry.code);

suite('a collection of values', () => {
  test('the element is named Value inside a quantifier', () => {
    assert.equal(VALUE_KEY, 'Value');

    assert.deepEqual(codes("Tags Any (Value = 'red')"), []);
    assert.deepEqual(codes('Scores All (Value >= 50)'), []);
    assert.deepEqual(codes("Tags None (Value StartsWith 'tmp')"), []);
    assert.deepEqual(codes('Scores Any (Value > 5 AND Value < 10)'), []);
  });

  test('a built condition reads the same as a parsed one', () => {
    assert.deepEqual(validateCondition(any('Tags', eq(VALUE_KEY, 'red')), { bindings }), []);
  });

  test('Value is matched without regard to case, as every key is', () => {
    assert.deepEqual(codes("Tags Any (value = 'red')"), []);
  });

  test('nothing but Value is in scope inside', () => {
    assert.deepEqual(codes("Tags Any (Name = 'red')"), ['unbound-field']);
    assert.deepEqual(codes('Tags Any (Id = 1)'), ['unbound-field']);
  });

  test('and Value means nothing outside one, or inside a collection of objects', () => {
    assert.deepEqual(codes("Value = 'red'"), ['unbound-field']);
    assert.deepEqual(codes('Assignments Any (Value = 5)'), ['unbound-field']);
  });

  test('the element is held to its type, as a property of that type would be', () => {
    assert.deepEqual(codes("Scores Any (Value StartsWith '1')"), ['type-mismatch']);
    assert.deepEqual(codes('Flags Any (Value > 1)'), ['type-mismatch']);
    assert.deepEqual(codes('Readings Any (Value IsNull)'), ['type-mismatch']);
  });

  test('an element with no type given takes every operator, as an untyped binding does', () => {
    assert.deepEqual(codes("Anything Any (Value StartsWith 'x')"), []);
  });

  test('it is still a property, so it is tested for null and indexed', () => {
    assert.deepEqual(codes('Tags IsNull'), []);
    assert.deepEqual(codes("Tags[0] = 'red'"), []);
  });

  test('but it is not compared whole, and the refusal names Value', () => {
    const [problem] = validateQuery("Tags = 'red'", { bindings }).diagnostics;

    assert.equal(problem!.code, 'collection-not-comparable');
    assert.match(problem!.message, /Value/);
  });

  test('a collection of values projects, where a collection of objects does not', () => {
    assert.deepEqual(validateProjection('Id, Tags', { bindings }), []);
    assert.deepEqual(
      validateProjection('Assignments', { bindings }).map((entry) => entry.code),
      ['not-projectable'],
    );
  });

  test('it is not sorted on whole, though one element of it is', () => {
    assert.deepEqual(
      validateSorts([asc('Tags')], { bindings }).map((entry) => entry.code),
      ['not-sortable'],
    );
    assert.deepEqual(validateSorts([asc('Tags[0]')], { bindings }), []);
  });

  test('a binding is a collection of objects or of values, not both', () => {
    const thrown = expectError(() => new BindingSet([{ key: 'Tags', values: 'string', elements: ['Name'] }]));

    assert.match(thrown.message, /both/);
  });

  test('a property is still not a collection', () => {
    assert.deepEqual(codes('Id Any (Value = 1)'), ['not-a-collection']);
  });
});
