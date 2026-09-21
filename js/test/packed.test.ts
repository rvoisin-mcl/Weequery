import assert from 'node:assert/strict';
import { describe as suite, test } from 'node:test';

import {
  and,
  binding,
  eq,
  gt,
  isIn,
  isNull,
  not,
  Operator,
  pack,
  parseQuery,
  toQuery,
  unpack,
  ValueSource,
  WeequeryError,
  type PackedCondition,
} from '../src/index.js';

suite('the wire format', () => {
  test('the operator numbers are the ones the C# enum has', () => {
    // These are the wire format. Reordering either side silently changes what every saved filter means.
    assert.equal(Operator.IsNull, 0);
    assert.equal(Operator.Equals, 2);
    assert.equal(Operator.IsBetween, 8);
    assert.equal(Operator.IsIn, 10);
    assert.equal(Operator.Or, 18);
    assert.equal(Operator.And, 19);
    assert.equal(Operator.Not, 20);
    assert.equal(Operator.DoesNotMatch, 22);
    assert.equal(ValueSource.Raw, 0);
    assert.equal(ValueSource.Binding, 1);
  });

  test('a comparison packs to the documented shape', () => {
    const packed = pack(isIn('Name', ['Alice Fox', binding('Alias')])) as PackedCondition;

    assert.deepEqual(packed, {
      Operator: 10,
      Field: 'Name',
      Values: ['Alice Fox', { Source: 1, Value: 'Alias' }],
      Conditions: [],
    });
  });

  test('a value travels as itself and only a key carries a source', () => {
    const packed = pack(isIn('Name', ['Alice Fox', 'Bob Samuelson'])) as PackedCondition;

    assert.deepEqual(packed.Values, ['Alice Fox', 'Bob Samuelson']);
  });

  test('a container packs its children and nothing else', () => {
    const packed = pack(and(gt('Pay', 1), isNull('Alias'))) as PackedCondition;

    assert.equal(packed.Operator, Operator.And);
    assert.equal(packed.Field, '');
    assert.deepEqual(packed.Values, []);
    assert.equal(packed.Conditions.length, 2);
  });

  test('a negation packs as one child', () => {
    const packed = pack(not(gt('Pay', 1))) as PackedCondition;

    assert.equal(packed.Operator, Operator.Not);
    assert.equal(packed.Conditions.length, 1);
  });

  test('camelCase is available for an API that uses it', () => {
    const packed = pack(eq('Name', 'Alice'), 'camel') as { operator: number; field: string; values: unknown[] };

    assert.equal(packed.operator, Operator.Equals);
    assert.equal(packed.field, 'Name');
    assert.deepEqual(packed.values, ['Alice']);
  });

  test('values are stringified on the way in, whatever they were', () => {
    const packed = pack(and(eq('Pay', 12000), eq('IsActive', true))) as PackedCondition;

    assert.deepEqual((packed.Conditions[0] as PackedCondition).Values, ['12000']);
    assert.deepEqual((packed.Conditions[1] as PackedCondition).Values, ['True']);
  });
});

suite('reading a payload back', () => {
  test('a packed tree survives the round trip', () => {
    const condition = and(not(gt('Pay', 10000)), isIn('Name', ['Alice', binding('Alias')]), isNull('FireDate'));

    const viaWire = unpack(JSON.parse(JSON.stringify(pack(condition))));

    // Everything off the wire is text, so the values come back quoted where the tree wrote 10000 bare. That is
    // the documented difference: the trip preserves meaning, not types.
    assert.equal(
      toQuery(viaWire),
      "(NOT ([Pay] > '10000') AND ([Name] IsIn ('Alice', [Alias])) AND ([FireDate] IsNull))",
    );

    // and an operand naming a property is still naming one, rather than having become the text of its key
    assert.match(toQuery(viaWire), /\[Alias\]/);

    // stable from here on, through the wire and through the text
    assert.equal(toQuery(unpack(JSON.parse(JSON.stringify(pack(viaWire))))), toQuery(viaWire));
  });

  test('either casing reads', () => {
    const pascal = unpack({ Operator: 2, Field: 'Name', Values: ['Alice'], Conditions: [] });
    const camel = unpack({ operator: 2, field: 'Name', values: ['Alice'], conditions: [] });

    assert.equal(toQuery(pascal), toQuery(camel));
    assert.equal(toQuery(pascal), "([Name] = 'Alice')");
  });

  test('a missing Values or Conditions is treated as empty', () => {
    assert.equal(toQuery(unpack({ Operator: 0, Field: 'Alias' })), '([Alias] IsNull)');
  });

  test('an unknown operator is refused', () => {
    assert.throws(() => unpack({ Operator: 99, Field: 'Pay', Values: ['1'] }), WeequeryError);
  });

  test('a comparison with no field is refused', () => {
    assert.throws(() => unpack({ Operator: 2, Field: '', Values: ['1'] }), WeequeryError);
  });

  test('the wrong number of operands is refused', () => {
    assert.throws(() => unpack({ Operator: 8, Field: 'Pay', Values: ['1'] }), WeequeryError);
  });

  test('a negation with nothing to negate is refused', () => {
    assert.throws(() => unpack({ Operator: 20, Conditions: [] }), WeequeryError);
  });

  test('something that is not an object at all is refused', () => {
    assert.throws(() => unpack('Pay > 1'), WeequeryError);
    assert.throws(() => unpack(null), WeequeryError);
  });

  test('a tree nested past the limit is refused rather than walked', () => {
    let packed: Record<string, unknown> = { Operator: 2, Field: 'Pay', Values: ['1'], Conditions: [] };

    for (let i = 0; i < 20; i += 1) {
      packed = { Operator: 20, Field: '', Values: [], Conditions: [packed] };
    }

    assert.throws(() => unpack(packed), WeequeryError);
  });

  test('what the parser produces packs the same as the tree it stands for', () => {
    const fromText = parseQuery("(Pay > 10000) AND (Name Contains 'temp')")!;

    const packed = pack(fromText) as PackedCondition;

    assert.equal(packed.Operator, Operator.And);
    assert.equal(packed.Conditions.length, 2);
    assert.equal((packed.Conditions[0] as PackedCondition).Field, 'Pay');
    assert.deepEqual((packed.Conditions[0] as PackedCondition).Values, ['10000']);
  });
});
