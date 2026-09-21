import { Condition, ConditionValue, comparison, isTooDeep } from './condition.js';
import { WeequeryError } from './errors.js';
import {
  ComparisonOperator,
  MAX_NESTING_DEPTH,
  Operator,
  OPERATOR_NAMES,
  ValueSource,
  isContainerOperator,
  isQuantifierOperator,
} from './operators.js';

/**
 * A condition in its serializable shape: one object carrying whatever any condition needs, matching the C# side's
 * `PackedCondition` field for field so a tree written here reads there.
 *
 * An operand that is a value travels as the value alone; only one naming a bound property carries a source with
 * it. The two are told apart by the shape they arrive in, a string against an object, which no value can be
 * mistaken for whatever it spells.
 *
 * ```jsonc
 * // Name IsIn ('Alice Fox', [Alias])
 * { "Operator": 10, "Field": "Name", "Conditions": [],
 *   "Values": [ "Alice Fox", { "Source": 1, "Value": "Alias" } ] }
 * ```
 */
export interface PackedCondition {
  Operator: number;
  Field: string;
  /**
   * Which element of the bound collection this tests. Left out entirely where there is none, which is nearly
   * every condition ever sent, so a payload naming no index is byte for byte the payload it always was.
   */
  Index?: string;
  Values: PackedValue[];
  Conditions: PackedCondition[];
}

/** The camelCase spelling, for an API configured with `JsonNamingPolicy.CamelCase`. */
export interface PackedConditionCamel {
  operator: number;
  field: string;
  index?: string;
  values: PackedValueCamel[];
  conditions: PackedConditionCamel[];
}

export type PackedValue = string | { Source: number; Value: string };
export type PackedValueCamel = string | { source: number; value: string };

/** Which spelling of the property names to write. Defaults to Pascal, which is System.Text.Json's own default. */
export type PackedCasing = 'pascal' | 'camel';

/**
 * Pack a condition into the shape the wire carries.
 *
 * Serialize the result with `JSON.stringify` and hand it to a `TransportCondition`'s `Condition` member.
 *
 * @param casing `'camel'` where the API is configured with a camelCase naming policy, which many are
 * @throws {WeequeryError} the tree nests deeper than the limit
 */
export function pack(condition: Condition, casing: PackedCasing = 'pascal'): PackedCondition | PackedConditionCamel {
  if (isTooDeep(condition)) {
    throw new WeequeryError(`Condition nests deeper than the limit of ${MAX_NESTING_DEPTH}`);
  }

  return casing === 'camel' ? packCamel(condition) : packPascal(condition);
}

function packPascal(condition: Condition): PackedCondition {
  switch (condition.kind) {
    case 'comparison': {
      const packed: PackedCondition = {
        Operator: condition.operator,
        Field: condition.field,
        Values: condition.values.map(packValuePascal),
        Conditions: [],
      };

      // Set only where there is one, so a condition naming no index says nothing about it and the payload is the
      // one it always was
      if (condition.index !== undefined) {
        packed.Index = condition.index;
      }

      return packed;
    }

    case 'conjunction':
      return {
        Operator: condition.operator,
        Field: '',
        Values: [],
        Conditions: condition.conditions.map(packPascal),
      };

    case 'not':
      return {
        Operator: Operator.Not,
        Field: '',
        Values: [],
        Conditions: [packPascal(condition.condition)],
      };

    // The shape the wire already had: an operator, a field and one child. Nothing about the format changed to
    // carry a quantifier.
    case 'quantified':
      return {
        Operator: condition.operator,
        Field: condition.field,
        Values: [],
        Conditions: [packPascal(condition.condition)],
      };

    default:
      throw new WeequeryError(`Condition kind '${String((condition as { kind?: unknown }).kind)}' cannot be packed`);
  }
}

function packCamel(condition: Condition): PackedConditionCamel {
  const pascal = packPascal(condition);

  const toCamel = (node: PackedCondition): PackedConditionCamel => {
    const camel: PackedConditionCamel = {
      operator: node.Operator,
      field: node.Field,
      values: node.Values.map((value) =>
        typeof value === 'string' ? value : { source: value.Source, value: value.Value },
      ),
      conditions: node.Conditions.map(toCamel),
    };

    if (node.Index !== undefined) {
      camel.index = node.Index;
    }

    return camel;
  };

  return toCamel(pascal);
}

function packValuePascal(operand: ConditionValue): PackedValue {
  // A value is the default and the common case, so it says nothing about where it came from
  if (operand.source !== ValueSource.Binding) {
    return operand.value;
  }

  return { Source: ValueSource.Binding, Value: operand.value };
}

/** Read a property under either casing, so a payload from any API reads back. */
function member(node: Record<string, unknown>, name: string): unknown {
  const lowered = name.toLowerCase();

  for (const key of Object.keys(node)) {
    if (key.toLowerCase() === lowered) {
      return node[key];
    }
  }

  return undefined;
}

