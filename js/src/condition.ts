import { WeequeryError } from './errors.js';
import {
  ComparisonOperator,
  ConditionShape,
  MAX_NESTING_DEPTH,
  Operator,
  OPERATOR_NAMES,
  isQuantifierOperator,
  QuantifierOperator,
  shapeForOperation,
  ValueSource,
  valuesRequired,
} from './operators.js';

/**
 * One of a condition's operands: a value to compare against, or the key of another bound property to compare
 * against.
 *
 * Both are text by the time a condition travels, so which one it is has to travel with it. Nothing is guessed
 * from the text: a bare word is a value, and a value that happens to spell a binding key stays a value.
 */
export interface ConditionValue {
  readonly source: ValueSource;
  readonly value: string;
}

/** A value the caller supplied, to be compared against directly. */
export function raw(value: string): ConditionValue {
  return { source: ValueSource.Raw, value };
}

/** The key of another bound property, whose value is what the comparison is against. */
export function binding(key: string): ConditionValue {
  return { source: ValueSource.Binding, value: key };
}

/** A comparison against a bound property. */
export interface ComparisonCondition {
  readonly kind: 'comparison';
  readonly operator: ComparisonOperator;
  readonly field: string;
  readonly values: readonly ConditionValue[];
  /**
   * Whether the operands are text, which is the only thing this decides: text is quoted when written, and a
   * number or a date reads better bare.
   *
   * Mirrors the C# side, where the same distinction is the condition's generic argument. A condition built from
   * strings, or read back from a query or a packed payload, holds text; one built from numbers, booleans or
   * dates does not. It has no bearing on meaning, and the values are always sent as text either way.
   */
  readonly holdsText: boolean;
  /**
   * Which element of the bound collection this tests, or absent for the binding as it stands.
   *
   * Written as text and read against the collection's key type on the server: a position for a list or an array,
   * a key for a dictionary. In the query language it is the brackets after the field, `Tallies[apples]`.
   *
   * An element that is not there is not an error and not a default. It is the absence of a value, and behaves
   * exactly as a nullable property does, so it satisfies nothing except `IsNull` and the negative operators do
   * not catch it either.
   */
  readonly index?: string;
}

/** An AND or an OR over any number of operands. */
export interface ConjunctionCondition {
  readonly kind: 'conjunction';
  readonly operator: Operator.And | Operator.Or;
  readonly conditions: readonly Condition[];
}

/**
 * A negation.
 *
 * Not the same thing as the negative operators. It negates the whole test, the null guard included, so it brings
 * the null rows back: `NOT ([Alias] = 'Ghost')` matches a row with no alias, where `[Alias] <> 'Ghost'` does not.
 * Neither is normalised into the other.
 */
export interface NotCondition {
  readonly kind: 'not';
  readonly condition: Condition;
}

/**
 * A test over the elements of a bound collection: `Any`, `All` or `None` of them satisfying the condition it
 * holds.
 *
 * ```
 * Assignments Any (LairID = 5 AND IsPrimary = true)
 * ```
 *
 * The only kind that is both bound and a container. It names a collection, as a comparison names a property, and
 * it holds one condition, as a negation does; what it does not hold is operands, since the test is the condition
 * inside rather than a value.
 *
 * The point of one condition rather than several tests is that it is scoped to **one element**. "Any assignment
 * that is both to lair 5 and primary" is what this asks; "some assignment is to lair 5, and some assignment is
 * primary" is a weaker question, and it is what two separate quantifiers ANDed together mean.
 *
 * The fields inside resolve against the collection's own allow-list rather than the entity's, which the server
 * declares with `BindCollection` and a {@link Binding}'s `elements` mirrors here.
 *
 * **It is never unknown.** Every other bound condition can be defeated by a null and this one cannot: a
 * collection either holds an element that matches or it does not. Empty and missing are the same answer, so
 * `Any` is false of both and `All` and `None` are true of both.
 */
export interface QuantifiedCondition {
  readonly kind: 'quantified';
  readonly operator: QuantifierOperator;
  readonly field: string;
  readonly condition: Condition;
}

export type Condition = ComparisonCondition | ConjunctionCondition | NotCondition | QuantifiedCondition;

/** Anything a builder will take as a value, formatted invariantly on the way in. */
export type Value = string | number | boolean | Date | bigint;

/**
 * Format a value the way the C# side's `ValueFormat.ToInvariantString` does, so the text means the same thing
 * on both ends.
 *
 * A `Date` becomes an ISO 8601 string in UTC, which .NET reads back as a `DateTime` with millisecond precision.
 * Where you need more than that, or a specific `Kind` or offset, pass the text yourself.
 */
