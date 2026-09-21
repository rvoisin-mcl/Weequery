/**
 * Weequery for TypeScript.
 *
 * Builds and checks the queries the C# library reads: a condition as a tree, or as a string in the query
 * language, and the two convert to each other. Nothing here talks to a server; it produces the text and the JSON
 * a server is sent, and tells you what is wrong with them first.
 *
 * The allow-list still lives on the server. Give this the same binding list and it will predict what the server
 * says, which turns a round trip into a message beside the input box. It does not grant anything.
 */

export { WeequeryError, excerpt } from './errors.js';

export {
  ConditionShape,
  allows,
  BindingUse,
  MAX_NESTING_DEPTH,
  MAX_VALUES_IN_LIST,
  Operator,
  OPERATOR_NAMES,
  SortDirection,
  ValueSource,
  isContainerOperator,
  isQuantifierOperator,
  operationString,
  shapeForOperation,
  useName,
  valuesRequired,
} from './operators.js';
export type { ComparisonOperator, ContainerOperator, QuantifierOperator } from './operators.js';

export {
  all,
  and,
  any,
  at,
  binding,
  children,
  comparison,
  contains,
  doesNotContain,
  doesNotEndWith,
  doesNotMatch,
  doesNotStartWith,
  endsWith,
  eq,
  fieldsUsed,
  gt,
  gte,
  isBetween,
  isIn,
  isMatch,
  isNotBetween,
  isNotIn,
  isNotNull,
  isNull,
  isTooDeep,
  lt,
  lte,
  ne,
  nestingDepth,
  none,
  not,
  or,
  quantify,
  raw,
  shapeOf,
  startsWith,
  toInvariantString,
  validateValueCount,
  walk,
} from './condition.js';
export type {
  ComparisonCondition,
  Condition,
  ConditionList,
  ConditionValue,
  ConjunctionCondition,
  NotCondition,
  Operand,
  QuantifiedCondition,
  Value,
} from './condition.js';

export {
  isEverything,
  isUnder,
  NO_PROJECTION,
  parseProjection,
  projectedKeys,
  projection,
  PROJECTION_WILDCARD,
  projectionToQuery,
  SELECT_PREFIX,
  wildcardPrefix,
} from './projection.js';
export type { Projection } from './projection.js';

export {
  indexText,
  isBareWord,
  isBindingKey,
  isKeyword,
  isQualifiedSqlName,
  isReserved,
  isSqlName,
  quote,
  splitIndex,
} from './syntax.js';

export { parseQuery } from './parser.js';
export { describe, toQuery } from './writer.js';

export { pack, unpack } from './packed.js';
export type {
  PackedCasing,
  PackedCondition,
  PackedConditionCamel,
  PackedValue,
  TransportCondition,
} from './packed.js';

export { asc, desc, parseSorts, SORT_SEPARATOR, sortsToQuery } from './sort.js';
export type { Sort } from './sort.js';

export { parseParsedQuery, parsedQueryToQuery } from './parsedQuery.js';
export type { ParsedQuery } from './parsedQuery.js';

export {
  BindingSet,
  droppedFields,
  validateCondition,
  validatePacked,
  validateParsedQuery,
  validateProjection,
  validateQuery,
  validateSorts,
} from './validate.js';
export type {
  Binding,
  BindingType,
  Diagnostic,
  DiagnosticCode,
  ValidateOptions,
  ValidationResult,
} from './validate.js';
