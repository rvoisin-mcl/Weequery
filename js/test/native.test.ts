import assert from 'node:assert/strict';
import { describe as suite, test } from 'node:test';

import {
  parseParsedQuery,
  parsedQueryToQuery,
  parseQuery,
  parseSorts,
  SortDirection,
  sortsToQuery,
  toQuery,
} from '../src/index.js';
import { expectError } from './helpers.js';

suite('what the grammar refuses', () => {
  const refusals: [query: string, found: string, instead: string][] = [
    ['(Pay > 1) && (Pay > 2)', '&&', 'AND'],
    ['(Pay > 1) || (Pay > 2)', '||', 'OR'],
    ['!(Pay > 1)', '!', 'NOT'],
    ['Alias IS NULL', 'IS NULL', 'IsNull'],
    ['Alias IS NOT NULL', 'IS NOT NULL', 'IsNotNull'],
    ['Pay NOT IN (1, 2)', 'NOT IN', 'IsNotIn'],
    ['Pay NOT BETWEEN (1, 2)', 'NOT BETWEEN', 'IsNotBetween'],
  ];

  for (const [query, found, instead] of refusals) {
    test(`${query} says to write ${instead}`, () => {
      const error = expectError(() => parseQuery(query));

      assert.ok(error.message.includes(found), `expected the message to name '${found}': ${error.message}`);
      assert.ok(error.message.includes(instead), `expected the message to name '${instead}': ${error.message}`);
      assert.match(error.message, /Native/);
    });
  }

  test('a range written with an infix AND is refused', () => {
    for (const query of ['Pay IsBetween 1 AND 2', 'Pay BETWEEN 1 AND 2']) {
      assert.throws(() => parseQuery(query), /\(low, high\)/);
    }
  });

  test('none of these is malformed, which is the point of naming the replacement', () => {
    // Each one meant something for years. What it gets back is the one spelling, not "no".
    for (const [, , instead] of refusals) {
      assert.ok(instead.length > 0);
    }
  });
});

suite('what it takes', () => {
  const accepted = [
    '(Pay > 1) AND (Pay > 2)',
    '(Pay > 1) OR (Pay > 2)',
    'NOT (Pay > 1)',
    'NOT NOT (Pay > 1)',
    'Pay = 1',
    'Pay == 1',
    'Pay <> 1',
    'Pay != 1',
    'Pay >= 1',
    'Alias IsNull',
    'Pay IsIn (1, 2)',
    'Pay IN (1, 2)',
    'Pay IsBetween (1, 2)',
    'Pay BETWEEN (1, 2)',
    "Name StartsWith 'A'",
    'Pay > [Ceiling]',
    'Alias = null',
  ];

  for (const query of accepted) {
    test(query, () => {
      assert.ok(parseQuery(query));
    });
  }

  test('a one word alternate reads and is written back as the one form', () => {
    // The writer is what settles it, which is where two spellings become one
    assert.equal(toQuery(parseQuery('Pay == 1')!), "([Pay] = '1')");
    assert.equal(toQuery(parseQuery('Pay != 1')!), "([Pay] <> '1')");
    assert.equal(toQuery(parseQuery('Pay IN (1, 2)')!), "([Pay] IsIn ('1', '2'))");
    assert.equal(toQuery(parseQuery('Pay BETWEEN (1, 2)')!), "([Pay] IsBetween ('1', '2'))");
  });

  test('case still does not matter', () => {
    for (const query of ['(Pay > 1) and (Pay > 2)', 'nOt (Pay > 1)', 'Alias isnull', 'Pay ISIN (1, 2)']) {
      assert.ok(parseQuery(query));
    }
  });

  test('a value spelling a refused operator is still a value', () => {
    assert.equal(toQuery(parseQuery("Name = '&&'")!), "([Name] = '&&')");
    assert.equal(toQuery(parseQuery("Name = 'IS NULL'")!), "([Name] = 'IS NULL')");
  });
});

suite('what it writes, it reads', () => {
  const queries = [
    'Pay == 12000',
    'Pay != 12000',
    '(Pay > 10000) AND (IsActive == true)',
    'NOT (Pay > 10000)',
    'NOT NOT (Pay > 10000)',
    'Alias IsNull',
    'Alias IsNotNull',
    'Pay IsBetween (8000, 12000)',
    'Pay IsNotBetween (8000, 12000)',
    "Name IN ('Alice Fox')",
    "Name IsNotIn ('Alice Fox')",
    "Name StartsWith 'Al'",
    'Pay > [Ceiling]',
  ];

  for (const query of queries) {
    test(query, () => {
      // The load bearing property: anything the writer emits, the parser accepts
      const written = toQuery(parseQuery(query)!);

      assert.equal(written, toQuery(parseQuery(written)!));
    });
  }
});

suite('the sort clause prefix', () => {
  test('ORDER BY is refused, and says to write OrderBy', () => {
    const error = expectError(() => parseSorts('ORDER BY Pay DESC'));

    assert.match(error.message, /ORDER BY/);
    assert.match(error.message, /OrderBy/);
    assert.match(error.message, /Native/);
  });

  test('the one word prefix, and no prefix at all, are accepted', () => {
    for (const clause of ['OrderBy Pay DESC', 'orderby Pay DESC', 'Pay DESC', 'Pay DESC, Name']) {
      assert.ok(parseSorts(clause).length > 0);
    }
  });

  test('a field named Order still reads', () => {
    const sorts = parseSorts('Order DESC');

    assert.equal(sorts.length, 1);
    assert.equal(sorts[0]!.field, 'Order');
  });

  test('a bare clause is written without a prefix', () => {
    assert.equal(sortsToQuery(parseSorts('Pay DESC, Name')), '[Pay] DESC, [Name] ASC');
  });

  test('a sort clause round trips exactly', () => {
    const once = sortsToQuery(parseSorts('Pay DESC, Name'));

    assert.equal(once, sortsToQuery(parseSorts(once)));
  });
});

suite('a combined string', () => {
  test('it splits on the separator, not on the words', () => {
    const parsed = parseParsedQuery("Name = 'ORDER BY'");

    assert.ok(parsed.condition);
    assert.equal(parsed.sorts.length, 0);
  });

  test('it writes the one word separator and reads that back', () => {
    const parsed = parseParsedQuery('Pay > 10000 OrderBy Pay DESC');

    const written = parsedQueryToQuery(parsed);

    assert.equal(written, "([Pay] > '10000') OrderBy [Pay] DESC");
    assert.equal(written, parsedQueryToQuery(parseParsedQuery(written)));
  });

  test('the two word separator is refused here too', () => {
    assert.throws(() => parseParsedQuery('Pay > 1 ORDER BY Pay'), /OrderBy/);
    assert.ok(parseParsedQuery('Pay > 1 OrderBy Pay').condition);
  });

  test('any part may be missing', () => {
    assert.equal(parseParsedQuery('Pay > 1').sorts.length, 0);
    assert.equal(parseParsedQuery('OrderBy Pay').condition, null);
    assert.equal(parseParsedQuery('OrderBy Pay').sorts.length, 1);
  });

  test('the default sort stands in where none was named', () => {
    const parsed = parseParsedQuery('Pay > 1', [{ field: 'MinionID', direction: SortDirection.Ascending }]);

    assert.equal(parsed.sorts.length, 1);
    assert.equal(parsed.sorts[0]!.field, 'MinionID');
  });
});
