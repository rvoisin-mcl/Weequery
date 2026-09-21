import { ComparisonCondition, Condition, ConditionValue, validateValueCount } from './condition.js';
import { WeequeryError } from './errors.js';
import {
  ConditionShape,
  MAX_NESTING_DEPTH,
  Operator,
  operationString,
  shapeForOperation,
  ValueSource,
} from './operators.js';
import { fieldText, indexText, isBareWord, quote } from './syntax.js';

/** What a condition nested past the limit is written as when the caller cannot be thrown at. */
const TOO_DEEP = '<nested too deep>';

/**
 * Write a condition as a query string that {@link parseQuery} reads back.
 *
 * The round trip preserves meaning, not types. Every value is written as text and comes back as text, which the
 * server parses against the bound property's type when the query is built. One consequence worth knowing if you
 * compare the text: a condition built from numbers writes its values unquoted, while one that came from the
 * parser holds strings and writes them quoted, so `parseQuery(toQuery(x))` can differ from `x` by the quoting,
 * and is stable from there on.
 *
 * @throws {WeequeryError} the condition has no representation in the language, or nests too deep
 */
export function toQuery(condition: Condition): string {
  if (condition === null || condition === undefined) {
    throw new WeequeryError('condition cannot be null');
  }

  return render(condition, true, 0);
}

/**
 * Write a condition for display. Prefers the round-trippable form but will not throw, because a `toString` that
 * throws makes debugging worse.
 */
export function describe(condition: Condition | null | undefined): string {
  return condition === null || condition === undefined ? '' : render(condition, false, 0);
}

function render(condition: Condition, strict: boolean, depth: number): string {
  // Writing recurses, so a tree deep enough would overflow the stack. Strict callers are told, since a string
  // they cannot parse back is no use to them, while describe settles for saying where it stopped.
  if (depth > MAX_NESTING_DEPTH) {
    if (strict) {
      throw new WeequeryError(`Condition nests deeper than the limit of ${MAX_NESTING_DEPTH}`);
    }

    return TOO_DEEP;
  }

  switch (condition.kind) {
    case 'comparison':
      return renderComparison(condition);

    case 'conjunction':
      return renderConjunction(condition.operator, condition.conditions, strict, depth);

    case 'not': {
      // '!' can butt up against its operand, 'NOT' needs a space to stay a separate word
      const not = operationString(Operator.Not);
    
      return `${not} ${render(condition.condition, strict, depth + 1)}`;
    }

    case 'quantified': {
      const name = operationString(condition.operator);

      return `(${fieldText(condition.field)} ${name} ${grouped(condition.condition, strict, depth + 1)})`;
    }

    default: {
      const unknown = condition as { kind?: unknown };
      if (strict) {
        throw new WeequeryError(`Condition kind '${String(unknown.kind)}' cannot be written as a query`);
      }
      return `<${String(unknown.kind)}>`;
    }
  }
}

/**
 * A condition in the parentheses a quantifier requires after its operator.
 *
 * Almost everything the writer emits already wraps itself, and a second pair would only be noise that reads back
 * the same, so the parentheses are added only where they are load bearing. A negation leads with its operator
 * instead, and `Any NOT (...)` would not parse.
 */
function grouped(condition: Condition, strict: boolean, depth: number): string {
  const text = render(condition, strict, depth);

  return condition.kind === 'not' ? `(${text})` : text;
}

function renderConjunction(
  operator: Operator.And | Operator.Or,
  conditions: readonly Condition[],
  strict: boolean,
  depth: number,
): string {
  const separator = ` ${operationString(operator)} `;

  if (conditions.length === 0) {
    // The language has no way to say "match everything" or "match nothing" on its own. The parser can never
    // produce this; it only arrives from a hand built tree.
    if (strict) {
      throw new WeequeryError(
        `An empty ${operationString(operator, 'native')} condition has no representation in the query language, so it cannot be round tripped`,
      );
    }

    return `(<empty ${operationString(operator, 'native')}>)`;
  }

  return `(${conditions.map((child) => render(child, strict, depth + 1)).join(separator)})`;
}

function renderComparison(condition: ComparisonCondition): string {
  // A tree that came from a builder, the parser or a payload has already been checked. One assembled by hand has
  // not, and writing a comparison with the wrong number of operands would produce text nothing can read back.
  validateValueCount(condition.operator, condition.field, condition.values.length);

  const operands = condition.values.map((operand) => renderOperand(operand, condition.holdsText));

  const field = `${fieldText(condition.field)}${indexText(condition.index)}`;
  const op = operationString(condition.operator);

  switch (shapeForOperation(condition.operator)) {
    case ConditionShape.NoValue:
      return `(${field} ${op})`;

    case ConditionShape.OneValue:
      return `(${field} ${op} ${operands[0]!})`;

    // Both list shapes write their operands in parentheses, which is what the parser reads back
    case ConditionShape.TwoValue:
    case ConditionShape.MultipleValue:
      return `(${field} ${op} (${operands.join(', ')}))`;

    default:
      throw new WeequeryError(`Operator '${operationString(condition.operator)}' cannot be written as a comparison`);
  }
}

/**
 * One thing on the right of a comparison: a property in the brackets that say so, which is how it is read back,
 * or a value written as any other value is. Quoting a key would make it a value again.
 */
function renderOperand(operand: ConditionValue, quoteValues: boolean): string {
  // An operand naming a property is written the way a field is, which is the same "[Key]" for an ordinary key
  // and the same "[Key][index]" for one carrying an index. Writing the index inside the key's brackets,
  // "[Tallies[apples]]", is the shape that does not read back.
  if (operand.source === ValueSource.Binding) {
    return fieldText(operand.value);
  }

  return quoteValues || !isBareWord(operand.value) ? quote(operand.value) : operand.value;
}
