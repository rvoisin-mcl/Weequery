import { describeError, WeequeryError } from './errors.js';
import { fieldText, splitIndex } from './syntax.js';
import { Token, TokenKind, tokenize } from './tokenizer.js';

/**
 * Which bound fields a query reads back, for the caller that wants three columns rather than the whole row.
 *
 * The allow-list is the same one the conditions use: anything bound is projectable, matched without regard to
 * case, and a field nobody bound is refused exactly as it is in a condition. So a projection grants nothing that
 * filtering did not already.
 *
 * ```ts
 * parseProjection('Name, Pay');   // ['Name', 'Pay']
 * ```
 *
 * A field may carry an index, as it may anywhere else: `Tallies[apples]` projects that one element. What it may
 * not be is a bound collection, which has no single value to read.
 */
export type Projection = readonly string[];

/** The projection that names nothing, which is what a null or empty string reads as. */
export const NO_PROJECTION: Projection = Object.freeze([]);

/**
 * Read a projection from a comma separated list of field names.
 *
 * A field is written the way a condition or a sort writes one: bare, quoted, or between brackets, with an index
 * after it where it is taken at one. So the three read alike, and a key needing quotes needs them in the same
 * place everywhere.
 *
 * ```
 * Name, Pay
 * [Name], 'Total Pay', Tallies[apples]
 * ```
 *
 * A field named twice is kept once, in the position it first appeared. A projection is a set of columns and the
 * server hands back one entry per key, so there is nothing a duplicate could mean; refusing one would only make
 * a caller assembling a list from checkboxes deduplicate it first.
 *
 * A projection holds no operators, so there was never a spelling of one to settle here.
 *
 * @returns never null, and empty where nothing was named
 * @throws {WeequeryError} the list is malformed
 */
export function parseProjection(fields: string | null | undefined): string[] {
  const text = fields ?? '';
  const tokens = tokenize(text);

  if (tokens.length === 0) {
    return [];
  }

  let index = 0;

  const positionOfCurrentOrEnd = (): number => (index < tokens.length ? tokens[index]!.position : text.length);

  /**
   * The index a field is read at, where the brackets after it say so. Unambiguous here: what may follow a field
   * is a comma or the end, neither of which opens a bracket.
   */
  const parseIndex = (field: string): string | undefined => {
    if (index >= tokens.length || tokens[index]!.kind !== TokenKind.BracketOpen) {
      return undefined;
    }

    index += 1;

    if (index >= tokens.length || !(tokens[index]!.kind === TokenKind.Word || tokens[index]!.kind === TokenKind.Text)) {
      throw describeError(text, `Expected an index for field '${field}'`, positionOfCurrentOrEnd());
    }

    const found = tokens[index++]!.text;

    if (index >= tokens.length || tokens[index]!.kind !== TokenKind.BracketClose) {
      throw describeError(text, "Expected ']'", positionOfCurrentOrEnd());
    }

    index += 1;

    return found;
  };

  const parseName = (): string => {
    if (index < tokens.length && tokens[index]!.kind === TokenKind.BracketOpen) {
      index += 1;

      if (index >= tokens.length || tokens[index]!.kind !== TokenKind.Word) {
        throw describeError(text, 'Expected a field name', positionOfCurrentOrEnd());
      }

      const name = tokens[index]!.text;
      index += 1;

      if (index >= tokens.length || tokens[index]!.kind !== TokenKind.BracketClose) {
        throw describeError(text, "Expected ']'", positionOfCurrentOrEnd());
      }

      index += 1;
      return name;
    }

    if (index < tokens.length && tokens[index]!.kind === TokenKind.Text) {
      return tokens[index++]!.text;
    }

    if (index >= tokens.length || tokens[index]!.kind !== TokenKind.Word) {
      throw describeError(text, 'Expected a field name', positionOfCurrentOrEnd());
    }

    return tokens[index++]!.text;
  };

  const read: string[] = [];

  for (;;) {
    const name = parseName();
    const at = parseIndex(name);

    read.push(at === undefined ? name : `${name}[${at}]`);

    if (index < tokens.length && tokens[index]!.kind === TokenKind.Separator) {
      index += 1;
      continue;
    }

    break;
  }

  if (index < tokens.length) {
    throw describeError(text, `Unexpected '${tokens[index]!.text}'`, tokens[index]!.position);
  }

  return dedupe(read);
}

