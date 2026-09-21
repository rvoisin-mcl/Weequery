import assert from 'node:assert/strict';
import { describe as suite, test } from 'node:test';

import {
  and,
  contains,
  eq,
  gt,
  isBetween,
  isIn,
  isNull,
  not,
  or,
  binding,
  parseQuery,
  toQuery,
  WeequeryError,
} from '../src/index.js';
import { expectError } from './helpers.js';

suite('writing a tree', () => {
  test('a comparison is parenthesised, the field bracketed', () => {
    assert.equal(toQuery(gt('Pay', 10000)), "([Pay] > 10000)");
  });

  test('text is quoted, numbers are not', () => {
    // Mirrors the C# side, where this is the condition's generic argument: a condition built from strings holds
    // text and quotes it, one built from numbers writes them bare
    assert.equal(toQuery(eq('Name', 'Alice Fox')), "([Name] = 'Alice Fox')");
    assert.equal(toQuery(eq('Pay', 12000)), '([Pay] = 12000)');
  });

  test('a conjunction joins with the one word there is', () => {
    const condition = and(gt('Pay', 10000), eq('IsActive', true));

    assert.equal(toQuery(condition), '(([Pay] > 10000) AND ([IsActive] = True))');
  });

  test('NOT keeps its space, and consecutive ones stay separate words', () => {
    assert.equal(toQuery(not(gt('Pay', 1))), 'NOT ([Pay] > 1)');
    assert.equal(toQuery(not(not(gt('Pay', 1)))), 'NOT NOT ([Pay] > 1)');
  });

  test('the list shapes write their operands in parentheses', () => {
    assert.equal(toQuery(isBetween('Pay', 8000, 12000)), '([Pay] IsBetween (8000, 12000))');
    assert.equal(toQuery(isIn('Name', ['Alice', 'Bob'])), "([Name] IsIn ('Alice', 'Bob'))");
  });

  test('an operand naming a property is bracketed rather than quoted', () => {
    assert.equal(toQuery(gt('HireDate', binding('FireDate'))), '([HireDate] > [FireDate])');
    assert.equal(toQuery(isIn('Pay', [8000, binding('Ceiling')])), '([Pay] IsIn (8000, [Ceiling]))');
  });

  test('a field that is not a plain word is quoted instead of bracketed', () => {
    assert.equal(toQuery(eq('my field', 5)), "('my field' = 5)");
  });

  test('an empty conjunction cannot be written', () => {
    assert.throws(() => toQuery(and()), WeequeryError);
    assert.throws(() => toQuery(or()), WeequeryError);
  });
});

