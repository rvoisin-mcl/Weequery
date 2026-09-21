import { Condition, QuantifiedCondition, isTooDeep, nestingDepth } from './condition.js';
import { WeequeryError } from './errors.js';
import {
  BOOLEAN_OPERATORS,
  allows,
  BindingUse,
  MAX_NESTING_DEPTH,
  Operator,
  operationString,
  OPERATOR_NAMES,
  shapeForOperation,
  STRING_ONLY_OPERATORS,
  useName,
  ValueSource,
  valuesRequired,
} from './operators.js';
import { unpack } from './packed.js';
import { parseQuery } from './parser.js';
import { parseParsedQuery, ParsedQuery } from './parsedQuery.js';
import {
  isEverything,
  isUnder,
  parseProjection,
  Projection,
  projection,
  wildcardPrefix,
} from './projection.js';
import { bindingKeyProblem, splitIndex } from './syntax.js';
import { Sort } from './sort.js';

/**
 * What a bound property holds, where you know. Optional throughout: without it the field is checked for being
 * bound at all, which is the part that matters, and the operator rules that depend on the type are skipped.
 */
export type BindingType = 'string' | 'number' | 'boolean' | 'date' | 'time' | 'guid' | 'enum' | 'other';

/**
 * One entry of the allow-list, as the server declares it.
 *
 * Mirror the server's binding list here and a caller's mistakes are caught before the round trip. Nothing here
 * grants anything: the server's list is the one that decides, and this only predicts what it will say.
 */
export interface Binding {
  /** The name the caller uses, matched without regard to case. */
  readonly key: string;
  /** What the property holds, where you know it. */
  readonly type?: BindingType;
  /** Whether a null is possible, which is what the null tests need. Unknown by default, so they are allowed. */
  readonly nullable?: boolean;
  /** False for a collection or a navigation property, which can be tested for null but has no ordering. */
  readonly sortable?: boolean;
  /** True for a value your code supplies under a name, which is the same for every row and so cannot be sorted on. */
  readonly constant?: boolean;
  /**
   * What may be asked about one element of this collection, where the server bound it with `BindCollection`.
   *
   * Its presence is what makes this a collection rather than a property: a key with `elements` answers `Any`,
   * `All` and `None` and nothing else, and a key without them answers everything else and no quantifier. The
   * inner list is its own allow-list, exactly as it is on the server, so the two do not leak into each other.
   *
   * ```ts
   * { key: 'Assignments', elements: ['LairID', { key: 'LairName', type: 'string' }] }
   * ```
   */
  readonly elements?: readonly (Binding | string)[];
  /**
   * What the server's binding may be used for, see {@link BindingUse}. All three where it is left off, which is
   * what the server defaults to.
   *
   * Mirror the server's `BindingUse` here and a caller is told before the round trip rather than after: a
   * condition, an operand or a sort naming a key that does not grant it comes back as `projection-only`.
   *
   * ```ts
   * { key: 'Notes', type: 'string', use: BindingUse.Projection }
   * ```
   */
  readonly use?: BindingUse;
}

/** The allow-list, keyed without regard to case, as the server keys it. */
export class BindingSet {
  private readonly bindings = new Map<string, Binding>();

  constructor(bindings: readonly (Binding | string)[] = []) {
    for (const entry of bindings) {
      this.add(typeof entry === 'string' ? { key: entry } : entry);
    }
  }

  add(binding: Binding): this {
    const problem = bindingKeyProblem(binding.key);
    if (problem !== null) {
      throw new WeequeryError(problem);
    }

    const lowered = binding.key.toLowerCase();
    if (this.bindings.has(lowered)) {
      throw new WeequeryError(`Binding already exists for '${binding.key}'`);
    }

    this.bindings.set(lowered, binding);
    return this;
  }

  get(key: string): Binding | undefined {
    return this.bindings.get(key.toLowerCase());
  }

  has(key: string): boolean {
    return this.bindings.has(key.toLowerCase());
  }

  get keys(): string[] {
    return [...this.bindings.values()].map((binding) => binding.key);
  }
}

/** One thing wrong, or one thing worth saying. */
export interface Diagnostic {
  readonly severity: 'error' | 'warning';
  /** A short machine-readable code, for a caller mapping these onto its own messages. */
  readonly code: DiagnosticCode;
  readonly message: string;
  /** Index into the query text, where the problem came from text and has a place in it. */
  readonly position?: number;
  /** The field the problem is about, where there is one. */
  readonly field?: string;
}

