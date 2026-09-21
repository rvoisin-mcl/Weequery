using System.Linq.Expressions;
using BenchmarkDotNet.Attributes;

namespace Weequery.Benchmarks;

/// <summary>
/// A whole request, from the text a caller sent to a query ready to enumerate.
/// </summary>
/// <remarks>
/// <para>
/// Nothing executes: <c>Build</c> hands back an <see cref="IQueryable{T}"/> and the provider is not asked for
/// anything until something enumerates it. So this is the fixed cost a request pays before the database is
/// involved at all (bind, parse, build, sort, page) and is the number to put beside whatever the query itself
/// takes. See <see cref="FilteringBenchmarks"/> for the executing half.
/// </para>
/// <para>
/// Each of these hands back the built <see cref="IQueryable{T}.Expression"/> rather than the query. Returning the
/// query itself is exactly the mistake BenchmarkDotNet refuses to let through: a deferred result usually means a
/// benchmark measured nothing, but here the deferral is the point, and the expression tree is the thing that was
/// actually built. Reading the property off the query costs nothing and leaves the tree where the JIT cannot
/// decide the whole call was pointless.
/// </para>
/// <para>
/// Written the way the README writes it, in one chain per request, because that is what is being measured: the
/// cost of the documented usage rather than of a tuned one.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class PipelineBenchmarks
{
    private readonly IQueryable<Henchman> Empty = Array.Empty<Henchman>().AsQueryable();

    /// <summary>Bind and build with no filtering at all, so the floor a request cannot go below</summary>
    [Benchmark(Baseline = true, Description = "request: bind and build, no condition")]
    public Expression BindAndBuild()
    {
        return Empty.WithWeequery().BindProperties(Workloads.Bindings).Build().Expression;
    }

    /// <summary>The ordinary case: a filter string, a sort string and a page</summary>
    [Benchmark(Description = "request: filter, sort and page")]
    public Expression FilterSortAndPage()
    {
        return Empty.WithWeequery()
            .BindProperties(Workloads.Bindings)
            .ApplyCondition(Workloads.Typical)
            .ApplySorts("Pay DESC, Name")
            .ApplyPagination(pageSize: 20, page: 0)
            .Build()
            .Expression;
    }

    /// <summary>The same, with the caller's advanced search behind it</summary>
    [Benchmark(Description = "request: complex filter, sort and page")]
    public Expression ComplexFilterSortAndPage()
    {
        return Empty.WithWeequery()
            .BindProperties(Workloads.Bindings)
            .ApplyCondition(Workloads.Complex)
            .ApplySorts("Pay DESC, Name")
            .ApplyPagination(pageSize: 20, page: 0)
            .Build()
            .Expression;
    }

    /// <summary>
    /// Reading back a subset rather than the entity, which builds a <c>Dictionary</c> projection instead of a
    /// pass through and is the one path that constructs an expression per selected field.
    /// </summary>
    [Benchmark(Description = "request: filter, sort, page and project")]
    public Expression FilterSortPageAndProject()
    {
        return Empty.WithWeequery()
            .BindProperties(Workloads.Bindings)
            .ApplyCondition(Workloads.Typical)
            .ApplySorts("Pay DESC")
            .ApplyProjection("Name,Alias,Pay")
            .ApplyPagination(pageSize: 20, page: 0)
            .BuildProjected()
            .Expression;
    }

    /// <summary>
    /// A quantifier, which binds a second type and builds a lambda inside the predicate.
    /// </summary>
    [Benchmark(Description = "request: quantified filter")]
    public Expression QuantifiedFilter()
    {
        return Empty.WithWeequery()
            .BindProperties(Workloads.Bindings)
            .BindCollection(henchman => henchman.Assignments, "Assignments", inner => inner
                .BindProperty(assignment => assignment.Role)
                .BindProperty(assignment => assignment.Stipend))
            .ApplyCondition(Workloads.Quantified)
            .Build()
            .Expression;
    }
}
