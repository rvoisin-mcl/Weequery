namespace Weequery;

/// <summary>
/// One thing wrong with a query, as <see cref="Inquiry{T}.Validate()"/> found it. The same refusal that would
/// have been thrown, reported rather than raised.
/// </summary>
/// <remarks>
/// <para>
/// Carries what a <see cref="WeequeryException"/> carries, because it is one: the reason to branch on and the
/// message to read, taken off the exception the build would have thrown. What it adds is
/// <see cref="Part"/>, since a validation looks at all three halves of a query rather than stopping at the
/// first, and knowing which half a complaint came from is what lets it be shown beside the input box that
/// caused it.
/// </para>
/// </remarks>
/// <param name="Part">
/// which half of the query this is about: <see cref="BindingUse.Test"/>, <see cref="BindingUse.Sort"/> or
/// <see cref="BindingUse.Projection"/>, and <see cref="BindingUse.None"/> for the request as a whole rather than
/// any one of them, which is what a page size and a page index that cannot be combined are. Always exactly one
/// of them, never a combination
/// </param>
/// <param name="Error">
/// why it was refused, as something to branch on rather than read. See <see cref="WeequeryError"/>
/// </param>
/// <param name="Message">
/// what the refusal said, which names the offending input. Written for whoever typed the query rather than for
/// whoever wrote the model
/// </param>
public record ValidationProblem(BindingUse Part, WeequeryError Error, string Message)
{
    /// <summary>
    /// What is wrong and where, for logging or for an answer to the caller
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return (Part == BindingUse.None) ? Message : $"{Part.ToString().ToLowerInvariant()}: {Message}";
    }
}