export type DiagnosticCode =
  | 'syntax'
  | 'unbound-field'
  | 'unbound-operand'
  | 'operator-shape'
  | 'value-count'
  | 'list-too-long'
  | 'nesting'
  | 'type-mismatch'
  | 'not-sortable'
  | 'sort-on-constant'
  | 'empty-conjunction'
  | 'paging-without-sort'
  /** A quantifier named a key that is bound as an ordinary property, which has no elements to quantify over. */
  | 'not-a-collection'
  /** A comparison named a key that is bound as a collection, which the comparison operators cannot be asked. */
  | 'collection-not-comparable'
  /** A projection named a key that is bound as a collection, which has no single value to read. */
  | 'not-projectable'
  /** A condition, an operand or a sort named a key that is bound for projection only. */
  | 'projection-only';

export interface ValidationResult {
  readonly ok: boolean;
  readonly diagnostics: readonly Diagnostic[];
  /** The condition, where one parsed. Null for an empty query, absent where parsing failed. */
  readonly condition?: Condition | null;
  readonly sorts?: readonly Sort[];
  /** The fields a combined string asked to read back, where one parsed. */
  readonly projection?: Projection;
}

export interface ValidateOptions {
  /** The allow-list to check field names against. Without one, only the structure is checked. */
  readonly bindings?: BindingSet | readonly (Binding | string)[];
  /**
   * True where the server was told to drop unbound fields rather than refuse the query, matching its
   * `InquirySettings.IgnoreUnboundFields`.
   *
   * Every `unbound-field` and `unbound-operand` comes back as a **warning** instead of an error, so the result
   * is `ok` and a form can say "this part of your saved filter no longer applies" rather than refusing to
   * submit. Mirror whatever the server is set to, or the two will disagree about whether the query is usable.
   *
   * Nothing else softens: a narrowed binding, a type mismatch and malformed text are errors either way, exactly
   * as they are on the server.
   */
  readonly ignoreUnboundFields?: boolean;
  /** True where the query will be paged, which makes a missing sort worth a warning. */
  readonly paged?: boolean;
}

function toBindingSet(bindings: ValidateOptions['bindings']): BindingSet | null {
  if (bindings === undefined) {
    return null;
  }

  return bindings instanceof BindingSet ? bindings : new BindingSet(bindings);
}

function error(code: DiagnosticCode, message: string, field?: string, position?: number): Diagnostic {
  return { severity: 'error', code, message, field, position };
}

function warning(code: DiagnosticCode, message: string, field?: string): Diagnostic {
  return { severity: 'warning', code, message, field };
}

/**
 * The fields a lenient server is going to drop, read off diagnostics it already produced.
 *
 * The counterpart to the server's `DroppedFields`, and the same answer arrived at from the other end: there,
 * the query has been built and this is what came out of it; here, nothing has been sent yet and this is what
 * will. Empty unless {@link ValidateOptions.ignoreUnboundFields} was set, since only then are these warnings
 * rather than errors.
 *
 * ```ts
 * const result = validateQuery(saved, { bindings, ignoreUnboundFields: true });
 *
 * for (const field of droppedFields(result.diagnostics)) {
 *   warn(`'${field}' no longer applies and has been removed from your saved view`);
 * }
 * ```
 *
 * @returns each name once, in the order it was met
 */
export function droppedFields(diagnostics: readonly Diagnostic[]): string[] {
  const seen = new Map<string, string>();

  for (const diagnostic of diagnostics) {
    const unboundField = (diagnostic.code === 'unbound-field') || (diagnostic.code === 'unbound-operand');

    if ((diagnostic.severity !== 'warning') || (!unboundField) || (diagnostic.field === undefined)) {
      continue;
    }

    const key = splitIndex(diagnostic.field).key;

    if (!seen.has(key.toLowerCase())) {
      seen.set(key.toLowerCase(), diagnostic.field);
    }
  }

  return [...seen.values()];
}

/**
 * A field nothing bound, reported as whichever the server is going to treat it as.
 *
 * An error normally, and a warning where the server was told to drop these rather than refuse, see
 * {@link ValidateOptions.ignoreUnboundFields}. Only genuine absence goes through here; everything else stays an
 * error however lenient the server is.
 */
function unbound(options: ValidateOptions, code: DiagnosticCode, message: string, field?: string): Diagnostic {
  return options.ignoreUnboundFields === true
    ? warning(code, `${message}, so it will be dropped from the query`, field)
    : error(code, message, field);
}

/**
 * Check a condition tree: its own structure always, and its field names against the allow-list where one is
 * given.
 *
 * Collects everything rather than stopping at the first problem, which is what a form wants. The builders refuse
 * most of this at construction, so a tree that came from them is already sound; this is for one that arrived as
 * data, or one assembled by hand.
 */
