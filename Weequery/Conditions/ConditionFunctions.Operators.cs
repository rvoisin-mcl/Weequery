
namespace Weequery;

// What an operator requires of the condition that names it: how many values it takes, what shape that makes
// the condition, and the refusal when a condition arrives carrying the wrong number of them.
public static partial class ConditionFunctions
{
    /// <summary>
    /// How many values the IsIn family will take.
    /// <para>
    /// The list becomes parameters, and a provider will only take so many: SQL Server allows about 2,100 in one
    /// statement, this cap is arbititrarily under that.
    /// </para>
    /// </summary>
    internal const int MaxValuesInList = 1000;

    /// <summary>
    /// How many values an operator takes, as an inclusive range
    /// </summary>
    /// <param name="Minimum">fewer than this is refused</param>
    /// <param name="Maximum">more than this is refused</param>
    internal record NumberOfValuesRequired(int Minimum, int Maximum);

    /// <summary>
    /// Check a value count against what the operator can use.
    /// <para>
    /// Called when a condition is built and again when it is turned into an expression. The second time is not
    /// redundant: a condition holds its values in a <see cref="List{T}"/> that a caller can still add to, so what
    /// it holds when the query is built is what actually becomes parameters
    /// </para>
    /// </summary>
    /// <param name="op"></param>
    /// <param name="field">named in the error</param>
    /// <param name="count"></param>
    /// <exception cref="WeequeryException">too few values for the operator, or too many</exception>
    internal static void ValidateValueCount(Operator op, string field, int count)
    {
        var required = GetNumberOfValuesRequiredForOperation(op);

        if (count < required.Minimum)
        {
            throw new WeequeryException(WeequeryError.OperandCount, $"Not enough values provided for Operator '{op}' on field '{field}', it needs at least {required.Minimum} but got {count}");
        }

        // IsIn has a cap, see MaxValuesInList
        if (count > required.Maximum)
        {
            throw new WeequeryException(WeequeryError.OperandCount, $"Extra values provided for Operator '{op}' on field '{field}', it accepts at most {required.Maximum} but got {count}");
        }
    }

    /// <summary>
    /// The values an operator takes, as a range. Checked wherever a condition is built.
    /// </summary>
    /// <param name="op"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    internal static NumberOfValuesRequired GetNumberOfValuesRequiredForOperation(Operator op)
    {
        switch (op)
        {
            case Operator.IsNull:
            case Operator.IsNotNull:
                return new(0, 0);

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
                return new(1, 1);

            case Operator.IsBetween:
            case Operator.IsNotBetween:
                return new(2, 2);

            case Operator.IsIn:
            case Operator.IsNotIn:
                return new(0, MaxValuesInList);

            case Operator.Or:
            case Operator.And:
            case Operator.Not:
            case Operator.Any:
            case Operator.All:
            case Operator.None:
                return new(0, 0);

            default:
                throw new WeequeryException(WeequeryError.OperatorInvalid, $"Operator {op} is invalid");
        }
    }

    /// <summary>
    /// Which of the four comparison shapes an operator belongs to
    /// </summary>
    /// <param name="op"></param>
    /// <returns>
    /// null Operators which do not test conditions directly
    /// </returns>
    /// <exception cref="WeequeryException">the operator is not one of the known ones</exception>
    internal static ConditionShape? GetShapeForOperation(Operator op)
    {
        switch (op)
        {
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
            case Operator.Any:
            case Operator.All:
            case Operator.None:
                return null;

            default:
                throw new WeequeryException(WeequeryError.OperatorInvalid, $"Operator {op} is invalid");
        }
    }

}
