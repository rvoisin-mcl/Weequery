using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Base type for comparison types, containing the common requirements: hold a field 
/// and operator, refuse an incompatible operator, render, pack, and stringify.
/// </summary>
/// <remarks>
/// The types are <see cref="NoValueCondition"/>, <see cref="OneValueCondition{T}"/>,
/// <see cref="TwoValueCondition{T}"/> and <see cref="MultipleValueCondition{T}"/>, and the only thing that
/// distinguishes them is how many operands they hold. A caller with its own condition can implement
/// <see cref="IBoundCondition"/> instead; nothing in the library requires this base.
/// </remarks>
public abstract class BoundCondition : IBoundCondition
{
    /// <summary>
    /// The test this performs
    /// </summary>
    public Operator Operator { get; init; }

    /// <summary>
    /// The binding key this tests
    /// </summary>
    public string Field { get; init; }

    /// <summary>
    /// [OPT] Which element of the biunding this tests
    /// </summary>
    public string? Index { get; init; }

    /// <summary>
    /// ctor
    /// </summary>
    /// <param name="op">must be one of the operators the derived type's shape covers</param>
    /// <param name="field"></param>
    /// <param name="shape">what the derived type holds, which decides the operators it can carry</param>
    /// <param name="index">
    /// [OPT] which element of the collection to test, as text. Null tests the binding itself
    /// </param>
    /// <exception cref="WeequeryException">the field is missing, or the operator is not of that shape</exception>
    protected BoundCondition(Operator op, string field, ConditionShape shape, string? index = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(field);
        WeequeryException.ThrowIfNotNullButEmpty(index);

        if (ConditionFunctions.GetShapeForOperation(op) != shape)
        {
            throw new WeequeryException(WeequeryError.OperatorInvalid, $"Operator '{op}' on field '{field}' cannot be represented by {TypeName()}: it is not one of the operators that take {Describe(shape)}");
        }

        Operator = op;
        Field = field;
        Index = index;
    }

    /// <summary>
    /// The type name without the arity the runtime appends
    /// </summary>
    private string TypeName()
    {
        var name = GetType().Name;
        var arity = name.IndexOf('`');

        return (arity < 0) ? name : name[..arity];
    }

    /// <summary>
    /// What a shape holds
    /// </summary>
    private static string Describe(ConditionShape shape)
    {
        return shape switch
        {
            ConditionShape.NoValue => "no value",
            ConditionShape.OneValue => "a single value",
            ConditionShape.TwoValue => "a pair of values",
            ConditionShape.MultipleValue => "a list of values",

            _ => shape.ToString(),
        };
    }

    /// <summary>
    /// Check one operand: present, not missing a value, and if it names a property then named in a way that can
    /// be read back.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="op"></param>
    /// <param name="field">named in the error, since a caller building several conditions needs to know which</param>
    /// <param name="operand"></param>
    /// <param name="index">position among the operands, used for reporting</param>
    /// <param name="count">how many operands there are, used for reporting</param>
    /// <returns>the operand, so this can be used where one is being assigned</returns>
    /// <exception cref="WeequeryException">operator is invalid</exception>
    protected static ConditionValue<T> Validate<T>(Operator op, string field, ConditionValue<T> operand, int index, int count)
    {
        if (operand is null)
        {
            throw new WeequeryException(WeequeryError.ValueInvalid, $"Value {index + 1} of {count} for Operator '{op}' on field '{field}' is null");
        }

        if (operand.Value is null)
        {
            throw new WeequeryException(WeequeryError.ValueInvalid, $"Value {index + 1} of {count} for Operator '{op}' on field '{field}' is null; use {Operator.IsNull} to test for a missing value");
        }

        if (!operand.NamesProperty) { return operand; }

        // A key is a name whatever the property holds, so it cannot be carried as a value of another type.
        if (operand.Value is not string key)
        {
            throw new WeequeryException(WeequeryError.ValueInvalid, $"Value {index + 1} of {count} for Operator '{op}' on field '{field}' names a bound property, so cannot be represented by {typeof(T).Name}");
        }

        // An operand may carry an index along with the key, "Tallies[apples]"
        var split = BindingLookup.SplitIndex(key);
        if (!QueryTokenizer.IsBareWord(split.Key))
        {
            throw new WeequeryException(WeequeryError.KeyInvalid, $"'{key}' is not a legal binding name");
        }

        return operand;
    }

    /// <summary>
    /// <inheritdoc cref="IBoundCondition.StringifyOperands"/>
    /// </summary>
    /// <returns></returns>
    public abstract List<ConditionValue<string>> StringifyOperands();

    /// <summary>
    /// <inheritdoc cref="IBoundCondition.StringifyValues"/>
    /// </summary>
    /// <returns></returns>
    public List<string> StringifyValues()
    {
        return [.. from operand in StringifyOperands() select operand.Value];
    }

    /// <summary>
    /// Flatten to the serializable shape, with the operands as text and carrying what each of them is
    /// </summary>
    /// <returns></returns>
    public PackedCondition Pack()
    {
        return new PackedCondition(this);
    }

    /// <summary>
    /// Renders in the query language. See <see cref="ConditionFunctions.ToQuery"/> for the round-trippable form,
    /// which is the same text except that this will not throw on a condition the language cannot express.
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return QueryWriter.Describe(this);
    }
}
