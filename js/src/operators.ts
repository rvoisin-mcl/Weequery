import { WeequeryError } from './errors.js';

/**
 * What a condition does.
 *
 * The numbers are the wire format. They are the declaration order of the C# `Operator` enum and a packed
 * condition carries them as integers, so they are not free to be reordered on either side. `IsIn` is 10 there
 * and 10 here, and that is load bearing.
 */
export enum Operator {
  IsNull = 0,
  IsNotNull = 1,
  Equals = 2,
  NotEqual = 3,
  LessThan = 4,
  LessThanOrEqual = 5,
  GreaterThan = 6,
  GreaterThanOrEqual = 7,
  IsBetween = 8,
  IsNotBetween = 9,
  IsIn = 10,
  IsNotIn = 11,
  StartsWith = 12,
  DoesNotStartWith = 13,
  EndsWith = 14,
  DoesNotEndWith = 15,
  Contains = 16,
  DoesNotContain = 17,
  Or = 18,
  And = 19,
  Not = 20,
  IsMatch = 21,
  DoesNotMatch = 22,

  /** At least one element of a bound collection satisfies the condition inside it. False of an empty one. */
  Any = 23,
  /** Every element does. True of an empty collection, and of one that is not there at all. */
  All = 24,
  /** No element does, which is the negation of {@link Operator.Any}. True of an empty collection. */
  None = 25,
}

/** Where one of a condition's operands comes from. Also a wire format: Raw is 0, Binding is 1. */
export enum ValueSource {
  /** Text the caller supplied, read against the bound property's type when the query is built. */
  Raw = 0,
  /** The key of another bound property, whose value is what the comparison is against. */
  Binding = 1,
}

/** How many operands a comparison holds. */
export enum ConditionShape {
  NoValue = 'NoValue',
  OneValue = 'OneValue',
  TwoValue = 'TwoValue',
  MultipleValue = 'MultipleValue',
}

/** Levels of nesting a condition may have. Every conjunction and every negation is one level. */
export const MAX_NESTING_DEPTH = 16;

/** How many values the IsIn family will take. The list becomes parameters, and a provider will only take so many. */
export const MAX_VALUES_IN_LIST = 1000;

/** The operators that combine conditions rather than testing a property. */
export type ContainerOperator = Operator.And | Operator.Or | Operator.Not;

/**
 * The operators that quantify over a bound collection.
 *
 * Neither of the other two things: they name a field as a comparison does and hold a condition as a container
 * does, so they are their own kind rather than an awkward member of either.
 */
export type QuantifierOperator = Operator.Any | Operator.All | Operator.None;

/** The operators that test a bound property. */
export type ComparisonOperator = Exclude<Operator, ContainerOperator | QuantifierOperator>;

const CONTAINERS: ReadonlySet<Operator> = new Set([Operator.And, Operator.Or, Operator.Not]);

const QUANTIFIERS: ReadonlySet<Operator> = new Set([Operator.Any, Operator.All, Operator.None]);

export function isContainerOperator(op: Operator): op is ContainerOperator {
  return CONTAINERS.has(op);
}

/** Whether an operator quantifies over a collection rather than testing a property or combining conditions. */
export function isQuantifierOperator(op: Operator): op is QuantifierOperator {
  return QUANTIFIERS.has(op);
}

/** The canonical name of every operator, which is what the reserved word list is built from. */
export const OPERATOR_NAMES: Readonly<Record<Operator, string>> = {
  [Operator.IsNull]: 'IsNull',
  [Operator.IsNotNull]: 'IsNotNull',
  [Operator.Equals]: 'Equals',
  [Operator.NotEqual]: 'NotEqual',
  [Operator.LessThan]: 'LessThan',
  [Operator.LessThanOrEqual]: 'LessThanOrEqual',
  [Operator.GreaterThan]: 'GreaterThan',
  [Operator.GreaterThanOrEqual]: 'GreaterThanOrEqual',
  [Operator.IsBetween]: 'IsBetween',
  [Operator.IsNotBetween]: 'IsNotBetween',
  [Operator.IsIn]: 'IsIn',
  [Operator.IsNotIn]: 'IsNotIn',
  [Operator.StartsWith]: 'StartsWith',
  [Operator.DoesNotStartWith]: 'DoesNotStartWith',
  [Operator.EndsWith]: 'EndsWith',
  [Operator.DoesNotEndWith]: 'DoesNotEndWith',
  [Operator.Contains]: 'Contains',
  [Operator.DoesNotContain]: 'DoesNotContain',
  [Operator.Or]: 'Or',
  [Operator.And]: 'And',
  [Operator.Not]: 'Not',
  [Operator.IsMatch]: 'IsMatch',
  [Operator.DoesNotMatch]: 'DoesNotMatch',
  [Operator.Any]: 'Any',
  [Operator.All]: 'All',
  [Operator.None]: 'None',
};

/**
 * The one spelling an operator is written as.
 *
 * One form per operator and no alternates, which is what lets two conditions that mean the same thing be
 * compared as text. The parser reads a handful of one word alternates, `IN` and `BETWEEN` and `==` and `!=`,
 * and what comes back out is always the form here.
 */
