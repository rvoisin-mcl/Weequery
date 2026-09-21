import { binding, ComparisonCondition, Condition, ConditionValue, comparison, isTooDeep, quantify, raw } from './condition.js';
import { describeError, WeequeryError } from './errors.js';
import {
  ComparisonOperator,
  MAX_NESTING_DEPTH,
  Operator,
  operationString,
  isQuantifierOperator,
  QuantifierOperator,
  valuesRequired,
} from './operators.js';
import { OPERATOR_LOOKUP } from './syntax.js';
import { Token, TokenKind, tokenize } from './tokenizer.js';

/**
 * How deeply the *text* may nest before parsing gives up. Every group and every negation is one level.
 *
 * A guard on the parse stack, not the contract on conditions: what a caller is held to is
 * {@link MAX_NESTING_DEPTH}, checked against the tree once it is built, and the two are not the same count. Text
 * can nest without building anything, as the redundant parentheses in `(((A)))` do, and it can build without
 * nesting, as precedence does in `A AND B OR C AND D`.
 */
const MAX_SYNTAX_DEPTH = 64;

/**
 * Recursive descent parser for the query language. The grammar is:
 *
 * ```
 * expression  := disjunction
 * disjunction := conjunction ( ('||' | 'OR') conjunction )*
 * conjunction := unary ( ('&&' | 'AND') unary )*
 * unary       := ('!' | 'NOT') unary | primary
 * primary     := '(' expression ')' | quantified | comparison
 * quantified  := field ('Any' | 'All' | 'None') '(' expression ')'
 * comparison  := field operator operands?
 * field       := WORD | QUOTED | '[' WORD ']'
 * operands    := operand | '(' (operand (',' operand)*)? ')' | operand 'AND' operand
 * operand     := literal | '[' WORD ']'
 * literal     := WORD | QUOTED
 * ```
 *
 * Brackets name a bound property and parentheses hold a list, wherever either appears. AND binds tighter than
 * OR, matching SQL and C#, and parentheses group freely.
 */
class Parser {
  private index = 0;
  private depth = 0;

  constructor(
    private readonly tokens: readonly Token[],
    private readonly query: string,
  ) {}

  get position(): number {
    return this.index;
  }

  private get atEnd(): boolean {
    return this.index >= this.tokens.length;
  }

  private get current(): Token {
    return this.tokens[this.index]!;
  }

  private get positionOfCurrentOrEnd(): number {
    return this.atEnd ? this.query.length : this.current.position;
  }

  private check(kind: TokenKind): boolean {
    return !this.atEnd && this.current.kind === kind;
  }

  private match(kind: TokenKind): boolean {
    if (!this.check(kind)) {
      return false;
    }

    this.index += 1;
    return true;
  }

  private take(kind: TokenKind, expected: string): Token {
    if (!this.check(kind)) {
      throw describeError(this.query, `Expected ${expected}`, this.positionOfCurrentOrEnd);
    }

    return this.tokens[this.index++]!;
  }

  private describe(message: string, position: number): WeequeryError {
    return describeError(this.query, message, position);
  }

  /** The message for a worded spelling Native does not accept, naming the operator to write instead. */
  private refuse(found: string, op: Operator, position: number): WeequeryError {
    return this.describe(
      `'${found}' is not valid in the Native style, write '${operationString(op)}'`,
      position,
    );
  }

  private descend(position: number): void {
    if (this.depth >= MAX_SYNTAX_DEPTH) {
      throw this.describe(`Grouping nested deeper than the limit of ${MAX_SYNTAX_DEPTH}`, position);
    }

    this.depth += 1;
  }

  parseDisjunction(): Condition {
    const first = this.parseConjunction();
    if (!this.check(TokenKind.Or)) {
      return first;
    }

    // Flatten 'A OR B OR C' into a single Or over three operands rather than nesting
    const operands: Condition[] = [first];
    while (this.match(TokenKind.Or)) {
      operands.push(this.parseConjunction());
    }

    return { kind: 'conjunction', operator: Operator.Or, conditions: operands };
  }

