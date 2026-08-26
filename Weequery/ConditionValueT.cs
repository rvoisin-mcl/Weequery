using System.Text.Json.Serialization;

namespace Weequery;

/// <summary>
/// One of a condition's operands, and what it is: a value to compare against, or the key of another bound
/// property to compare against.
/// </summary>
/// <remarks>
/// <para>
/// A value and a property reference are both text by the time a condition has been packed or written, so which
/// one it is has to travel with it. In the query language that is what the brackets say: <c>Pay &gt; 10000</c>
/// compares against a number, <c>Pay &gt; [Salary]</c> against the property bound as Salary. Nothing is guessed
/// from the text itself, so a bare word is a value, and a value that happens to spell a binding key stays a value.
/// </para>
/// <para>
/// A key is a name whatever the property it names holds, so only a <c>ConditionValue&lt;string&gt;</c> can carry
/// one. A condition built with values of some other type is therefore always a comparison against values, which
/// is what lets a builder chosen by one binding's type read its operands without asking.
/// </para>
/// <para>
/// Serialized in the compact form, where a value travels as itself and only a key carries a source with it, see
/// <see cref="ConditionValueConverter"/>.
/// </para>
/// </remarks>
/// <typeparam name="T">what the operand is, which is string for anything that arrived as text</typeparam>
/// <param name="Source">whether this is a value or the key of a property</param>
/// <param name="Value">the value, or the binding key, depending on the source</param>
[JsonConverter(typeof(ConditionValueConverter))]
public record ConditionValue<T>(ValueSource Source, T Value)
{
    /// <summary>
    /// Whether this names another bound property rather than being something to compare against directly
    /// </summary>
    [JsonIgnore]
    public bool NamesProperty => (Source == ValueSource.Binding);

    /// <summary>
    /// The same operand held as text, formatted invariantly and round-trippably, which is the form it travels in.
    /// See <see cref="ValueFormat.ToInvariantString"/>. A key is already text, so it comes back as itself.
    /// </summary>
    /// <returns></returns>
    public ConditionValue<string> Stringify()
    {
        // Already text, so there is nothing to format and nothing to allocate
        if (this is ConditionValue<string> text) { return text; }

        return new ConditionValue<string>(Source, ValueFormat.ToInvariantString(Value));
    }
}