export function toInvariantString(value: Value): string {
  if (typeof value === 'string') {
    return value;
  }

  if (typeof value === 'boolean') {
    // .NET writes True/False, and reads either case back
    return value ? 'True' : 'False';
  }

  if (typeof value === 'bigint') {
    return value.toString();
  }

  if (value instanceof Date) {
    if (Number.isNaN(value.getTime())) {
      throw new WeequeryError('An invalid Date cannot be used as a value');
    }
    return value.toISOString();
  }

  if (typeof value === 'number') {
    if (!Number.isFinite(value)) {
      throw new WeequeryError(`${String(value)} cannot be used as a value`);
    }
    return String(value);
  }

  throw new WeequeryError(`Values of type ${typeof value} are not supported`);
}

/** An operand a builder will take: a value to format, or an already-built one naming a property. */
export type Operand = Value | ConditionValue;

function isConditionValue(operand: Operand): operand is ConditionValue {
  return typeof operand === 'object' && operand !== null && !(operand instanceof Date) && 'source' in operand;
}

function toOperand(operand: Operand): { value: ConditionValue; wasText: boolean } {
  if (isConditionValue(operand)) {
    return { value: operand, wasText: false };
  }

  return { value: raw(toInvariantString(operand)), wasText: typeof operand === 'string' };
}

/**
 * Build a comparison, checking the operand count against what the operator takes.
 *
 * The count is checked here rather than left for the server, so a condition that exists is one holding the right
 * number of operands for what it does.
 */
export function comparison(
  operator: ComparisonOperator,
  field: string,
  operands: readonly Operand[] = [],
  index?: string,
): ComparisonCondition {
  if (typeof field !== 'string' || field.length === 0) {
    throw new WeequeryError(`A comparison needs a field, and '${String(field)}' is not one`);
  }

  if (index !== undefined && (typeof index !== 'string' || index.length === 0)) {
    throw new WeequeryError(`An index has to be text, and '${String(index)}' is not`);
  }

  const shape = shapeForOperation(operator);
  if (shape === null) {
    throw new WeequeryError(`Operator ${OPERATOR_NAMES[operator] ?? String(operator)} combines conditions, so it is not a comparison`);
  }

  const mapped = operands.map(toOperand);
  const values = mapped.map((entry) => entry.value);

  validateValueCount(operator, field, values.length);

  // Text only where every operand that is a value is one, which is the C# side's single generic argument
  const rawOperands = mapped.filter((entry) => entry.value.source === ValueSource.Raw);
  const holdsText = rawOperands.length > 0 && rawOperands.every((entry) => entry.wasText);

  // Left off entirely rather than set to undefined, so a condition naming no index is the object it always was
  return (index === undefined)
    ? { kind: 'comparison', operator, field, values, holdsText }
    : { kind: 'comparison', operator, field, values, holdsText, index };
}

/**
 * The same comparison, tested at one element of a bound collection rather than against the collection itself.
 *
 * ```ts
 * at(gt('Tallies', 5), 'apples')          // Tallies[apples] > 5
 * at(isNull('Commendations'), '0')        // Commendations[0] IsNull
 * ```
 *
 * One function rather than an extra argument on all sixteen builders: the index is the same idea whichever
 * operator it is under, and a trailing optional argument on `isBetween(field, low, high, index)` is a thing to
 * miscount.
 *
 * A list and an array index by position, a dictionary by key. The index travels as text and the server reads it
 * against the collection's key type, exactly as it reads a value.
 */
export function at(condition: ComparisonCondition, index: string): ComparisonCondition {
  if (typeof index !== 'string' || index.length === 0) {
    throw new WeequeryError(`An index has to be text, and '${String(index)}' is not`);
  }

  // Copied rather than rebuilt through the builder, which would recompute holdsText from operands that are
  // already ConditionValues and decide none of them was text: at(eq('Name', 'Alice'), '0') would stop quoting
  return { ...condition, index };
}

/** Check a value count against what the operator takes. */
export function validateValueCount(operator: Operator, field: string, count: number): void {
  const required = valuesRequired(operator);

  if (count < required.minimum) {
    throw new WeequeryError(
      `Not enough values provided for Operator '${OPERATOR_NAMES[operator]}' on field '${field}', it needs at least ${required.minimum} but got ${count}`,
    );
  }

  if (count > required.maximum) {
    throw new WeequeryError(
      `Extra values provided for Operator '${OPERATOR_NAMES[operator]}' on field '${field}', it accepts at most ${required.maximum} but got ${count}`,
    );
  }
}

