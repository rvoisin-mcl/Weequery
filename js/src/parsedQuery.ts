import { Condition } from './condition.js';
import { describeError } from './errors.js';
import { parseLeading } from './parser.js';
import {
  NO_PROJECTION,
  parseProjection,
  Projection,
  projectionToQuery,
  SELECT_PREFIX,
  selectPrefixLength,
} from './projection.js';
import { parseLeadingSorts, prefixLength, Sort, SORT_SEPARATOR, sortsToQuery } from './sort.js';
import { Token, tokenize } from './tokenizer.js';
import { toQuery } from './writer.js';

/** A condition, a sort clause and a projection read from one string, for the caller with one box rather than three. */
export interface ParsedQuery {
  /** What to filter by, or null where the string asked for no filtering. */
  readonly condition: Condition | null;
  /** What to sort by, in the order they apply; never null, and empty where nothing was asked. */
  readonly sorts: readonly Sort[];
  /** Which fields to read back; never null, and empty where nothing was asked, which is the whole row. */
  readonly projection: Projection;
}

/**
 * Read a condition, a sort clause and a projection from one string, each introduced by its own word.
 *
 * The separators are what tell the parts apart, so unlike the clauses read on their own, here each is required
 * to introduce its own. Without any of them the whole string is a condition. Every part may be left out:
 *
 * ```
 * Pay > 10000 OrderBy Pay DESC Select Name, Pay   all three
 * Pay > 10000 Select Name, Pay                    a condition and a projection, and no sorting
 * Pay > 10000                                     a condition, and whatever default sort was given
 * OrderBy Pay DESC Select Name                    sorts and a projection, and no filtering at all
 * Select Name, Pay                                a projection, and nothing else
 * ```
 *
 * The order is fixed, condition then sorts then projection, because where a part sits is the only thing saying
 * which part it is. A Select before an OrderBy is refused rather than put back in order.
 *
 * Where each split falls is found by reading the part before it and seeing where it stops, not by searching the
 * text, so a value that spells a separator is still a value: `Name = 'Select'` is one comparison and no
 * projection.
 *
 */
export function parseParsedQuery(
  query: string | null | undefined,
  defaultSort?: readonly Sort[] | null,
): ParsedQuery {
  const text = query ?? '';
  const tokens = tokenize(text);

  if (tokens.length === 0) {
    return { condition: null, sorts: [...(defaultSort ?? [])], projection: NO_PROJECTION };
  }

  // No condition, only what a separator introduces
  if (startsAPart(tokens, 0)) {
    const tail = parseTail(text, defaultSort);

    return { condition: null, sorts: tail.sorts, projection: tail.projection };
  }

  const { condition, stopped } = parseLeading(tokens, text);

  // A condition and nothing after it
  if (stopped >= tokens.length) {
    return { condition, sorts: [...(defaultSort ?? [])], projection: NO_PROJECTION };
  }

  // Unexpected text between the condition and whatever follows it. ORDER BY throws from prefixLength rather
  // than going unmatched, so the separator a caller did write is named instead of being reported as a stray
  // word. Against the whole query, since that is the text they sent.
  if (!startsAPart(tokens, stopped)) {
    throw describeError(text, `Unexpected '${tokens[stopped]!.text}'`, tokens[stopped]!.position);
  }

  const tail = parseTail(text.slice(tokens[stopped]!.position), defaultSort);

  return { condition, sorts: tail.sorts, projection: tail.projection };
}

/** Whether a separator sits at this point, so whether what follows is a part rather than stray text. */
function startsAPart(tokens: readonly Token[], index: number): boolean {
  return prefixLength(tokens, index) > 0 || selectPrefixLength(tokens, index) > 0;
}

/**
 * Everything after the condition: an optional sort clause, then an optional projection.
 *
 * Given its own text rather than an offset into the query, so the two clause readers see the same strings they
 * would have been handed on their own and their messages point where they always did.
 */
function parseTail(
  tail: string,
  defaultSort: readonly Sort[] | null | undefined,
): { sorts: Sort[]; projection: Projection } {
  const tokens = tokenize(tail);

  if (tokens.length === 0) {
    return { sorts: [...(defaultSort ?? [])], projection: NO_PROJECTION };
  }

  // A projection with no sorts in front of it, so the default stands in as it does anywhere else
  if (selectPrefixLength(tokens, 0) > 0) {
    return { sorts: [...(defaultSort ?? [])], projection: fieldsAfter(tail, tokens, 0) };
  }

  const { sorts, stopped } = parseLeadingSorts(tokens, tail, defaultSort);

  if (stopped >= tokens.length) {
    return { sorts, projection: NO_PROJECTION };
  }

  if (selectPrefixLength(tokens, stopped) === 0) {
    throw describeError(tail, `Unexpected '${tokens[stopped]!.text}'`, tokens[stopped]!.position);
  }

  return { sorts, projection: fieldsAfter(tail, tokens, stopped) };
}

/**
 * The field list a Select introduces, which has to name at least one field.
 *
 * A Select with nothing after it is refused rather than read as the projection that names nothing. The second
 * is a real thing and it is what leaving the Select off says; somebody who typed the word and then stopped
 * meant to name a column.
 */
function fieldsAfter(text: string, tokens: readonly Token[], select: number): Projection {
  if (select + 1 >= tokens.length) {
    throw describeError(text, `'${SELECT_PREFIX}' names no fields`, tokens[select]!.position);
  }

  return parseProjection(text.slice(tokens[select + 1]!.position));
}

/**
 * Write every part back out as one string, such that {@link parseParsedQuery} reads it back.
 *
 * Each part is written only where there is one, and the separators are what hold them apart, so what comes out
 * reads back as what went in. Nothing at all is the empty string rather than a dangling separator.
 */
export function parsedQueryToQuery(parsed: ParsedQuery): string {
  const parts: string[] = [];

  if (parsed.condition !== null) {
    parts.push(toQuery(parsed.condition));
  }

  // The clause with no prefix, so the separator can be put on. Unlike a standalone clause it is not optional
  // here: it is one of the things telling the parts apart
  const clause = sortsToQuery(parsed.sorts);

  if (clause.length > 0) {
    parts.push(`${SORT_SEPARATOR} ${clause}`);
  }

  if (parsed.projection.length > 0) {
    parts.push(`${SELECT_PREFIX} ${projectionToQuery(parsed.projection)}`);
  }

  return parts.join(' ');
}
