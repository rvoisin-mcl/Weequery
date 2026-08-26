namespace Weequery;

/// <summary>
/// Where one of a condition's values comes from: text the caller supplied, or another bound property to compare
/// against.
/// </summary>
/// <remarks>
/// A value and a field reference are both text by the time a condition has been packed or written, so which one it
/// is has to travel with it. In the query language that is what the brackets say: <c>Pay &gt; 10000</c> compares
/// against a number, <c>Pay &gt; [Salary]</c> compares against the property bound as Salary. Nothing is guessed
/// from the text itself a bare word is a value, and a value that happens to spell a binding key stays a value.
/// </remarks>
public enum ValueSource
{
    /// <summary>Text the caller supplied, to be read as the bound property's type</summary>
    Raw,

    /// <summary>The key of another bound property, whose value is what this compares against</summary>
    Binding,
}