// ---------- the builders ----------

export const isNull = (field: string): ComparisonCondition => comparison(Operator.IsNull, field);
export const isNotNull = (field: string): ComparisonCondition => comparison(Operator.IsNotNull, field);

export const eq = (field: string, value: Operand): ComparisonCondition => comparison(Operator.Equals, field, [value]);
export const ne = (field: string, value: Operand): ComparisonCondition => comparison(Operator.NotEqual, field, [value]);
export const lt = (field: string, value: Operand): ComparisonCondition => comparison(Operator.LessThan, field, [value]);
export const lte = (field: string, value: Operand): ComparisonCondition => comparison(Operator.LessThanOrEqual, field, [value]);
export const gt = (field: string, value: Operand): ComparisonCondition => comparison(Operator.GreaterThan, field, [value]);
export const gte = (field: string, value: Operand): ComparisonCondition => comparison(Operator.GreaterThanOrEqual, field, [value]);

export const startsWith = (field: string, value: Operand): ComparisonCondition => comparison(Operator.StartsWith, field, [value]);
export const doesNotStartWith = (field: string, value: Operand): ComparisonCondition => comparison(Operator.DoesNotStartWith, field, [value]);
export const endsWith = (field: string, value: Operand): ComparisonCondition => comparison(Operator.EndsWith, field, [value]);
export const doesNotEndWith = (field: string, value: Operand): ComparisonCondition => comparison(Operator.DoesNotEndWith, field, [value]);
export const contains = (field: string, value: Operand): ComparisonCondition => comparison(Operator.Contains, field, [value]);
export const doesNotContain = (field: string, value: Operand): ComparisonCondition => comparison(Operator.DoesNotContain, field, [value]);

export const isMatch = (field: string, pattern: Operand): ComparisonCondition => comparison(Operator.IsMatch, field, [pattern]);
export const doesNotMatch = (field: string, pattern: Operand): ComparisonCondition => comparison(Operator.DoesNotMatch, field, [pattern]);

export const isBetween = (field: string, low: Operand, high: Operand): ComparisonCondition =>
  comparison(Operator.IsBetween, field, [low, high]);
export const isNotBetween = (field: string, low: Operand, high: Operand): ComparisonCondition =>
  comparison(Operator.IsNotBetween, field, [low, high]);

export const isIn = (field: string, values: readonly Operand[]): ComparisonCondition =>
  comparison(Operator.IsIn, field, values);
export const isNotIn = (field: string, values: readonly Operand[]): ComparisonCondition =>
  comparison(Operator.IsNotIn, field, values);

/** What the conjunction builders take: conditions, arrays of them, or a mix, so `and(...filters)` and `and(filters)` both work. */
export type ConditionList = Condition | readonly Condition[];

function flatten(items: readonly ConditionList[]): Condition[] {
  const flat: Condition[] = [];

  for (const item of items) {
    if (Array.isArray(item)) {
      flat.push(...(item as readonly Condition[]));
    } else {
      flat.push(item as Condition);
    }
  }

  return flat;
}

/**
 * Every operand must match. Over no operands this matches everything, which is the identity of AND and the
 * natural "no filters were selected" case.
 */
export function and(...conditions: ConditionList[]): ConjunctionCondition {
  return conjunction(Operator.And, flatten(conditions));
}

/** Any operand must match. Over no operands this matches nothing, which is the identity of OR. */
export function or(...conditions: ConditionList[]): ConjunctionCondition {
  return conjunction(Operator.Or, flatten(conditions));
}

function conjunction(operator: Operator.And | Operator.Or, conditions: readonly Condition[]): ConjunctionCondition {
  conditions.forEach((condition, index) => {
    if (condition === null || condition === undefined) {
      throw new WeequeryError(`conditions[${index}] is null`);
    }
  });

  const built: ConjunctionCondition = { kind: 'conjunction', operator, conditions: [...conditions] };

  assertWithinNestingLimit(built);

  return built;
}

/**
 * Build a quantifier over a bound collection.
 *
 * ```ts
 * any('Assignments', and(eq('LairID', 5), eq('IsPrimary', true)))
 * ```
 *
 * @throws {WeequeryError} the operator is not a quantifier, the field is missing, or the tree nests too deep
 */