/**
 * Read a packed condition back into a tree, checking as it goes: the operator has to be a known one, the operand
 * count has to match what it takes, and the tree has to be within the nesting limit.
 *
 * Property names are matched without regard to case, so a payload written under either naming policy reads.
 *
 * @throws {WeequeryError} the payload is not a condition this version understands
 */
export function unpack(packed: unknown): Condition {
  return unpackAt(packed, 0);
}

function unpackAt(packed: unknown, depth: number): Condition {
  if (depth > MAX_NESTING_DEPTH) {
    throw new WeequeryError(`Condition nests deeper than the limit of ${MAX_NESTING_DEPTH}`);
  }

  if (typeof packed !== 'object' || packed === null || Array.isArray(packed)) {
    throw new WeequeryError('A packed condition must be an object');
  }

  const node = packed as Record<string, unknown>;

  const rawOperator = member(node, 'Operator');
  if (typeof rawOperator !== 'number' || !Number.isInteger(rawOperator) || Operator[rawOperator] === undefined) {
    throw new WeequeryError(`'${String(rawOperator)}' is not a known operator`);
  }

  const operator = rawOperator as Operator;

  if (isContainerOperator(operator)) {
    const children = member(node, 'Conditions');
    const list = Array.isArray(children) ? children.filter((child) => child !== null && child !== undefined) : [];

    if (operator === Operator.Not) {
      if (list.length === 0) {
        throw new WeequeryError('Not has no condition to negate');
      }

      return { kind: 'not', condition: unpackAt(list[0], depth + 1) };
    }

    return {
      kind: 'conjunction',
      operator,
      conditions: list.map((child) => unpackAt(child, depth + 1)),
    };
  }

  if (isQuantifierOperator(operator)) {
    const collection = member(node, 'Field');
    if (typeof collection !== 'string' || collection.length === 0) {
      throw new WeequeryError(`A quantifier needs the key of a bound collection, and '${String(collection)}' is not one`);
    }

    const children = member(node, 'Conditions');
    const list = Array.isArray(children) ? children.filter((child) => child !== null && child !== undefined) : [];

    if (list.length === 0) {
      throw new WeequeryError(`${OPERATOR_NAMES[operator]} on '${collection}' has no condition to quantify`);
    }

    return { kind: 'quantified', operator, field: collection, condition: unpackAt(list[0], depth + 1) };
  }

  const field = member(node, 'Field');
  if (typeof field !== 'string' || field.length === 0) {
    throw new WeequeryError(`A comparison needs a field, and '${String(field)}' is not one`);
  }

  const values = member(node, 'Values');
  const operands = Array.isArray(values) ? values.map(unpackValue) : [];

  const index = member(node, 'Index');

  if ((index !== undefined) && (index !== null) && (typeof index !== 'string')) {
    throw new WeequeryError(`An index has to be text, and '${String(index)}' is not`);
  }

  // Rebuilt through the builder, so the operand count is checked exactly as it is for a hand built condition
  const built = comparison(
    operator as ComparisonOperator,
    field,
    operands,
    (index === null) ? undefined : (index as string | undefined),
  );

  // Everything that arrived as a packed payload is text, so it writes back out quoted
  return { ...built, holdsText: true };
}

function unpackValue(value: unknown): ConditionValue {
  if (typeof value === 'string') {
    return { source: ValueSource.Raw, value };
  }

  if (typeof value === 'number' || typeof value === 'boolean') {
    // Tolerated on the way in: a hand written payload may not have stringified its values
    return { source: ValueSource.Raw, value: String(value) };
  }

  if (typeof value !== 'object' || value === null) {
    throw new WeequeryError(`'${String(value)}' is not a valid operand`);
  }

  const node = value as Record<string, unknown>;

  const inner = member(node, 'Value');
  if (inner === undefined) {
    throw new WeequeryError('An operand written as an object needs a Value');
  }

  const source = member(node, 'Source');

  return {
    source: source === ValueSource.Binding ? ValueSource.Binding : ValueSource.Raw,
    value: typeof inner === 'string' ? inner : String(inner),
  };
}

/**
 * The two forms a condition travels in, matching the C# side's `TransportCondition`. A request DTO can carry
 * whichever the client prefers; where both are present the packed one wins.
 */
export interface TransportCondition {
  Condition?: PackedCondition | PackedConditionCamel | null;
  Query?: string | null;
  /**
   * Which fields to read back, as a comma separated list, where the caller asked for some of them.
   *
   * Left off where nothing was asked, which is nearly every payload ever sent, so one written before this
   * existed is the payload it always was. See {@link parseProjection}.
   */
  Projection?: string | null;
}
