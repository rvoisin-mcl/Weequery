using BenchmarkDotNet.Attributes;

namespace Weequery.Benchmarks;

/// <summary>
/// Declaring the allow-list, which is the part of a request that scales with the model rather than the filter.
/// </summary>
/// <remarks>
/// <para>
/// Three ways to say the same nine bindings, and they do not cost the same thing. The fluent calls build a
/// binding each, every time. <see cref="Inquiry{T}.BindProperties"/> goes through the process wide cache and
/// pays for a lookup and a copy. <see cref="Inquiry{T}.ResolveBindables"/> reflects over the type and is the
/// only one that touches reflection at all.
/// </para>
/// <para>
/// Worth having published because the guidance follows from the numbers rather than from taste: whether it is
/// worth hoisting a <c>BindingRequest[]</c> to a static field depends on what the copy costs against what the
/// building costs, and until this is measured that advice is a guess.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class BindingBenchmarks
{
    private readonly IQueryable<Henchman> Empty = Array.Empty<Henchman>().AsQueryable();

    /// <summary>
    /// The declared set, resolved once for the process and copied in. What a request pays in an application that
    /// keeps its requests in a static, which is how the README says to write it.
    /// </summary>
    [Benchmark(Baseline = true, Description = "bind: nine, from a cached request set")]
    public Inquiry<Henchman> BindFromRequests()
    {
        return Empty.WithWeequery().BindProperties(Workloads.Bindings);
    }

    /// <summary>
    /// The same nine written out, which builds every binding again on every call.
    /// </summary>
    [Benchmark(Description = "bind: nine, fluent calls")]
    public Inquiry<Henchman> BindFluently()
    {
        return Empty.WithWeequery()
            .BindProperty(henchman => henchman.HenchmanID)
            .BindProperty(henchman => henchman.Name)
            .BindProperty(henchman => henchman.Alias)
            .BindProperty(henchman => henchman.Pay)
            .BindProperty(henchman => henchman.IsActive)
            .BindProperty(henchman => henchman.HireDate)
            .BindProperty(henchman => henchman.FireDate)
            .BindProperty(henchman => henchman.Clearance)
            .BindProperty(henchman => henchman.LairID);
    }

    /// <summary>
    /// The reflection walk on its own, without the binding that follows it. Not cached, so this is what it costs
    /// every time it is called, which is the argument for calling it once and keeping the result.
    /// </summary>
    [Benchmark(Description = "resolve: reflect the whole type")]
    public IReadOnlyList<BindingRequest> ResolveBindables()
    {
        return Inquiry<Henchman>.ResolveBindables();
    }

    /// <summary>
    /// A collection bound for quantifiers, which builds an inner binding set against a second type.
    /// </summary>
    [Benchmark(Description = "bind: a collection for quantifying")]
    public Inquiry<Henchman> BindCollection()
    {
        return Empty.WithWeequery()
            .BindProperties(Workloads.Bindings)
            .BindCollection(henchman => henchman.Assignments, "Assignments", inner => inner
                .BindProperty(assignment => assignment.Role)
                .BindProperty(assignment => assignment.Stipend)
                .BindProperty(assignment => assignment.IsPrimary));
    }
}