  private parseConjunction(): Condition {
    const first = this.parseUnary();
    if (!this.check(TokenKind.And)) {
      return first;
    }

    const operands: Condition[] = [first];
    while (this.match(TokenKind.And)) {
      operands.push(this.parseUnary());
    }

    return { kind: 'conjunction', operator: Operator.And, conditions: operands };
  }

  private parseUnary(): Condition {
    // Right associative, so 'NOT NOT A' is legal and means A
    if (this.check(TokenKind.Not)) {
      const position = this.current.position;
      this.index += 1;

      this.descend(position);
      const operand = this.parseUnary();
      this.depth -= 1;

      return { kind: 'not', condition: operand };
    }

    return this.parsePrimary();
  }

  private parsePrimary(): Condition {
    if (this.check(TokenKind.GroupOpen)) {
      const position = this.current.position;
      this.index += 1;

      this.descend(position);
      const inner = this.parseDisjunction();
      this.depth -= 1;

      this.take(TokenKind.GroupClose, "')'");

      return inner;
    }

    return this.parseComparison();
  }

  private parseComparison(): Condition {
    const start = this.positionOfCurrentOrEnd;

    const field = this.parseField();
    const index = this.parseIndex(field);
    const op = this.parseOperator(field);

    // A quantifier is not a comparison, whatever its position looks like: what follows it is a condition about
    // one element, not a value to compare the field against
    if (isQuantifierOperator(op)) {
      return this.parseQuantified(field, index, op, start);
    }

    const required = valuesRequired(op);

    // 'X = null' is an accepted spelling of 'X IsNull'. Only an unquoted null counts, so a string field can
    // still be compared against the literal text 'null'.
    if (
      (op === Operator.Equals || op === Operator.NotEqual) &&
      this.check(TokenKind.Word) &&
      this.current.text.toLowerCase() === 'null'
    ) {
      this.index += 1;

      return comparison(op === Operator.Equals ? Operator.IsNull : Operator.IsNotNull, field, [], index);
    }

    const operands = this.parseOperands(field, op, required);

    // Everything read from text is text, so it is quoted when written back out
    const built: ComparisonCondition = {
      kind: 'comparison',
      operator: op as ComparisonOperator,
      field,
      values: operands,
      holdsText: true,
    };

    return (index === undefined) ? built : { ...built, index };
  }

  /**
   * The rest of a quantifier, once the collection and the operator have been read: a parenthesised condition
   * scoped to one element.
   *
   * ```
   * Assignments Any (LairID = 5 AND IsPrimary = true)
   * ```
   *
   * The parentheses are required rather than conventional. Without them the end of the inner condition would be
   * indistinguishable from the start of whatever follows the quantifier, and the two readings ask different
   * questions.
   */
  private parseQuantified(field: string, index: string | undefined, op: QuantifierOperator, start: number): Condition {
    const name = operationString(op);

    // "Assignments[0] Any (...)" names one element and then asks about all of them, which is neither reading
    if (index !== undefined) {
      throw this.describe(
        `'${field}[${index}]' is one element rather than a collection, so there is nothing for '${name}' to quantify over. Drop the index to ask about every element, or compare the element itself`,
        start,
      );
    }

    const open = this.positionOfCurrentOrEnd;

    if (!this.check(TokenKind.GroupOpen)) {
      throw this.describe(
        `Expected '(' after '${name}' for collection '${field}', which takes a condition about one element rather than a value, as "${field} ${name} (Name = 'x')"`,
        open,
      );
    }

    this.index += 1;

    this.descend(open);
    const inner = this.parseDisjunction();
    this.depth -= 1;

    this.take(TokenKind.GroupClose, "')'");

    return quantify(op, field, inner);
  }

  /** A field is a bare word (which may be a dotted property path), a quoted one, or a bracketed one. */
  private parseField(): string {
    if (this.match(TokenKind.BracketOpen)) {
      const bracketed = this.take(TokenKind.Word, 'a field name');
      this.take(TokenKind.BracketClose, "']'");

      return bracketed.text;
    }

    if (this.check(TokenKind.Text)) {
      return this.tokens[this.index++]!.text;
    }

    return this.take(TokenKind.Word, 'a field name').text;
  }

