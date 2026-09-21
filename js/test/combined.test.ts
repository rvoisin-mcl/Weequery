import assert from 'node:assert/strict';
import { describe as suite, test } from 'node:test';

import {
  BindingSet,
  BindingUse,
  isBindingKey,
  SortDirection,
  isEverything,
  isUnder,
  parseParsedQuery,
  parsedQueryToQuery,
  PROJECTION_WILDCARD,
  validateParsedQuery,
  validateProjection,
  wildcardPrefix,
  type Binding,
} from '../src/index.js';
import { expectError } from './helpers.js';

const MINIONS: Binding[] = [
  { key: 'Name', type: 'string' },
  { key: 'Pay', type: 'number' },
  { key: 'IsActive', type: 'boolean', nullable: false },
  { key: 'Notes', type: 'string', use: BindingUse.Projection },
  { key: 'Lair.Name', type: 'string' },
  { key: 'Lair.Capacity', type: 'number' },
  { key: 'Assignments', elements: ['LairID'] },
];

const bindings = new BindingSet(MINIONS);

const codes = (result: { diagnostics: readonly { code: string }[] }) => result.diagnostics.map((entry) => entry.code);

suite('a projection in a combined string', () => {
  test('it reads all three parts', () => {
    const parsed = parseParsedQuery('Pay > 10000 OrderBy Pay DESC Select Name, Pay');

    assert.ok(parsed.condition !== null);
    assert.deepEqual(
      parsed.sorts.map((sort) => sort.field),
      ['Pay'],
    );
    assert.deepEqual([...parsed.projection], ['Name', 'Pay']);
  });

  test('the sorts are optional, so a projection can follow the condition directly', () => {
    const parsed = parseParsedQuery('Pay > 10000 Select Name');

    assert.ok(parsed.condition !== null);
    assert.deepEqual(parsed.sorts, []);
    assert.deepEqual([...parsed.projection], ['Name']);
  });

  test('so is the condition', () => {
    const parsed = parseParsedQuery('OrderBy Pay DESC Select Name');

    assert.equal(parsed.condition, null);
    assert.equal(parsed.sorts.length, 1);
    assert.deepEqual([...parsed.projection], ['Name']);
  });

  test('and everything before it, which leaves the projection alone', () => {
    const parsed = parseParsedQuery('Select Name, Pay');

    assert.equal(parsed.condition, null);
    assert.deepEqual(parsed.sorts, []);
    assert.deepEqual([...parsed.projection], ['Name', 'Pay']);
  });

  test('a query with no Select asks for the whole row, as it always did', () => {
    for (const query of ['Pay > 10000 OrderBy Pay DESC', 'Pay > 10000', 'OrderBy Pay DESC', '']) {
      assert.deepEqual([...parseParsedQuery(query).projection], [], query);
    }
  });

  test('the default sort stands in beside a projection', () => {
    const byName = [{ field: 'Name', direction: SortDirection.Ascending }];
    const parsed = parseParsedQuery('Pay > 1 Select Name', byName);

    assert.deepEqual(
      parsed.sorts.map((sort) => sort.field),
      ['Name'],
    );
  });

  test('fields are written the way a field is written anywhere else', () => {
    const parsed = parseParsedQuery("Select [Name], 'Total Pay', Tallies[apples]");

    assert.deepEqual([...parsed.projection], ['Name', 'Total Pay', 'Tallies[apples]']);
  });

  test('the word is matched without regard to case, as every word here is', () => {
    assert.deepEqual([...parseParsedQuery('Pay > 1 select name').projection], ['name']);
  });
});

suite('where the split falls', () => {
  test('a value spelling the separator is still a value', () => {
    for (const query of ["Name = 'Select'", "Name = 'Select' OrderBy Pay", "Name IsIn ('Select', 'OrderBy')"]) {
      const parsed = parseParsedQuery(query);

      assert.ok(parsed.condition !== null, query);
      assert.deepEqual([...parsed.projection], [], query);
    }
  });

  test('a field named Select where a part could begin needs its brackets', () => {
    expectError(() => parseParsedQuery('Select > 5'));

    const parsed = parseParsedQuery('[Select] > 5 Select Name');

    assert.ok(parsed.condition !== null);
    assert.deepEqual([...parsed.projection], ['Name']);
  });

  test('the order is fixed, because where a part sits is what says which part it is', () => {
    expectError(() => parseParsedQuery('Pay > 1 Select Name OrderBy Pay'));
  });

  test('a Select naming nothing says so rather than meaning nothing', () => {
    assert.match(expectError(() => parseParsedQuery('Pay > 1 Select')).message, /Select/);
    assert.match(expectError(() => parseParsedQuery('Select')).message, /Select/);
    assert.match(expectError(() => parseParsedQuery('Pay > 1 OrderBy Pay Select')).message, /Select/);
  });

  test('and a malformed field list is refused', () => {
    expectError(() => parseParsedQuery('Pay > 1 Select Name Pay'));
    expectError(() => parseParsedQuery('Pay > 1 Select Name,'));
    expectError(() => parseParsedQuery('Pay > 1 Select Name Select Pay'));
  });
});

