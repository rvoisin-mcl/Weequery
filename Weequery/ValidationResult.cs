namespace Weequery;

/// <summary>
/// What <see cref="Inquiry{T}.Validate()"/> found: everything wrong with a query, or nothing at all. See
/// <see cref="ValidationProblem"/>.
/// </summary>
/// <remarks>
/// <para>
/// The point of it is the answer arriving as a <i>list</i> rather than as the first exception thrown. A caller
/// filling in three boxes on a form has three ways to be wrong, and telling them about the filter, letting them
/// fix it, and only then telling them about the sort is a round trip per mistake.
/// </para>
/// <code>
/// var problems = inquiry.Validate(request);
/// if (!problems.IsValid) { return BadRequest(problems.Problems.Select(problem => problem.ToString())); }
/// </code>
/// <para>
/// <b>One problem per half per pass, not every problem in it.</b> Each half is built until it refuses something,
/// and what it refused is what is reported, so a condition naming two unbound fields reports the first of them.
/// Fixing that one and asking again is what finds the second. Reporting every fault in a single condition would
/// mean the expression builder carrying on past a field it could not resolve, building the rest of a tree around
/// a hole, and that is a worse thing to own than a second round trip. Validating a
/// <see cref="QueryRequest"/> reads the text and then resolves what it says, which are two passes, so a half
/// that will not parse and a half that parses and names nothing bound are two different problems and a half can
/// report one of each.
/// </para>
/// <para>
/// <b>It answers about the query, not about the data.</b> Nothing here runs, so a valid query is one that will
/// build, which is a different claim from one that will return rows, or one the database will accept: a
/// <see cref="Operator.IsMatch"/> against SQL Server validates here and fails there, being refused by the
/// provider rather than by the allow-list.
/// </para>
/// </remarks>
/// <param name="Problems">
/// what is wrong, in the order the halves are looked at: condition, then sort, then projection. Never null, and
/// empty for the query with nothing wrong with it
/// </param>
public record ValidationResult(IReadOnlyList<ValidationProblem> Problems)
{
    /// <summary>
    /// The answer for a query with nothing wrong with it.
    /// </summary>
    public static readonly ValidationResult Valid = new([]);

    /// <summary>
    /// If the query will build. The thing most callers are asking.
    /// </summary>
    public bool IsValid { get { return Problems.Count == 0; } }

    /// <summary>
    /// Every problem on one line, or a note that there were none
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return IsValid ? "valid" : string.Join("; ", Problems);
    }
}