export function validateCondition(condition: Condition, options: ValidateOptions = {}): Diagnostic[] {
  const diagnostics: Diagnostic[] = [];
  const bindings = toBindingSet(options.bindings);

  if (isTooDeep(condition)) {
    diagnostics.push(
      error('nesting', `Condition nests ${nestingDepth(condition)} levels deep, past the limit of ${MAX_NESTING_DEPTH}`),
    );

    // No point walking a tree that is already refused, and walking it is what would be expensive
    return diagnostics;
  }

  // Recursive rather than a flat walk, because a quantifier changes which allow-list is in scope: the fields
  // inside it resolve against the collection's own list and not the entity's, see Binding.elements
  const visit = (node: Condition, scope: BindingSet | null): void => {
    if (node.kind === 'conjunction') {
      if (node.conditions.length === 0) {
        diagnostics.push(
          warning(
            'empty-conjunction',
            `An empty ${operationString(node.operator, 'native')} matches ${node.operator === Operator.And ? 'everything' : 'nothing'}, and cannot be written as a query string`,
          ),
        );
      }

      for (const child of node.conditions) {
        visit(child, scope);
      }
      return;
    }

    if (node.kind === 'not') {
      visit(node.condition, scope);
      return;
    }

    if (node.kind === 'quantified') {
      visit(node.condition, quantifierScope(diagnostics, node, scope, options));
      return;
    }

    const { operator, field, values } = node;
    const name = OPERATOR_NAMES[operator] ?? String(operator);

    if (shapeForOperation(operator) === null) {
      diagnostics.push(error('operator-shape', `Operator '${name}' combines conditions, so it is not a comparison`, field));
      return;
    }

    const required = valuesRequired(operator);
    if (values.length < required.minimum) {
      diagnostics.push(
        error('value-count', `Operator '${name}' on field '${field}' needs at least ${required.minimum} value(s) but got ${values.length}`, field),
      );
    } else if (values.length > required.maximum) {
      const code = operator === Operator.IsIn || operator === Operator.IsNotIn ? 'list-too-long' : 'value-count';
      diagnostics.push(
        error(code, `Operator '${name}' on field '${field}' accepts at most ${required.maximum} value(s) but got ${values.length}`, field),
      );
    }

    if (scope === null) {
      return;
    }

    const bound = scope.get(field);
    if (bound === undefined) {
      diagnostics.push(unbound(options, 'unbound-field', `Unbound field: '${field}'`, field));
    } else if (!allows(bound.use, BindingUse.Condition)) {
      // Bound, so "unbound" would send the caller looking for a typo in a name that works perfectly well
      // elsewhere. Say what it is actually for.
      diagnostics.push(
        error(
          'projection-only',
          `'${field}' cannot be used in a condition: it is bound for ${useName(bound.use)}`,
          field,
        ),
      );
    } else if (bound.elements !== undefined) {
      // A collection is not something the comparison operators can be asked. Asking about its elements is what
      // the quantifiers are for.
      diagnostics.push(
        error(
          'collection-not-comparable',
          `'${field}' is a collection, so '${name}' cannot be asked of it. Use Any, All or None with a condition about one element`,
          field,
        ),
      );
    } else if (node.index !== undefined) {
      // The type recorded here is the collection's, and an indexed condition is about one element of it. Guessing
      // the element type from "the binding is a list of something" is not something a BindingSet can do, so the
      // type rules are skipped rather than answered wrongly: Tallies[apples] StartsWith 'x' is a fair question
      // even where Tallies itself is no sort of string.
    } else {
      checkOperatorAgainstType(diagnostics, operator, name, field, bound);
    }

    for (const operand of values) {
      if (operand.source !== ValueSource.Binding) {
        continue;
      }

      // An operand may carry an index, "Tallies[apples]"; what has to be bound is the key
      const named = scope.get(splitIndex(operand.value).key);

      if (named === undefined) {
        diagnostics.push(
          // Reported against the operand rather than the comparison: the comparison's own field is bound and
          // fine, and the operand is the name the caller has to do something about
          unbound(options, 'unbound-operand', `'${operand.value}', compared against on field '${field}', is not bound`, operand.value),
        );
      } else if (!allows(named.use, BindingUse.Condition)) {
        // Comparing against a column is a read of it, so this is the back door onto a binding that does not
        // grant Condition: the value would be learnable by bisection
        diagnostics.push(
          error(
            'projection-only',
            `'${operand.value}', compared against on field '${field}', cannot be used in a condition: it is bound for ${useName(named.use)}`,
            field,
          ),
        );
      }
    }
  };

  visit(condition, bindings);

  return diagnostics;
}