/**
 * Build a projection from keys already in hand, for the caller assembling one rather than reading it.
 *
 * @returns never null, and empty where nothing was named; duplicates are kept once
 * @throws {WeequeryError} a field is not a non-empty string
 */
export function projection(fields: readonly string[] | null | undefined): string[] {
  if (fields === null || fields === undefined) {
    return [];
  }

  for (const field of fields) {
    if (typeof field !== 'string' || field.length === 0) {
      throw new WeequeryError(`A projected field has to be the name of a bound one, and '${String(field)}' is not`);
    }
  }

  return dedupe(fields);
}

/** Keys are matched without regard to case, so two spellings of one key are one column. */
function dedupe(fields: readonly string[]): string[] {
  const kept: string[] = [];
  const seen = new Set<string>();

  for (const field of fields) {
    const lowered = field.toLowerCase();

    if (!seen.has(lowered)) {
      seen.add(lowered);
      kept.push(field);
    }
  }

  return kept;
}

/**
 * Write a projection back out, such that {@link parseProjection} reads it back.
 *
 * @returns the empty string where nothing is named
 */
export function projectionToQuery(fields: Projection): string {
  return fields.map(fieldText).join(', ');
}

/** Every key a projection names, with any index stripped off, which is what has to be bound. */
export function projectedKeys(fields: Projection): string[] {
  return fields.map((field) => splitIndex(field).key);
}

/**
 * The field that stands for every field a caller may read, and the suffix that stands for every one under a
 * prefix: `*` and `Lair.*`.
 *
 * Expanded by the server when the projection is built rather than when it is parsed, because what it expands to
 * is the binding set. That also means it survives a round trip as whatever the caller wrote, which is why the
 * writer here leaves it alone.
 */
export const PROJECTION_WILDCARD = '*';

/** Whether a projected field stands for all of the bound ones. */
export function isEverything(field: string): boolean {
  return field === PROJECTION_WILDCARD;
}

/**
 * What a projected field stands for the whole of, or null where it names one thing.
 *
 * The trailing dot is kept, which is the point of returning a prefix rather than a name: matching on `Lair`
 * would sweep in a key called `Lairyard.Capacity`, and matching on `Lair.` cannot.
 *
 * @returns `'Lair.'` for `'Lair.*'`, and null for anything else
 */
export function wildcardPrefix(field: string): string | null {
  return field.endsWith(`.${PROJECTION_WILDCARD}`) ? field.slice(0, -PROJECTION_WILDCARD.length) : null;
}

/**
 * Whether a key is one of the ones a prefix stands for. Matched without regard to case, as keys are everywhere.
 *
 * @param prefix as {@link wildcardPrefix} returned it, with its dot still on
 */
export function isUnder(key: string, prefix: string): boolean {
  return key.toLowerCase().startsWith(prefix.toLowerCase());
}

/**
 * The word that introduces a projection inside a combined query string.
 *
 * One word, as every separator and every operator here is. A binding may not be named for it.
 */
export const SELECT_PREFIX = 'Select';

/**
 * How many tokens the projection separator takes at a given point, or zero where there is none.
 *
 * The counterpart of the sort clause's `prefixLength`, and simpler for the reason above: one word, one
 * spelling, so it is either there or it is not.
 *
 * @returns 1 for Select, 0 for anything else
 */
export function selectPrefixLength(tokens: readonly Token[], index: number): number {
  if (index >= tokens.length) {
    return 0;
  }

  const token = tokens[index]!;

  return token.kind === TokenKind.Word && token.text.toUpperCase() === SELECT_PREFIX.toUpperCase() ? 1 : 0;
}
