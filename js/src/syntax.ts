import { WeequeryError } from './errors.js';
import { Operator, OPERATOR_NAMES } from './operators.js';

/**
 * The words the tokenizer gives a meaning of their own, wherever they appear. A value or a field name spelling
 * one of these has to be quoted to be read as itself.
 */
const KEYWORDS: ReadonlySet<string> = new Set(['AND', 'OR', 'NOT', 'NULL']);

/**
 * Everything the query language claims, so everything a binding key may not be.
 *
 * Gathered rather than listed, the way the C# side gathers it: the keywords, the words SQL spells its operators
 * with, the separator, and the canonical name of every operator. An operator added later is reserved by having
 * been added.
 */
const RESERVED: ReadonlySet<string> = (() => {
  const reserved = new Set<string>(KEYWORDS);

  // The multi-word SQL spellings, word by word, since that is how the parser reads them
  reserved.add('IS');
  reserved.add('IN');
  reserved.add('BETWEEN');

  // The two words a combined query string is split on, each read as a separator wherever a part could begin
  // rather than as a field. ORDER and BY are read only together and so are not reserved: a field named Order
  // is filtered and sorted on like any other.
  reserved.add('ORDERBY');
  reserved.add('SELECT');

  for (const name of Object.values(OPERATOR_NAMES)) {
    if (isSqlName(name)) {
      reserved.add(name.toUpperCase());
    }
  }

  return reserved;
})();

/** Whether the tokenizer gives this word a meaning of its own. */
export function isKeyword(text: string): boolean {
  return KEYWORDS.has(text.toUpperCase());
}

/** Whether the query language claims this word, so whether it is unusable as a binding key. */
export function isReserved(text: string | null | undefined): boolean {
  return text !== null && text !== undefined && RESERVED.has(text.toUpperCase());
}

/**
 * A valid unquoted SQL name: an ASCII letter or underscore, then letters, digits or underscores. Deliberately
 * ASCII only, and deliberately the same rule as the C# side's `WeequeryException.IsSqlName`.
 *
 * One name, so no period. {@link isBindingKey} is the looser rule a key is held to.
 */
export function isSqlName(text: string | null | undefined): boolean {
  if (text === null || text === undefined || text.length === 0) {
    return false;
  }

  return /^[A-Za-z_][A-Za-z0-9_]*$/.test(text);
}

/**
 * Whether a string is shaped like a binding key: one or more {@link isSqlName} segments separated by periods.
 *
 * The period is what lets a nested property be bound under the path it already has. Every segment is held to the
 * whole of the name rule, so a leading or trailing period, two in a row, and a segment starting with a digit are
 * all still refused.
 */
export function isQualifiedSqlName(text: string | null | undefined): boolean {
  if (text === null || text === undefined || text.length === 0) {
    return false;
  }

  // split refuses a leading or trailing period and two in a row on its own, each producing an empty segment
  return text.split('.').every(isSqlName);
}

/**
 * Whether a string can be used as a binding key: shaped like one, and not a word the language has claimed.
 *
 * A key is written as a bare field name, so one spelling an operator makes a query that reads two ways. Only the
 * whole key is checked, since only a whole word is promoted: `Lair.And` is one word to the tokenizer and is not
 * the conjunction.
 */
export function isBindingKey(text: string | null | undefined): boolean {
  return isQualifiedSqlName(text) && !isReserved(text);
}

/** Why a key was refused, for a message that says more than "no". */
export function bindingKeyProblem(text: string | null | undefined): string | null {
  if (text === null || text === undefined || text.length === 0) {
    return 'a binding key cannot be empty';
  }

  if (!isQualifiedSqlName(text)) {
    return `'${text}' is not a valid binding key, which is one or more unquoted SQL names separated by periods, each a letter or underscore followed by letters, digits or underscores`;
  }

  if (isReserved(text)) {
    return `'${text}' is a word the query language claims, so it cannot be a binding key`;
  }

  return null;
}

/**
 * Characters that terminate an unquoted word. '.', '-', ':' and '_' are deliberately absent, so property paths,
 * negative numbers, dates and GUIDs need no quoting.
 */
export function isWordTerminator(ch: string): boolean {
  return /\s/.test(ch) || isQuote(ch) || '()[],=!<>&|'.includes(ch);
}

/** Both quote characters open a literal, and a literal is closed by whichever one opened it. */
export function isQuote(ch: string): boolean {
  return ch === "'" || ch === '"';
}