/**
 * Check a projection: that it reads, and that every field it names is one the server will project.
 *
 * ```ts
 * validateProjection('Name, Pay', { bindings });
 * validateProjection(['Name', 'Pay'], { bindings });
 * ```
 *
 * The allow-list is the same one a condition is checked against, so nothing extra needs declaring. A collection
 * is the one bound thing that cannot be projected: it holds many values and a column holds one.
 *
 * Malformed text comes back as a `syntax` diagnostic rather than as a throw, so a form can show it beside the
 * input box like everything else here.
 *
 * @param fields the list as text, or the keys already in hand
 * @returns every problem found, and empty where there are none
 */
export function validateProjection(
  fields: string | readonly string[] | null | undefined,
  options: ValidateOptions = {},
): Diagnostic[] {
  const diagnostics: Diagnostic[] = [];

  let named: readonly string[];

  try {
    named = typeof fields === 'string' ? parseProjection(fields) : projection(fields);
  } catch (caught) {
    const problem = caught as WeequeryError;

    return [error('syntax', problem.message, undefined, problem.position)];
  }

  const bindings = toBindingSet(options.bindings);
  if (bindings === null) {
    return diagnostics;
  }

  /** Every key the allow-list would let this caller read, which is what a wildcard stands for */
  const projectable = (): string[] =>
    bindings.keys.filter((key) => {
      const bound = bindings.get(key)!;

      return allows(bound.use, BindingUse.Projection) && bound.elements === undefined;
    });

  for (const field of named) {
    // A wildcard names the allow-list rather than a field, so there is no key to look up. The server expands it
    // when it builds, against the same bindings, which is why nothing is expanded here.
    if (isEverything(field)) {
      continue;
    }

    const prefix = wildcardPrefix(field);

    if (prefix !== null) {
      // A prefix that stands for nothing is the same mistake as naming a field nobody bound, and is reported
      // as one: the caller asked for a branch of the allow-list that is not there
      if (!projectable().some((key) => isUnder(key, prefix))) {
        diagnostics.push(
          unbound(
            options,
            'unbound-field',
            `'${field}' matches nothing: no binding under '${prefix}' can be projected`,
            field,
          ),
        );
      }

      continue;
    }

    // A field may carry an index, "Tallies[apples]"; what has to be bound is the key
    const key = splitIndex(field).key;
    const bound = bindings.get(key);

    if (bound === undefined) {
      diagnostics.push(unbound(options, 'unbound-field', `Unbound field: '${key}'`, field));
    } else if (!allows(bound.use, BindingUse.Projection)) {
      diagnostics.push(
        error(
          'not-projectable',
          `'${key}' cannot be projected: it is bound for ${useName(bound.use)}`,
          field,
        ),
      );
    } else if (bound.elements !== undefined) {
      diagnostics.push(
        error(
          'not-projectable',
          `'${key}' is a collection, so it cannot be projected: it has no single value to read. Project a field of the entity, or ask about its elements with a quantifier`,
          field,
        ),
      );
    }
  }

  return diagnostics;
}

/**
 * The allow-list that applies inside a quantifier, and the diagnostics for a field that cannot carry one.
 *
 * Null where there is nothing to check against, which is either no allow-list at all or a field that failed:
 * the inner condition is still walked for its own structure, and reporting every field inside as unbound on top
 * of the one real problem would only bury it.
 */
function quantifierScope(
  diagnostics: Diagnostic[],
  node: QuantifiedCondition,
  scope: BindingSet | null,
  options: ValidateOptions,
): BindingSet | null {
  if (scope === null) {
    return null;
  }

  const name = OPERATOR_NAMES[node.operator] ?? String(node.operator);

  const bound = scope.get(node.field);
  if (bound === undefined) {
    diagnostics.push(unbound(options, 'unbound-field', `Unbound collection: '${node.field}'`, node.field));
    return null;
  }

  if (bound.elements === undefined) {
    diagnostics.push(
      error(
        'not-a-collection',
        `'${node.field}' is bound as a property rather than a collection, so there is nothing for '${name}' to quantify over`,
        node.field,
      ),
    );
    return null;
  }

  return new BindingSet(bound.elements);
}

