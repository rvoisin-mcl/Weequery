using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Weequery.EntityFrameworkCore;

/// <summary>
/// Checks a binding list against an EF Core model, before there is a query to check.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Inquiry{T}.Validate()"/> asks whether a <i>request</i> fits the bindings. This asks the question
/// underneath it, which nothing in Weequery can answer on its own: whether the bindings fit the <b>database</b>.
/// A path that no column maps is not a Weequery error, because Weequery has no opinion about what the model
/// holds, so it survives every check the library makes and fails on the first request that names it.
/// </para>
/// <code>
/// // In startup, once, where a failure is yours rather than a caller's
/// var problems = context.ValidateBindings&lt;Minion&gt;(MinionBindings);
///
/// if (!problems.IsValid) { throw new InvalidOperationException(problems.ToString()); }
/// </code>
/// <para>
/// <b>No connection is opened.</b> The model is built from your configuration rather than read from the server,
/// so this costs whatever building the model costs and nothing after that. It is a startup check, and belongs
/// beside the one that proves the connection string works rather than on the request path.
/// </para>
/// <para>
/// <b>Mapped is not the same as translatable</b>, and this only answers the first. Whether a query will
/// translate depends on the operator and the provider as much as the property, see <see cref="Operator.IsMatch"/>
/// against SQL Server, so a binding that passes here can still meet a provider that will not take what is asked
/// of it. The complete check for one query is to build it and call <c>ToQueryString()</c>, which compiles it
/// through the provider without opening a connection either, and throws where it cannot. That is per query; this
/// is per binding, and the two answer different halves.
/// </para>
/// </remarks>
public static class ModelValidation
{
    /// <summary>
    /// Every binding whose path the model cannot account for.
    /// </summary>
    /// <remarks>
    /// Walks each <see cref="BindingRequest.PropertyPath"/> segment by segment: a mapped property ends it, a
    /// navigation continues it, and anything else is reported with the type that was being looked in, so a
    /// misspelling three levels down says which level.
    /// </remarks>
    /// <typeparam name="T">the entity the bindings are against</typeparam>
    /// <param name="context">any context built on the model in question; nothing is executed against it</param>
    /// <param name="bindingRequests">the list as it would be handed to <see cref="Inquiry{T}.BindProperties"/></param>
    /// <returns>the problems, in the order the bindings were given; never null</returns>
    /// <exception cref="WeequeryException">the context or the list is null</exception>
    [RequiresUnreferencedCode("Weequery reaches properties by the name a caller gives at runtime, so trimming cannot know which ones to keep")]
    public static ValidationResult ValidateBindings<T>(this DbContext context, IEnumerable<BindingRequest> bindingRequests)
        where T : class
    {
        WeequeryException.ThrowIfNull(context);
        WeequeryException.ThrowIfNull(bindingRequests);

        var entity = context.Model.FindEntityType(typeof(T));

        if (entity is null)
        {
            return new ValidationResult([Problem($"{typeof(T).Name} is not part of this model, so nothing bound against it can reach a database")]);
        }

        List<ValidationProblem> problems = [];

        foreach (var request in bindingRequests)
        {
            WeequeryException.ThrowIfNull(request, nameof(bindingRequests));

            var problem = Unmapped(entity, request);

            if (problem is not null) { problems.Add(problem); }
        }

        return (problems.Count == 0) ? ValidationResult.Valid : new ValidationResult(problems);
    }

    /// <summary>
    /// The one thing wrong with a binding's path, or null where the model accounts for all of it.
    /// </summary>
    /// <remarks>
    /// One problem per binding rather than all of them, since a path stops meaning anything after the first
    /// segment that does not resolve: there is nothing to look in for the rest of it.
    /// </remarks>
    /// <param name="entity">the type the path starts at</param>
    /// <param name="request"></param>
    /// <returns></returns>
    private static ValidationProblem? Unmapped(IEntityType entity, BindingRequest request)
    {
        var looking = entity;
        var segments = request.PropertyPath.Split('.');

        for (var index = 0; index < segments.Length; index++)
        {
            var segment = segments[index];
            var last = index == (segments.Length - 1);

            // A mapped scalar. It can end a path and cannot continue one
            if (looking.FindProperty(segment) is not null)
            {
                return last
                    ? null
                    : Problem(request, $"'{segment}' is a mapped column rather than something to reach through, so '{segments[index + 1]}' is not there to bind");
            }

            var navigation = looking.FindNavigation(segment);

            if (navigation is null)
            {
                return Problem(request, $"{looking.ClrType.Name} has no mapped property or navigation called '{segment}'");
            }

            // Binding the navigation itself is legal, and only the null tests will work on it
            if (last) { return null; }

            // Many rows have many values, so there is no single one for the rest of the path to read. A
            // quantifier is how a collection is asked about, see BindCollection
            if (navigation.IsCollection)
            {
                return Problem(request, $"'{segment}' is a collection, so '{segments[index + 1]}' cannot be reached through it. Bind it as a collection and ask about its elements with a quantifier");
            }

            looking = navigation.TargetEntityType;
        }

        return null;
    }

    /// <summary>
    /// A problem naming the binding it is about.
    /// </summary>
    /// <remarks>
    /// Reported against <see cref="BindingUse.None"/> rather than the binding's own <see cref="BindingUse"/>: a
    /// path the model cannot account for is wrong for every use it was declared for, and naming one of them
    /// would read as though the others were fine.
    /// </remarks>
    /// <param name="request"></param>
    /// <param name="because">what the model said, which the message ends with</param>
    /// <returns></returns>
    private static ValidationProblem Problem(BindingRequest request, string because)
    {
        var named = (request.Key == request.PropertyPath)
            ? $"'{request.PropertyPath}'"
            : $"'{request.Key}' ({request.PropertyPath})";

        return Problem($"{named} is not mapped: {because}");
    }

    /// <summary>
    /// The same, for what is wrong with the whole list rather than with one of its entries.
    /// </summary>
    /// <param name="message"></param>
    /// <returns></returns>
    private static ValidationProblem Problem(string message)
    {
        return new ValidationProblem(BindingUse.None, WeequeryError.NotTranslatable, message);
    }
}
