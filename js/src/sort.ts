import { describeError, WeequeryError } from './errors.js';
import { SortDirection } from './operators.js';
import { fieldText } from './syntax.js';
import { Token, TokenKind, tokenize } from './tokenizer.js';

/**
 * One sort. Several apply in the order given, each breaking ties in the one before, and the field must be bound
 * the same as a field in a condition.
 */
export interface Sort {
  readonly field: string;
  readonly direction: SortDirection;
}

export function asc(field: string): Sort {
  return { field, direction: SortDirection.Ascending };
}

export function desc(field: string): Sort {
  return { field, direction: SortDirection.Descending };
}

const DIRECTIONS: ReadonlyMap<string, SortDirection> = new Map([
  ['asc', SortDirection.Ascending],
  ['ascending', SortDirection.Ascending],
  ['desc', SortDirection.Descending],
  ['descending', SortDirection.Descending],
]);

function isWord(token: Token | undefined, text: string): boolean {
  return token !== undefined && token.kind === TokenKind.Word && token.text.toUpperCase() === text;
}

/**
 * How many tokens the prefix takes at a given point, or zero where there is none.
 *
 * Only OrderBy. The rule that applies to the operators, that a name is one word, is not one a separator gets
 * to be exempt from for being a separator. The two word spelling is still recognised rather than simply going
 * unmatched, so what a caller gets back names the spelling to use instead of a stray word called ORDER.
 *
 * @returns 1 for OrderBy, 0 for anything else, and ORDER BY is refused
 */
export function prefixLength(tokens: readonly Token[], index: number): number {
  // Two words, and only together
  if (index + 1 < tokens.length && isWord(tokens[index], 'ORDER') && isWord(tokens[index + 1], 'BY')) {
    throw new WeequeryError(
      `'ORDER BY' at position ${tokens[index]!.position} is not valid in the Native style, write 'OrderBy'`,
      tokens[index]!.position,
    );
  }

  // One word, and the only one Native will take
  if (index < tokens.length && isWord(tokens[index], 'ORDERBY')) {
    return 1;
  }

  return 0;
}

/**
 * Read a sort clause, falling back to the sorts the caller decided on where there is nothing to read.
 *
 * A comma separated list of fields, each optionally followed by a direction. Leave the direction off and it runs
 * ascending, as it does in SQL. `Asc`, `Ascending`, `Desc` and `Descending` are all accepted, case is ignored,
 * and the clause may open with `OrderBy`, which is the one spelling of the separator.
 *
 * The default is worth supplying wherever the query is paged: a page of an unordered query holds arbitrary rows.
 *
 * @returns never null; empty when there was nothing to read and no default
 */
export function parseSorts(
  clause: string | null | undefined,
  defaultSort?: readonly Sort[] | null,
): Sort[] {
  const text = clause ?? '';
  const tokens = tokenize(text);
  const { sorts, stopped } = parseLeadingSorts(tokens, text, defaultSort);

  // Anything left over means the clause was not a well formed list (eg. "Pay Name")
  if (stopped < tokens.length) {
    throw describeError(text, `Unexpected '${tokens[stopped]!.text}'`, tokens[stopped]!.position);
  }

  return sorts;
}

/**
 * Read as much of a sort clause as there is, and say where it stopped rather than refusing what follows it.
 *
 * The same split the condition parser performs, and for the same reason: a combined string puts a projection
 * after the sorts, so something has to read the sorts and hand back where they ended instead of treating the
 * next word as a malformed field.
 *
 * Where the clause ends is found by reading it, not by searching the text, so a field spelled like whatever
 * follows it is still a field.
 *
 * @param tokens the clause, already tokenized
 * @param text the text those tokens came from, for the error messages
 * @returns the sorts, and the token the clause stopped at, which is the count where it ran to the end
 */
export function parseLeadingSorts(
  tokens: readonly Token[],
  text: string,
  defaultSort?: readonly Sort[] | null,
): { sorts: Sort[]; stopped: number } {
  if (tokens.length === 0) {
    return { sorts: [...(defaultSort ?? [])], stopped: 0 };
  }

  let index = prefixLength(tokens, 0);
  const sorts: Sort[] = [];

  const positionOfCurrentOrEnd = (): number => (index < tokens.length ? tokens[index]!.position : text.length);

  /**
   * The index a sort field is taken at, where the brackets after it say so: `Tallies[apples] DESC`.
   *
   * Kept in the field's own text, a Sort having nowhere else to put it, and taken apart again on lookup.
   * Unambiguous here: what may follow a field is a direction, a comma or the end, none of which opens a bracket.
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

  const parseField = (): string => {
    const name = parseName();
    const at = parseIndex(name);

    return (at === undefined) ? name : `${name}[${at}]`;
  };

  for (;;) {
    const field = parseField();

    // Read only here, which is what lets a field be named Asc or Desc: a direction can never start a sort
    let direction = SortDirection.Ascending;
    if (index < tokens.length && tokens[index]!.kind === TokenKind.Word) {
      const named = DIRECTIONS.get(tokens[index]!.text.toLowerCase());
      if (named !== undefined) {
        direction = named;
        index += 1;
      }
    }

    sorts.push({ field, direction });

    if (index < tokens.length && tokens[index]!.kind === TokenKind.Separator) {
      index += 1;
      continue;
    }

    break;
  }

  return { sorts, stopped: index };
}

/** The one spelling a direction is written as. The parser reads the long forms too. */
function directionText(direction: SortDirection): string {
  return direction === SortDirection.Descending ? 'DESC' : 'ASC';
}

/**
 * The word that separates a condition from a sort clause when the two are written as one string. Mandatory
 * there, and one word for the same reason the operators are.
 */
export const SORT_SEPARATOR = 'OrderBy';

/**
 * Write a clause, in the order the sorts apply.
 *
 * The trip is exact, unlike a condition's: a sort carries no value to be read back against a property's type, so
 * what goes out comes back identical.
 */
export function sortsToQuery(sorts: readonly Sort[] | null | undefined): string {
  if (sorts === null || sorts === undefined) {
    return '';
  }

  const clauses: string[] = [];

  sorts.forEach((sort, index) => {
    if (sort === null || sort === undefined) {
      throw new WeequeryError(`sorts[${index}] is null`);
    }

    if (typeof sort.field !== 'string' || sort.field.length === 0) {
      throw new WeequeryError(`sorts[${index}].field is empty`);
    }

    clauses.push(`${fieldText(sort.field)} ${directionText(sort.direction)}`);
  });

  // Don't just return 'ORDER BY'
  if (clauses.length === 0) {
    return '';
  }

  return `${clauses.join(', ')}`;
}
