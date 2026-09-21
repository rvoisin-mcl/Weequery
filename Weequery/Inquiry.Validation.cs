using System.Diagnostics.CodeAnalysis;

namespace Weequery;

// Whether a build would succeed, answered without building, so a caller can say what is wrong with a request
// before running it.
public partial class Inquiry<T> where T : class
{
    /// <summary>
    /// Determine if this query is valid, and if not, what is wrong with it <see cref="ValidationResult"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Everything <see cref="Build"/> would refuse, reported rather than raised, and all of it rather than only the
    /// first encountered
    /// <code>
    /// var problems = inquiry.Validate();
    /// if (!problems.IsValid) { return BadRequest(problems.Problems.Select(problem =&gt; problem.ToString())); }
    /// </code>
    /// </para>
    /// <para>
    /// <b>Nothing is executed and nothing is kept.</b> The queries this builds to see whether they can be built
    /// are thrown away, so this costs what a build costs and changes nothing about what a later build does. The
    /// one thing it does leave behind is <see cref="DroppedFields"/>, which it fills exactly as a build fills it,
    /// since what a query quietly drops is worth knowing at the same time as what it refuses outright, see
    /// <see cref="InquirySettings.IgnoreUnboundFields"/>.
    /// </para>
    /// <para>
    /// <b>Valid means it will build</b>, which doesn't necessarily mean it will work. A provider may still refuse
    /// what it is handed <see cref="Operator.IsMatch"/> against SQL Server is the standing example. Where what a
    /// backend cannot do is known, say so on <see cref="InquirySettings.Operators"/> and that much of it is
    /// refused here instead of there.
    /// </para>
    /// </remarks>
    /// <returns>any problems found, it order of discovery; never null</returns>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    public ValidationResult Validate()
    {
        Dropped.Clear(); // should only represent the last Build() or Validate(), not cumulative

        List<ValidationProblem> problems = [];

        try
        {
            var condition = Combined();
            if (condition is not null) { Predicate(condition); }
        }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Test, error.Error, error.Message)); }

        // Against the unfiltered query, so a condition that refused does not take the sorts down with it
        try { Sorted(Query); }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Sort, error.Error, error.Message)); }

        // Only if a projection was requested
        if (!Projected.IsEmpty)
        {
            try { Projector(); }
            catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.Projection, error.Error, error.Message)); }
        }

        return (problems.Count == 0) ? ValidationResult.Valid : new ValidationResult(problems);
    }

    /// <summary>
    /// Determine if anything is wrong with a request prior to application. See <see cref="QueryRequest"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Should be used on externally sourced rquests prior to <see cref="ApplyRequest"/>, which will only
    /// return the first issue found.
    /// <code>
    /// var problems = inquiry.Validate(request, DefaultSort);
    /// if (!problems.IsValid) { return BadRequest(problems.Problems.Select(problem =&gt; problem.ToString())); }
    ///
    /// var (page, total) = inquiry.ApplyRequest(request, DefaultSort).BuildPagedProjected();
    /// </code>
    /// </para>
    /// <para>
    /// <b>Will not modify the Inquiry</b> A copy of the current Inquiry is created to test against, then discarded.
    /// </para>
    /// <para>
    /// <b>Two passes, each portion can report twice.</b> Reading the text and resolving what it says against the
    /// bindings are separate failures: a sort clause that will not parse is one problem, and a sort clause that
    /// parses and names an unbound field is another. Every parse failure is reported first, then everything
    /// <see cref="Validate()"/> finds in what did parse.
    /// </para>
    /// <para>
    /// <b>It validates the request with current Inquiry settings</b> a condition this Inquiry already carries 
    /// is ANDed with the request's, and is validated alongside it.
    /// </para>
    /// </remarks>
    /// <param name="request">the caller's query; null asks about this Inquiry as it stands, see <see cref="Validate()"/></param>
    /// <param name="defaultSort">what to sort by where the request named no sorts, as <see cref="ApplyRequest"/> takes it</param>
    /// <returns>the problems, parse faults first; never null</returns>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    public ValidationResult Validate(QueryRequest? request, IEnumerable<Sort>? defaultSort = null)
    {
        if (request is null) { return Validate(); }

        List<ValidationProblem> problems = [];
        var candidate = Copy();

        ParsedQuery? unpacked = null;

        try
        {
            unpacked = request.Unpack(defaultSort);
        }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.None, error.Error, error.Message)); }

        // Not one of segments, so it is reported against the request itself
        try { candidate = candidate.ApplyPagination(request.PageSize, request.Page ?? 0); }
        catch (WeequeryException error) { problems.Add(new ValidationProblem(BindingUse.None, error.Error, error.Message)); }

        // Attempt to apply what made it through the unpack. If it didn't unpack, there is nothing to apply
        candidate = candidate
            .ApplyCondition(unpacked?.Condition)
            .ApplySorts(unpacked?.Sorts)
            .ApplyProjection(unpacked?.Projection);

        problems.AddRange(candidate.Validate().Problems);

        return (problems.Count == 0) ? ValidationResult.Valid : new ValidationResult(problems);
    }

}