export function operationString(op: Operator): string {
  switch (op) {
    case Operator.Equals:
      return '=';
    case Operator.NotEqual:
      return '<>';
    case Operator.LessThan:
      return '<';
    case Operator.LessThanOrEqual:
      return '<=';
    case Operator.GreaterThan:
      return '>';
    case Operator.GreaterThanOrEqual:
      return '>=';
    case Operator.And:
      return 'AND';
    case Operator.Or:
      return 'OR';
    case Operator.Not:
      return 'NOT';
    default: {
      const name = OPERATOR_NAMES[op];
      if (name === undefined) {
        throw new WeequeryError(`Operator ${String(op)} is invalid`);
      }
      return name;
    }
  }
}

/** Which of the four comparison shapes an operator belongs to, or null for the three that combine conditions. */
export function shapeForOperation(op: Operator): ConditionShape | null {
  switch (op) {
    case Operator.IsNull:
    case Operator.IsNotNull:
      return ConditionShape.NoValue;

    case Operator.Equals:
    case Operator.NotEqual:
    case Operator.LessThan:
    case Operator.LessThanOrEqual:
    case Operator.GreaterThan:
    case Operator.GreaterThanOrEqual:
    case Operator.StartsWith:
    case Operator.DoesNotStartWith:
    case Operator.EndsWith:
    case Operator.DoesNotEndWith:
    case Operator.Contains:
    case Operator.DoesNotContain:
    case Operator.IsMatch:
    case Operator.DoesNotMatch:
      return ConditionShape.OneValue;

    case Operator.IsBetween:
    case Operator.IsNotBetween:
      return ConditionShape.TwoValue;

    case Operator.IsIn:
    case Operator.IsNotIn:
      return ConditionShape.MultipleValue;

    case Operator.Or:
    case Operator.And:
    case Operator.Not:

    // A quantifier holds a condition rather than operands, so it has no comparison shape either
    case Operator.Any:
    case Operator.All:
    case Operator.None:
      return null;

    default:
      throw new WeequeryError(`Operator ${String(op)} is invalid`);
  }
}

/** The values an operator takes, as a range. */
export function valuesRequired(op: Operator): { minimum: number; maximum: number } {
  switch (shapeForOperation(op)) {
    case ConditionShape.NoValue:
      return { minimum: 0, maximum: 0 };
    case ConditionShape.OneValue:
      return { minimum: 1, maximum: 1 };
    case ConditionShape.TwoValue:
      return { minimum: 2, maximum: 2 };
    case ConditionShape.MultipleValue:
      return { minimum: 0, maximum: MAX_VALUES_IN_LIST };
    default:
      return { minimum: 0, maximum: 0 };
  }
}

/** The six substring operators and the two regex ones, which only apply to strings. */
export const STRING_ONLY_OPERATORS: ReadonlySet<Operator> = new Set([
  Operator.StartsWith,
  Operator.DoesNotStartWith,
  Operator.EndsWith,
  Operator.DoesNotEndWith,
  Operator.Contains,
  Operator.DoesNotContain,
  Operator.IsMatch,
  Operator.DoesNotMatch,
]);

/**
 * What a bool accepts: the null tests, equality, and the IsIn family. It has no ordering, so the comparisons are
 * refused up front rather than left to fail against the provider.
 */
export const BOOLEAN_OPERATORS: ReadonlySet<Operator> = new Set([
  Operator.IsNull,
  Operator.IsNotNull,
  Operator.Equals,
  Operator.NotEqual,
  Operator.IsIn,
  Operator.IsNotIn,
]);

/**
 * What a binding may be used for, mirroring the C# `BindingUse`. A bitmask: combine with `|`.
 *
 * A binding grants three separable things, and they are not always wanted together. Filtering lets a caller ask
 * a question of every row; sorting lets them order by it; projecting lets them read it back. A column can be
 * worth returning without being worth interrogating, and one can be worth filtering on without being worth
 * showing.
 *
 * ```ts
 * { key: 'Name' }                                                  // all three, the default
 * { key: 'Notes', use: BindingUse.Projection }                     // read it back, and nothing else
 * { key: 'Tenant', use: BindingUse.Condition }                     // filter on it, never show it
 * { key: 'Morale', use: BindingUse.Condition | BindingUse.Sort }
 * ```
 */
export enum BindingUse {
  None = 0,
  /**
   * May be named in a condition: on the left of an operator, and as an operand another field is compared
   * against. The two go together because comparing against a field is a read of it.
   */
  Condition = 1,
  /** May be sorted on. */
  Sort = 2,
  /** May be read back by a projection. */
  Projection = 4,
  /** All three, which is what a binding grants unless it says otherwise. */
  All = 7,
}

/** Whether a use is granted, for a binding that may not have said. Nothing said means all three. */
export function allows(use: BindingUse | undefined, wanted: BindingUse): boolean {
  return ((use ?? BindingUse.All) & wanted) === wanted;
}

/** The name of a use combination, for a message that has to say what a binding is actually for. */
export function useName(use: BindingUse | undefined): string {
  const granted = use ?? BindingUse.All;

  if (granted === BindingUse.All) {
    return 'All';
  }

  const names = (['Condition', 'Sort', 'Projection'] as const).filter(
    (name) => (granted & BindingUse[name]) !== 0,
  );

  return names.length === 0 ? 'None' : names.join(', ');
}

/** Which way a sort runs. Also a wire format: Ascending is 0, Descending is 1. */
export enum SortDirection {
  Ascending = 0,
  Descending = 1,
}