suite('reading a string', () => {
  test('an empty query is no condition at all', () => {
    assert.equal(parseQuery(''), null);
    assert.equal(parseQuery('   '), null);
  });

  test('AND binds tighter than OR', () => {
    const condition = parseQuery('A = 1 OR B = 2 AND C = 3');

    assert.ok(condition && condition.kind === 'conjunction');
    assert.equal(condition.conditions.length, 2);
    assert.equal(toQuery(condition), "(([A] = '1') OR (([B] = '2') AND ([C] = '3')))");
  });

  test('case is all a conjunction may vary by', () => {
    const written = toQuery(parseQuery('(A = 1) AND (B = 2)')!);

    assert.equal(written, toQuery(parseQuery('(A = 1) and (B = 2)')!));
    assert.throws(() => parseQuery('(A = 1) && (B = 2)'), /AND/);
  });

  test('the one word alternates read, and the ones spelled with a space do not', () => {
    assert.equal(toQuery(parseQuery('Pay IN (1, 2)')!), "([Pay] IsIn ('1', '2'))");
    assert.equal(toQuery(parseQuery('Pay BETWEEN (1, 5)')!), "([Pay] IsBetween ('1', '5'))");

    assert.throws(() => parseQuery('Alias IS NULL'), /IsNull/);
    assert.throws(() => parseQuery('Pay NOT IN (1)'), /IsNotIn/);
    assert.throws(() => parseQuery('Pay NOT BETWEEN (1, 5)'), /IsNotBetween/);
  });

  test('= null is a spelling of IsNull', () => {
    assert.equal(toQuery(parseQuery('Alias = null')!), '([Alias] IsNull)');
    assert.equal(toQuery(parseQuery('Alias <> null')!), '([Alias] IsNotNull)');

    // and only an unquoted one, so a string field can still be compared against the text
    assert.equal(toQuery(parseQuery("Alias = 'null'")!), "([Alias] = 'null')");
  });

  test('a quoted value survives whatever it spells', () => {
    assert.equal(toQuery(parseQuery("Name = 'AND'")!), "([Name] = 'AND')");
    assert.equal(toQuery(parseQuery("Name = 'IS NULL'")!), "([Name] = 'IS NULL')");
  });

  test('a literal closes on the quote that opened it', () => {
    assert.equal(toQuery(parseQuery('Name = "it\'s"')!), "([Name] = 'it\\'s')");
    assert.equal(toQuery(parseQuery("Name = 'say \"hi\"'")!), "([Name] = 'say \"hi\"')");
  });

  test('a backslash escapes only a quote or another backslash', () => {
    const condition = parseQuery("Name IsMatch '^A\\w+'");

    assert.ok(condition && condition.kind === 'comparison');
    assert.equal(condition.values[0]!.value, '^A\\w+');
  });

  test('brackets in operand position name a property', () => {
    const condition = parseQuery('Pay > [Ceiling]');

    assert.ok(condition && condition.kind === 'comparison');
    assert.equal(condition.values[0]!.source, 1);
    assert.equal(condition.values[0]!.value, 'Ceiling');
  });

  test('a malformed query is refused, and says where', () => {
    const error = expectError(() => parseQuery('Pay >'));

    assert.match(error.message, /Expected a value/);
    assert.equal(typeof error.position, 'number');
  });

  test('leftover text is refused', () => {
    assert.throws(() => parseQuery('(A = 1) (B = 2)'), WeequeryError);
  });

  test('too many values for the operator is refused', () => {
    assert.throws(() => parseQuery('Pay IsBetween (1, 2, 3)'), WeequeryError);
  });
});

suite('the round trip', () => {
  const queries = [
    'Pay = 12000',
    'Pay <> 12000',
    'Pay > 12000',
    'Pay <= 12000',
    '(Pay > 10000) AND (IsActive = true)',
    '(Pay > 10000) OR (IsActive = true)',
    'NOT (Pay > 10000)',
    'NOT NOT (Pay > 10000)',
    '((Pay > 15000) OR (Pay < 5000)) AND (IsActive = true)',
    'Alias IsNull',
    'Alias IsNotNull',
    'Pay IsBetween (8000, 12000)',
    'Pay IsNotBetween (8000, 12000)',
    "Name IsIn ('Alice Fox', 'Bob Samuelson')",
    "Name IsNotIn ('Alice Fox')",
    "Name StartsWith 'Al'",
    "Name DoesNotContain 'li'",
    'Pay > [Ceiling]',
    "Name IsMatch '^A\\w+ Fox$'",
  ];

  for (const query of queries) {
    test(`${query} is stable once parsed`, () => {
      const once = toQuery(parseQuery(query)!);
      const twice = toQuery(parseQuery(once)!);

      assert.equal(once, twice);
    });
  }

  test('a tree survives being written and read', () => {
    const condition = and(
      not(contains('Name', 'temp')),
      or(eq('IsActive', true), isNull('Alias')),
      isIn('Pay', [8000, binding('Ceiling')]),
    );

    assert.equal(
      toQuery(condition),
      "(NOT ([Name] Contains 'temp') AND (([IsActive] = True) OR ([Alias] IsNull)) AND ([Pay] IsIn (8000, [Ceiling])))",
    );

    // Reading it back gives text conditions, so the bare values pick up quotes once and are stable after that
    const once = toQuery(parseQuery(toQuery(condition))!);

    assert.equal(once, toQuery(parseQuery(once)!));
    assert.match(once, /\[Ceiling\]/);
  });
});
