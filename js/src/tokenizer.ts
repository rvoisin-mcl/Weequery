import { WeequeryError } from './errors.js';
import { isEscapable, isQuote, isWordTerminator } from './syntax.js';

export enum TokenKind {
  /** Grouping open, '(' */
  GroupOpen = 'GroupOpen',
  /** Grouping close, ')' */
  GroupClose = 'GroupClose',
  /** Name open, '['. Quotes a field name, or names a bound property where a value is expected. */
  BracketOpen = 'BracketOpen',
  /** Name close, ']' */
  BracketClose = 'BracketClose',
  /** Value list separator, ',' */
  Separator = 'Separator',
  /** '&&' or 'AND' */
  And = 'And',
  /** '||' or 'OR' */
  Or = 'Or',
  /** '!' or 'NOT' */
  Not = 'Not',
  /** Comparison symbol, already normalized to its canonical form (so '=' arrives as '==') */
  Symbol = 'Symbol',
  /** Unquoted run of characters: a field name, a named operator, or a bare literal */
  Word = 'Word',
  /** Quoted literal, with escapes already resolved */
  Text = 'Text',
}

export interface Token {
  readonly kind: TokenKind;
  /** For a Text token the surrounding quotes are stripped and escapes resolved. */
  readonly text: string;
  /** Index into the source query the token started at. */
  readonly position: number;
}

/**
 * The message for a spelling `native` does not accept. Always names the spelling to use instead, since what is
 * being refused is one that worked for years and the caller is entitled to know where it went.
 */
function refuse(found: string, instead: string, position: number): WeequeryError {
  return new WeequeryError(
    `'${found}' at position ${position} is not valid in the Native style, write '${instead}'`,
    position,
  );
}

/**
 * Turn a query into tokens.
 *
 * The conjunction symbols are refused: this reads the one grammar, and each refusal names the word to write
 * instead. The comparison symbols are read, being unambiguous, and what comes back out is the one form. What
 * gets written.
 */
export function tokenize(query: string): Token[] {
  const tokens: Token[] = [];

  if (query === null || query === undefined || query.trim().length === 0) {
    return tokens;
  }

  let i = 0;

  while (i < query.length) {
    const ch = query[i]!;

    if (/\s/.test(ch)) {
      i += 1;
      continue;
    }

    const simple = simpleToken(ch);
    if (simple !== null) {
      tokens.push({ kind: simple, text: ch, position: i });
      i += 1;
      continue;
    }

    if (isQuote(ch)) {
      i = readText(query, i, ch, tokens);
      continue;
    }

    const afterOperator = tryReadOperator(query, i, tokens);
    if (afterOperator !== null) {
      i = afterOperator;
      continue;
    }

    i = readWord(query, i, tokens);
  }

  return tokens;
}

function simpleToken(ch: string): TokenKind | null {
  switch (ch) {
    case '(':
      return TokenKind.GroupOpen;
    case ')':
      return TokenKind.GroupClose;
    case '[':
      return TokenKind.BracketOpen;
    case ']':
      return TokenKind.BracketClose;
    case ',':
      return TokenKind.Separator;
    default:
      return null;
  }
}

/**
 * Read a quoted literal, closing on whichever quote opened it, so both 'text' and "text" work and the other
 * quote needs no escaping inside. A backslash escapes a quote or another backslash; in front of anything else it
 * is part of the value, so `'\w'` is the two characters it looks like.
 */
function readText(query: string, start: number, quoteChar: string, tokens: Token[]): number {
  let builder = '';

  for (let i = start + 1; i < query.length; i += 1) {
    const ch = query[i]!;

    if (ch === '\\' && i + 1 < query.length && isEscapable(query[i + 1]!)) {
      builder += query[i + 1]!;
      i += 1;
      continue;
    }

    if (ch === quoteChar) {
      tokens.push({ kind: TokenKind.Text, text: builder, position: start });
      return i + 1;
    }

    builder += ch;
  }

  throw new WeequeryError(`Unterminated ${quoteChar} quote starting at position ${start}`, start);
}

/**
 * Read a comparison or conjunction operator, normalizing the alternate spellings ('=' and '<>') as we go.
 *
 * @returns the index after the operator, or null if the character does not start one
 */
function tryReadOperator(query: string, start: number, tokens: Token[]): number | null {
  const ch = query[start]!;
  const peek = start + 1 < query.length ? query[start + 1]! : '';

  switch (ch) {
    case '=':
      // '=' and '==' both mean Equals; '=' is the one form written back
      tokens.push({ kind: TokenKind.Symbol, text: '==', position: start });
      return peek === '=' ? start + 2 : start + 1;

    case '!':
      if (peek === '=') {
        tokens.push({ kind: TokenKind.Symbol, text: '!=', position: start });
        return start + 2;
      }

      throw refuse('!', 'NOT', start);

    case '<':
      // '<>' is an alternate form of '!='
      tokens.push({
        kind: TokenKind.Symbol,
        text: peek === '>' ? '!=' : peek === '=' ? '<=' : '<',
        position: start,
      });
      return peek === '>' || peek === '=' ? start + 2 : start + 1;

    case '>':
      tokens.push({ kind: TokenKind.Symbol, text: peek === '=' ? '>=' : '>', position: start });
      return peek === '=' ? start + 2 : start + 1;

    case '&':
      throw refuse('&&', 'AND', start);

    case '|':
      throw refuse('||', 'OR', start);

    default:
      return null;
  }
}

/**
 * Read an unquoted word, promoting the conjunction keywords. Everything else stays a Word; whether it is a
 * field, an operator name or a literal is decided by position.
 */
function readWord(query: string, start: number, tokens: Token[]): number {
  let end = start;
  while (end < query.length && !isWordTerminator(query[end]!)) {
    end += 1;
  }

  const word = query.slice(start, end);

  switch (word.toUpperCase()) {
    case 'AND':
      tokens.push({ kind: TokenKind.And, text: '&&', position: start });
      break;
    case 'OR':
      tokens.push({ kind: TokenKind.Or, text: '||', position: start });
      break;
    case 'NOT':
      tokens.push({ kind: TokenKind.Not, text: '!', position: start });
      break;
    default:
      tokens.push({ kind: TokenKind.Word, text: word, position: start });
      break;
  }

  return end;
}