/**
 * Whether text can appear unquoted and come back as the single word it went in as. This is the rule the writer
 * uses to decide what needs quoting, so the writer and the tokenizer cannot drift apart.
 */
export function isBareWord(text: string | null | undefined): boolean {
  if (text === null || text === undefined || text.length === 0) {
    return false;
  }

  for (const ch of text) {
    if (isWordTerminator(ch)) {
      return false;
    }
  }

  return !isKeyword(text);
}

/**
 * Quote and escape a string so it survives a trip back through the parser.
 *
 * Escapes the backslash as well as the quote, although the parser only needs the second: a backslash it does not
 * recognise as an escape stays in the value. The one case it could not read back is a backslash immediately
 * before the closing quote, where `'a\'` would look like an escaped quote and swallow it.
 */
export function quote(value: string | null | undefined): string {
  return `'${(value ?? '').replace(/\\/g, '\\\\').replace(/'/g, "\\'")}'`;
}

/** Bracket quote a field whose name is a plain word, and single quote anything else. */
export function fieldText(field: string): string {
  // A field that carries its own index, which is how an operand and a sort hold one, is written as the two
  // bracket pairs the parser reads back rather than quoted whole: "[Tallies][apples]", not "'Tallies[apples]'"
  if (field.includes('[')) {
    const { key, index } = splitIndex(field);

    return `${fieldText(key)}${indexText(index)}`;
  }

  return isBareWord(field) ? `[${field}]` : quote(field);
}

/**
 * The brackets that say which element of a collection is being tested, written after the field:
 * `[Tallies][apples]`. Empty where there is no index.
 *
 * The index is quoted on the same rule a value is, so a dictionary key with a space or a delimiter in it survives
 * the trip and a number stays readable.
 */
export function indexText(index: string | undefined): string {
  if (index === undefined) {
    return '';
  }

  return `[${isBareWord(index) ? index : quote(index)}]`;
}

/**
 * Take a field name apart into the key and the index it may carry: `Tallies[apples]` is the binding Tallies read
 * at `apples`, and `Pay` is the binding Pay.
 *
 * For the two places an index arrives written into the field itself rather than beside it: an operand naming
 * another bound property, and a sort. A condition keeps the two apart, having somewhere to put it.
 */
export function splitIndex(field: string): { key: string; index: string | undefined } {
  const open = field.indexOf('[');
  if (open < 0) {
    return { key: field, index: undefined };
  }

  if (!field.endsWith(']')) {
    throw new WeequeryError(`'${field}' has a '[' that is never closed`);
  }

  const index = field.slice(open + 1, -1);
  if (index.length === 0) {
    throw new WeequeryError(`'${field}' has an empty index`);
  }

  return { key: field.slice(0, open), index };
}

/** The characters a backslash may escape inside a quoted literal: the two quotes, and itself. */
export function isEscapable(ch: string): boolean {
  return ch === "'" || ch === '"' || ch === '\\';
}

/** The operator names, for the parser's lookup. Case is not significant anywhere in the language. */
export const OPERATOR_LOOKUP: ReadonlyMap<string, Operator> = new Map<string, Operator>([
  ['isnull', Operator.IsNull],
  ['isnotnull', Operator.IsNotNull],
  ['==', Operator.Equals],
  ['!=', Operator.NotEqual],
  ['<', Operator.LessThan],
  ['<=', Operator.LessThanOrEqual],
  ['>', Operator.GreaterThan],
  ['>=', Operator.GreaterThanOrEqual],
  ['startswith', Operator.StartsWith],
  ['doesnotstartwith', Operator.DoesNotStartWith],
  ['endswith', Operator.EndsWith],
  ['doesnotendwith', Operator.DoesNotEndWith],
  ['contains', Operator.Contains],
  ['doesnotcontain', Operator.DoesNotContain],
  ['ismatch', Operator.IsMatch],
  ['doesnotmatch', Operator.DoesNotMatch],
  ['isin', Operator.IsIn],
  ['isnotin', Operator.IsNotIn],
  ['isbetween', Operator.IsBetween],
  ['isnotbetween', Operator.IsNotBetween],

  // The single word SQL spellings. Native reads these too, being one word each; the multi-word ones
  // (IS NULL, IS NOT NULL, NOT IN, NOT BETWEEN) cannot live in a lookup keyed on one token.
  ['in', Operator.IsIn],
  ['between', Operator.IsBetween],

  // The quantifiers. In operator position what follows one is a parenthesised condition rather than a value.
  ['any', Operator.Any],
  ['all', Operator.All],
  ['none', Operator.None],
]);