  /**
   * The index a field is tested at, where the brackets after it say so: `Tallies[apples]`, `[Items][0]`.
   *
   * Unambiguous in this position. A field has just been read and the only thing that may follow it is an
   * operator, so a bracket here can only open an index; the bracketed form that names a bound property is read
   * where a value is expected, which is the other side of the operator.
   *
   * @returns the index as text, or undefined where the field carries none
   */
  private parseIndex(field: string): string | undefined {
    if (!this.check(TokenKind.BracketOpen)) {
      return undefined;
    }

    const open = this.current.position;
    this.index += 1;

    // Quoted where the key needs it, bare where it does not, exactly as a value is written
    if (!(this.check(TokenKind.Word) || this.check(TokenKind.Text))) {
      throw this.describe(`Expected an index for field '${field}'`, open);
    }

    const index = this.tokens[this.index++]!.text;

    this.take(TokenKind.BracketClose, "']'");

    return index;
  }

  /**
   * The SQL operators written as more than one word, so they cannot be looked up by a single token.
   *
   * Unambiguous here: this is only called where an operator is expected and a field has already been read, so a
   * NOT in this position cannot be a negation and an IS cannot be anything else.
   */
  private tryParseSqlPhrase(field: string): Operator | null {
    // IS NULL, IS NOT NULL
    if (this.check(TokenKind.Word) && this.current.text.toUpperCase() === 'IS') {
      const isPosition = this.current.position;
      this.index += 1;

      const negated = this.match(TokenKind.Not);

      if (!(this.check(TokenKind.Word) && this.current.text.toUpperCase() === 'NULL')) {
        throw this.describe(
          `Expected NULL after IS${negated ? ' NOT' : ''} for field '${field}'`,
          isPosition,
        );
      }

      this.index += 1;
      const op = negated ? Operator.IsNotNull : Operator.IsNull;

      // Read all the way through before refusing, so the message names the whole phrase
      throw this.refuse(negated ? 'IS NOT NULL' : 'IS NULL', op, isPosition);
    }

    // NOT IN, NOT BETWEEN
    if (this.check(TokenKind.Not)) {
      const notPosition = this.current.position;
      this.index += 1;

      if (this.check(TokenKind.Word) && this.current.text.toUpperCase() === 'IN') {
        this.index += 1;

        throw this.refuse('NOT IN', Operator.IsNotIn, notPosition);
      }

      if (this.check(TokenKind.Word) && this.current.text.toUpperCase() === 'BETWEEN') {
        this.index += 1;

        throw this.refuse('NOT BETWEEN', Operator.IsNotBetween, notPosition);
      }

      throw this.describe(`Expected IN or BETWEEN after NOT for field '${field}'`, notPosition);
    }

    return null;
  }

  private parseOperator(field: string): Operator {
    if (this.atEnd) {
      throw this.describe(`Expected an operator for field '${field}'`, this.query.length);
    }

    const phrase = this.tryParseSqlPhrase(field);
    if (phrase !== null) {
      return phrase;
    }

    const token = this.current;
    if (token.kind !== TokenKind.Symbol && token.kind !== TokenKind.Word) {
      throw this.describe(`Expected an operator for field '${field}' but found '${token.text}'`, token.position);
    }

    const op = OPERATOR_LOOKUP.get(token.text.toLowerCase());
    if (op === undefined) {
      throw this.describe(`Unknown operator '${token.text}'`, token.position);
    }

    this.index += 1;
    return op;
  }

