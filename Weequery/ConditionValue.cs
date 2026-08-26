namespace Weequery;

/// <summary>
/// Builds the operands a condition compares against, see <see cref="ConditionValue{T}"/>. Separate from the
/// record so that the type can be inferred for a value and fixed to string for a key, which is what a key always
/// is.
/// </summary>
public static class ConditionValue
{
    /// <summary>
    /// A value the caller supplied, to be compared against directly
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="value"></param>
    /// <returns></returns>
    public static ConditionValue<T> Raw<T>(T value)
    {
        return new ConditionValue<T>(ValueSource.Raw, value);
    }

    /// <summary>
    /// The key of another bound property, whose value is what the comparison is against
    /// </summary>
    /// <param name="key"></param>
    /// <returns></returns>
    public static ConditionValue<string> Binding(string key)
    {
        return new ConditionValue<string>(ValueSource.Binding, key);
    }
}