function checkOperatorAgainstType(
  diagnostics: Diagnostic[],
  operator: Operator,
  name: string,
  field: string,
  bound: Binding,
): void {
  if (bound.type === undefined) {
    return;
  }

  if (STRING_ONLY_OPERATORS.has(operator) && bound.type !== 'string') {
    diagnostics.push(error('type-mismatch', `Operator '${name}' is for strings, and '${field}' is a ${bound.type}`, field));
  }

  if (bound.type === 'boolean' && !BOOLEAN_OPERATORS.has(operator)) {
    diagnostics.push(
      error('type-mismatch', `A boolean has no ordering, so operator '${name}' cannot be used on '${field}'`, field),
    );
  }

  if ((operator === Operator.IsNull || operator === Operator.IsNotNull) && bound.nullable === false) {
    diagnostics.push(error('type-mismatch', `'${field}' cannot be null, so '${name}' will never be true of it`, field));
  }
}

/** Check a sort list against the allow-list: bound, not a constant, and something with an ordering of its own. */
export function validateSorts(sorts: readonly Sort[], options: ValidateOptions = {}): Diagnostic[] {
  const diagnostics: Diagnostic[] = [];
  const bindings = toBindingSet(options.bindings);

  if (bindings !== null) {
    for (const sort of sorts) {
      // A sort may carry an index the same way an operand does
      const bound = bindings.get(splitIndex(sort.field).key);

      if (bound === undefined) {
        diagnostics.push(unbound(options, 'unbound-field', `Unbound field: '${sort.field}'`, sort.field));
        continue;
      }

      if (!allows(bound.use, BindingUse.Sort)) {
        diagnostics.push(
          error(
            'projection-only',
            `Cannot sort on '${sort.field}': it is bound for ${useName(bound.use)}`,
            sort.field,
          ),
        );
        continue;
      }

      if (bound.constant === true) {
        diagnostics.push(
          error('sort-on-constant', `Cannot sort on '${sort.field}', it is a constant and so the same for every row`, sort.field),
        );
      }

      if (bound.sortable === false) {
        diagnostics.push(error('not-sortable', `Cannot sort on '${sort.field}', it has no ordering of its own`, sort.field));
      }
    }
  }

  if (options.paged === true && sorts.length === 0) {
    diagnostics.push(
      warning(
        'paging-without-sort',
        'A page of an unordered query holds arbitrary rows, so page two may repeat a row from page one. Sort on something that breaks every tie',
      ),
    );
  }

  return diagnostics;
}

/**
 * Parse and check a query string in one go, returning what went wrong rather than throwing.
 *
 * A syntax error comes back as a diagnostic carrying its position, so an editor can point at it; everything
 * after that is a semantic check against the allow-list, and those are collected rather than stopping at the
 * first.
 */
export function validateQuery(query: string, options: ValidateOptions = {}): ValidationResult {
  let condition: Condition | null;

  try {
    condition = parseQuery(query);
  } catch (caught) {
    return { ok: false, diagnostics: [fromThrown(caught)] };
  }

  if (condition === null) {
    return { ok: true, diagnostics: [], condition: null };
  }

  const diagnostics = validateCondition(condition, options);

  return { ok: !diagnostics.some((entry) => entry.severity === 'error'), diagnostics, condition };
}

/**
 * The same for a combined string, which carries all three parts: the condition, the sorts and the projection.
 *
 * A fault in the text is one diagnostic, because one string is one thing to read and the separators are what
 * say which part a word belongs to. What the parts *name* is checked part by part, so a caller is still told
 * which box to look at.
 */
export function validateParsedQuery(query: string, options: ValidateOptions = {}): ValidationResult {
  let parsed: ParsedQuery;

  try {
    parsed = parseParsedQuery(query, null);
  } catch (caught) {
    return { ok: false, diagnostics: [fromThrown(caught)] };
  }

  const diagnostics: Diagnostic[] = [
    ...(parsed.condition === null ? [] : validateCondition(parsed.condition, options)),
    ...validateSorts(parsed.sorts, options),
    ...validateProjection(parsed.projection, options),
  ];

  return {
    ok: !diagnostics.some((entry) => entry.severity === 'error'),
    diagnostics,
    condition: parsed.condition,
    sorts: parsed.sorts,
    projection: parsed.projection,
  };
}

/** Check a packed payload that arrived as data, without trusting it to be a condition at all. */
export function validatePacked(packed: unknown, options: ValidateOptions = {}): ValidationResult {
  let condition: Condition;

  try {
    condition = unpack(packed);
  } catch (caught) {
    return { ok: false, diagnostics: [fromThrown(caught)] };
  }

  const diagnostics = validateCondition(condition, options);

  return { ok: !diagnostics.some((entry) => entry.severity === 'error'), diagnostics, condition };
}

function fromThrown(caught: unknown): Diagnostic {
  if (caught instanceof WeequeryError) {
    return { severity: 'error', code: 'syntax', message: caught.message, position: caught.position };
  }

  return { severity: 'error', code: 'syntax', message: caught instanceof Error ? caught.message : String(caught) };
}