  private parseOperands(
    field: string,
    op: Operator,
    required: { minimum: number; maximum: number },
  ): ConditionValue[] {
    const values: ConditionValue[] = [];

    const read = (): void => {
      const operand = this.parseOperand(field);
      values.push(operand);
    };

    if (required.maximum === 0) {
      // IsNull / IsNotNull take no value at all
      return values;
    }

    const position = this.positionOfCurrentOrEnd;

    // A list is written in parentheses, ('a', 'b'), which is what the writer emits. In operand position a '('
    // can only start a list, and a '[' is always a property name.
    if (this.check(TokenKind.GroupOpen)) {
      this.index += 1;

      if (!this.check(TokenKind.GroupClose)) {
        read();
        while (this.match(TokenKind.Separator)) {
          read();
        }
      }

      this.take(TokenKind.GroupClose, "',' or ')'");
    } else if (op === Operator.IsBetween || op === Operator.IsNotBetween) {
      // SQL wrote a range as "BETWEEN low AND high" rather than as a list, and this does not take it: reading
      // one AND as a separator and the next as a conjunction is the sort of two-ways sentence it is rid of.
      throw this.describe(
        `A range written as 'low AND high' is not valid in the Native style, write '${operationString(op)} (low, high)' for field '${field}'`,
        position,
      );
    } else {
      read();
    }

    if (values.length < required.minimum) {
      throw this.describe(
        `Operator '${operationString(op)}' on field '${field}' needs at least ${required.minimum} value(s) but got ${values.length}`,
        position,
      );
    }

    if (values.length > required.maximum) {
      throw this.describe(
        `Operator '${operationString(op)}' on field '${field}' accepts at most ${required.maximum} value(s) but got ${values.length}`,
        position,
      );
    }

    return values;
  }

  /** One thing a field can be compared against: a literal, or a bracketed name that says it is a property. */
  private parseOperand(field: string): ConditionValue {
    if (!this.check(TokenKind.BracketOpen)) {
      return raw(this.parseLiteral(field));
    }

    const open = this.current.position;
    this.index += 1;

    // The brackets hold one bare name and nothing else. A list in them is what someone reaching for one is most
    // likely to write, so it gets its own message.
    if (this.check(TokenKind.Text)) {
      throw this.listInBrackets(field, open);
    }

    const name = this.take(TokenKind.Word, `the name of a bound property for field '${field}'`).text;

    if (this.check(TokenKind.Separator)) {
      throw this.listInBrackets(field, open);
    }

    this.take(TokenKind.BracketClose, "']'");

    // A second pair of brackets indexes the property being compared against: "Pay > [Tallies][apples]". Kept in
    // the operand's own text, an operand having nowhere else to put it, and taken apart again on lookup.
    const index = this.parseIndex(name);

    return binding((index === undefined) ? name : `${name}[${index}]`);
  }

  private listInBrackets(field: string, position: number): WeequeryError {
    return this.describe(`The list of values for '${field}' must be contained in parentheses, as (a, b)`, position);
  }

  private parseLiteral(field: string): string {
    if (this.atEnd) {
      throw this.describe(`Expected a value for field '${field}'`, this.query.length);
    }

    const token = this.current;
    if (token.kind !== TokenKind.Word && token.kind !== TokenKind.Text) {
      throw this.describe(`Expected a value for field '${field}' but found '${token.text}'`, token.position);
    }

    this.index += 1;
    return token.text;
  }
}

/**
 * Parse a query string into a condition tree.
 *
 * Reading is permissive by default, and has to stay that way: every spelling the language ever accepted is
 * sitting in somebody's saved filter. Pass `'native'` for the strict grammar, where each operator has one
 * spelling and every alternate more than one word long is refused by name.
 *
 * @returns null if the query is empty or whitespace
 */
export function parseQuery(query: string): Condition | null {
  const tokens = tokenize(query);
  if (tokens.length === 0) {
    return null;
  }

  const { condition, stopped } = parseLeading(tokens, query);

  // Anything left over means the query was not a single well-formed expression (eg. "(A) (B)")
  if (stopped < tokens.length) {
    throw describeError(query, `Unexpected '${tokens[stopped]!.text}'`, tokens[stopped]!.position);
  }

  return condition;
}

/**
 * Read the condition at the front of a token stream, stopping wherever it ends rather than insisting it is the
 * whole of the text. What a combined condition-and-sort string needs to find its split: where the condition
 * stops is where the sort clause begins, and letting the parser find it is exact where searching the text for
 * ORDER BY would not be.
 */
export function parseLeading(
  tokens: readonly Token[],
  query: string,
): { condition: Condition; stopped: number } {
  const parser = new Parser(tokens, query);

  const condition = parser.parseDisjunction();

  if (isTooDeep(condition)) {
    throw new WeequeryError(`Condition nests deeper than the limit of ${MAX_NESTING_DEPTH}: '${query}'`);
  }

  return { condition, stopped: parser.position };
}