suite('writing a combined string back out', () => {
  test('every part it has, and nothing dangling for the ones it does not', () => {
    const cases: [string, string][] = [
      ['Pay > 10000 OrderBy Pay DESC Select Name, Pay', "([Pay] > '10000') OrderBy [Pay] DESC Select [Name], [Pay]"],
      ['Pay > 10000 Select Name', "([Pay] > '10000') Select [Name]"],
      ['OrderBy Pay Select Name', 'OrderBy [Pay] ASC Select [Name]'],
      ['Select Name', 'Select [Name]'],
      ['Pay > 10000', "([Pay] > '10000')"],
      ['', ''],
    ];

    for (const [query, expected] of cases) {
      assert.equal(parsedQueryToQuery(parseParsedQuery(query)), expected, query);
    }
  });

  test('and what is written reads back as itself', () => {
    const queries = [
      'Pay > 10000 OrderBy Pay DESC Select Name, Pay',
      'Pay > 10000 Select Name',
      "OrderBy Pay DESC Select 'Total Pay'",
      'Select Tallies[apples], Name',
      "Name = 'Select' OrderBy Pay Select Name",
    ];

    for (const query of queries) {
      const written = parsedQueryToQuery(parseParsedQuery(query));

      assert.equal(parsedQueryToQuery(parseParsedQuery(written)), written, query);
    }
  });

  test('one spelling for every separator, as for every operator', () => {
    const parsed = parseParsedQuery('Pay > 1 OrderBy Pay DESC Select Name');

    assert.equal(parsedQueryToQuery(parsed), "([Pay] > '1') OrderBy [Pay] DESC Select [Name]");
  });
});

suite('Select belongs to the language, so it is not a key', () => {
  test('a binding may not be named for it, as none may be named OrderBy', () => {
    assert.equal(isBindingKey('Select'), false);
    assert.equal(isBindingKey('select'), false);
    assert.equal(isBindingKey('OrderBy'), false);
  });

  test('but a key that merely starts like one is fine', () => {
    assert.equal(isBindingKey('Selected'), true);
    assert.equal(isBindingKey('Selection'), true);
  });
});

suite('the projection wildcards', () => {
  test('a star stands for the whole allow-list', () => {
    assert.equal(isEverything(PROJECTION_WILDCARD), true);
    assert.equal(isEverything('Lair.*'), false);
  });

  test('a prefix keeps its dot, which is what stops it sweeping in a neighbour', () => {
    assert.equal(wildcardPrefix('Lair.*'), 'Lair.');
    assert.equal(wildcardPrefix('Name'), null);
    assert.equal(wildcardPrefix('*'), null);

    assert.equal(isUnder('Lair.Name', 'Lair.'), true);
    assert.equal(isUnder('Lairyard.Capacity', 'Lair.'), false);
    assert.equal(isUnder('lair.name', 'Lair.'), true);
  });

  test('neither is reported as an unbound field, which is what it used to be', () => {
    assert.deepEqual(validateProjection('*', { bindings }), []);
    assert.deepEqual(validateProjection('Lair.*', { bindings }), []);
    assert.deepEqual(validateProjection('Name, Lair.*', { bindings }), []);
  });

  test('a prefix standing for nothing is still the mistake it is on the server', () => {
    const diagnostics = validateProjection('Gizmo.*', { bindings });

    assert.equal(diagnostics.length, 1);
    assert.equal(diagnostics[0]!.code, 'unbound-field');
    assert.match(diagnostics[0]!.message, /matches nothing/);
  });

  test('and a prefix standing only for what may not be projected stands for nothing', () => {
    const projectionOnly = new BindingSet([
      { key: 'Lair.Name', type: 'string', use: BindingUse.Test },
    ]);

    assert.equal(validateProjection('Lair.*', { bindings: projectionOnly }).length, 1);
  });

  test('the wildcard survives being written back out, since the server expands it', () => {
    assert.deepEqual([...parseParsedQuery('Select *').projection], ['*']);

    // Written bracketed, as any field is, and read back as the wildcard it went in as
    const written = parsedQueryToQuery(parseParsedQuery('Select *, Lair.*'));

    assert.deepEqual([...parseParsedQuery(written).projection], ['*', 'Lair.*']);
  });
});

suite('validating a combined string', () => {
  test('it checks the projection along with the other two', () => {
    const result = validateParsedQuery('Gizmo = 3 OrderBy Doohickey Select Widget', { bindings });

    assert.equal(result.ok, false);
    assert.deepEqual(codes(result), ['unbound-field', 'unbound-field', 'unbound-field']);
  });

  test('and hands the projection back with the rest of it', () => {
    const result = validateParsedQuery('Pay > 1 OrderBy Pay Select Name', { bindings });

    assert.equal(result.ok, true);
    assert.deepEqual([...(result.projection ?? [])], ['Name']);
  });

  test('a key bound for projection only may be projected and not filtered', () => {
    assert.equal(validateParsedQuery('Select Notes', { bindings }).ok, true);
    assert.equal(validateParsedQuery("Notes = 'x'", { bindings }).ok, false);
  });

  test('a collection cannot be projected, having no single value to read', () => {
    const result = validateParsedQuery('Select Assignments', { bindings });

    assert.deepEqual(codes(result), ['not-projectable']);
  });

  test('a fault in the text is one diagnostic, the string being one thing to read', () => {
    const result = validateParsedQuery('Pay Bogus 10000 OrderBy Pay', { bindings });

    assert.equal(result.ok, false);
    assert.deepEqual(codes(result), ['syntax']);
  });
});
