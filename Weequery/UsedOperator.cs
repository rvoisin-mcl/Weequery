namespace Weequery;

/// <summary>
/// One operator as a condition was found to use it, and where, see
/// <see cref="ConditionFunctions.OperatorsUsed"/>
/// </summary>
/// <remarks>
/// The field is what lets a refusal name the input that caused it rather than only the operator, which is the
/// difference between a message a caller can act on and one they cannot. It is null for the operators that name
/// no field, the conjunctions and the negation.
/// </remarks>
/// <param name="Operator">the operator used</param>
/// <param name="Field">the field it was used on, or null where it was used on none</param>
internal readonly record struct UsedOperator(Operator Operator, string? Field);