export function quantify(operator: QuantifierOperator, field: string, condition: Condition): QuantifiedCondition {
  if (!isQuantifierOperator(operator)) {
    throw new WeequeryError(
      `Operator ${OPERATOR_NAMES[operator as Operator] ?? String(operator)} is not a quantifier, so it cannot quantify over '${String(field)}': the quantifiers are Any, All and None`,
    );
  }

  if (typeof field !== 'string' || field.length === 0) {
    throw new WeequeryError(`A quantifier needs the key of a bound collection, and '${String(field)}' is not one`);
  }

  if (condition === null || condition === undefined) {
    throw new WeequeryError(`${OPERATOR_NAMES[operator]} on '${field}' has no condition to quantify`);
  }

  const built: QuantifiedCondition = { kind: 'quantified', operator, field, condition };

  assertWithinNestingLimit(built);

  return built;
}

/** At least one element of the collection satisfies the condition. False of an empty collection. */
export const any = (field: string, condition: Condition): QuantifiedCondition =>
  quantify(Operator.Any, field, condition);

/** Every element does. True of an empty collection, and of one that is not there at all. */
export const all = (field: string, condition: Condition): QuantifiedCondition =>
  quantify(Operator.All, field, condition);

/** No element does, which is the negation of {@link any}. True of an empty collection. */
export const none = (field: string, condition: Condition): QuantifiedCondition =>
  quantify(Operator.None, field, condition);

/** Negate a condition. See {@link NotCondition} for why this is not the same as the negative operators. */
export function not(condition: Condition): NotCondition {
  if (condition === null || condition === undefined) {
    throw new WeequeryError('Not has no condition to negate');
  }

  const built: NotCondition = { kind: 'not', condition };

  assertWithinNestingLimit(built);

  return built;
}

// ---------- walking one ----------

/** The children of a condition, which is empty for a comparison. */
export function children(condition: Condition): readonly Condition[] {
  switch (condition.kind) {
    case 'conjunction':
      return condition.conditions;
    case 'not':
    case 'quantified':
      return [condition.condition];
    default:
      return [];
  }
}

/**
 * How deeply a condition nests. Answers without walking deeper than the limit, so it is safe on a tree of any
 * depth including one that would overflow the stack to walk in full.
 */
export function nestingDepth(condition: Condition, ceiling = MAX_NESTING_DEPTH + 1): number {
  let deepest = 0;
  let level: readonly Condition[] = [condition];

  // Breadth first, so the walk is iterative and a pathological tree cannot take the stack with it
  while (level.length > 0 && deepest <= ceiling) {
    const next: Condition[] = [];

    for (const node of level) {
      for (const child of children(node)) {
        next.push(child);
      }
    }

    if (next.length === 0) {
      break;
    }

    deepest += 1;
    level = next;
  }

  return deepest;
}

/** Whether a condition nests deeper than the limit. */
export function isTooDeep(condition: Condition): boolean {
  return nestingDepth(condition) > MAX_NESTING_DEPTH;
}

export function assertWithinNestingLimit(condition: Condition): void {
  if (isTooDeep(condition)) {
    throw new WeequeryError(`Condition nests deeper than the limit of ${MAX_NESTING_DEPTH}`);
  }
}

/** Visit every condition in a tree, parents before children. */
export function walk(condition: Condition, visit: (node: Condition, depth: number) => void, depth = 0): void {
  visit(condition, depth);

  for (const child of children(condition)) {
    walk(child, visit, depth + 1);
  }
}

/**
 * Every field a condition names, including the ones its operands name, without duplicates.
 *
 * Stops at a quantifier, taking the collection's key and not the fields inside it. Those resolve against the
 * collection's own allow-list rather than the entity's, so putting them in one flat list would say a field is
 * bound on the entity when it is not. Use {@link QuantifiedCondition.condition} and call this on it for the
 * inner names.
 */
export function fieldsUsed(condition: Condition): string[] {
  const seen = new Map<string, string>();

  const visit = (node: Condition): void => {
    if (node.kind === 'quantified') {
      seen.set(node.field.toLowerCase(), node.field);
      return;
    }

    if (node.kind !== 'comparison') {
      for (const child of children(node)) {
        visit(child);
      }
      return;
    }

    seen.set(node.field.toLowerCase(), node.field);

    for (const value of node.values) {
      if (value.source === ValueSource.Binding) {
        seen.set(value.value.toLowerCase(), value.value);
      }
    }
  };

  visit(condition);

  return [...seen.values()];
}

/** The shape a comparison's operator calls for, for a caller inspecting a tree it was handed. */
export function shapeOf(condition: ComparisonCondition): ConditionShape {
  return shapeForOperation(condition.operator) ?? ConditionShape.NoValue;
}
