using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Weequery.Builders;
using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Wrapper class that provides fluent configuration for IQueryable with Weequery
/// </summary>
/// <remarks>
/// The bound properties are the allow-list: a condition or a sort naming a field that no binding claimed is
/// refused. Field names are matched against binding keys without regard to case.
/// </remarks>
/// <typeparam name="T"></typeparam>
public class Inquiry<T> where T : class
{
    private IQueryable<T> Query { get; init; }

    /// <summary>
    /// The "x" every accessor hangs off, one per entity type rather than one per query.
    /// <para>
    /// All the bindings used together have to share it: a lambda is built from one binding's parameter and a body
    /// assembled from several, so accessors rooted in different parameters would not compose. Sharing it for the
    /// whole type is what lets a binding built once be used by every query after it, see
    /// <see cref="BindingSets"/>, and costs nothing to do — an expression tree is immutable, and a parameter is an
    /// identity rather than a value, so two lambdas built over the same one are still two independent lambdas.
    /// </para>
    /// <para>
    /// The one place it would matter is a lambda nested inside another over the same type, where the inner one
    /// would rebind the parameter and shadow the outer. Weequery never builds that shape; a caller composing two
    /// predicates of its own into one is the only way to reach it, see the remarks on
    /// <see cref="BuildExpression"/>.
    /// </para>
    /// </summary>
    private static readonly ParameterExpression SharedBindingParameter = Expression.Parameter(typeof(T));

    private Dictionary<string, Binding<T>> Bindings { get; init; } = BindingLookup.Create<T>();

    /// <summary>
    /// The bound collections, kept apart from the properties because they answer a different kind of question.
    /// <para>
    /// A collection is not something the comparison operators can be asked, and a property is not something a
    /// quantifier can be asked, so one lookup would only mean each of them refusing half its entries. Keyed the
    /// same way, so a collection and a property still cannot share a name, see <see cref="BindCollection"/>.
    /// </para>
    /// </summary>
    private Dictionary<string, ICollectionBinding<T>> Collections { get; init; } = new(BindingLookup.KeyComparer);
    private List<ICondition> Conditions { get; init; } = new();
    private List<Sort> Sorts { get; init; } = new();
    private int PageSize { get; set; } = -1;
    private int Page { get; set; } = -1;

    /// <summary>
    /// How long <see cref="Operator.IsMatch"/> may spend on one value before giving up, when the match runs in
    /// this process. One second by default; assign to change it, or
    /// <see cref="Regex.InfiniteMatchTimeout"/> to remove the bound.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A pattern is caller input, and a regular expression can be made to cost far more than it looks: matching
    /// <c>(a+)+$</c> against a few dozen characters that do not match takes time exponential in their number.
    /// Every other operator is bounded by the size of the data, so this is the one that can turn a filter into a
    /// denial of service, and it is bounded rather than left to run.
    /// </para>
    /// <para>
    /// A value that exceeds it raises <see cref="RegexMatchTimeoutException"/> from wherever the query is being
    /// enumerated, rather than a <see cref="WeequeryException"/>: it is the framework reporting what it stopped
    /// doing, and no answer is available for that row.
    /// </para>
    /// <para>
    /// This bounds the match only where Weequery runs it, which is in memory. Translated to SQL the pattern is
    /// the database's to run and its own limits apply, see <see cref="Operator.IsMatch"/>. The reason it cannot
    /// be both is that the overload of
    /// <see cref="Regex.IsMatch(string, string, RegexOptions, TimeSpan)"/> carrying a timeout is not one any
    /// provider translates, so an expression built with it would stop being a query and start being a table
    /// scan on the client.
    /// </para>
    /// </remarks>
    public static TimeSpan MatchTimeout { get; set; } = TimeSpan.FromSeconds(1);

    internal Inquiry(IQueryable<T> query)
    {
        Query = query;
    }

    /// <summary>
    /// Bind the property indicated by the selector func, if a key is not provided, it will be bound as the property path
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    public Inquiry<T> BindProperty<TProperty>(Expression<Func<T, TProperty>> selector, string? key = null)
    {
        Binding<T>.Create(SharedBindingParameter, selector, Bindings, key);

        return this;
    }

    /// <summary>
    /// Bind the property reached by following the selector and then the segments after it, for a path a selector
    /// cannot write on its own. If a key is not provided, the last segment is used.
    /// </summary>
    /// <remarks>
    /// The case this exists for is a path through a <see cref="Nullable{T}"/>: C# will not compile
    /// <c>(x) =&gt; x.BirthDate.Year</c> against a <c>DateTime?</c>, since a Nullable exposes only its own members,
    /// and writing <c>(x) =&gt; x.BirthDate!.Value.Year</c> instead unwraps rather than reaches through, binding a
    /// plain int with no null of its own. Selecting BirthDate and naming "Year" as a segment binds
    /// "BirthDate.Year" exactly as <see cref="BindProperty(string, string?)"/> would, while the compiler still
    /// checks the part of the path it can see. See the remarks on <see cref="Operator"/> for what reaching through
    /// a nullable means for the operators.
    /// <code>
    /// .BindProperty(minion =&gt; minion.BirthDate, ["Year"], "BirthYear")
    /// </code>
    /// </remarks>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector">as far as the compiler can follow</param>
    /// <param name="segments">the rest of the path, in order</param>
    /// <param name="key"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> BindProperty<TProperty>(Expression<Func<T, TProperty>> selector, string[] segments, string? key = null)
    {
        Binding<T>.Create(SharedBindingParameter, selector, segments, Bindings, key);

        return this;
    }

    /// <summary>
    /// Bind a collection, and declare what may be asked about one of its elements, so a caller can ask whether
    /// <see cref="Operator.Any"/>, <see cref="Operator.All"/> or <see cref="Operator.None"/> of them match.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <code>
    /// .BindCollection(minion =&gt; minion.Assignments, "Assignments", inner =&gt; inner
    ///     .BindProperty(assignment =&gt; assignment.LairID)
    ///     .BindProperty(assignment =&gt; assignment.Lair.Name, "LairName"))
    ///
    /// .ApplyCondition("Assignments Any (LairID = 5 AND LairName StartsWith 'V')")
    /// </code>
    /// </para>
    /// <para>
    /// <b>The inside is its own allow-list.</b> Binding the collection exposes nothing within it; what a caller
    /// may name inside the brackets is what the inner configuration bound, and nothing else. That is the same
    /// rule the outer bindings follow, applied one level down, and it is why this takes a configuration rather
    /// than reaching into the element type on its own.
    /// </para>
    /// <para>
    /// The condition inside is scoped to <b>one element</b>, which is the whole reason a quantifier holds a
    /// condition rather than the caller writing two of them: "Any (LairID = 5 AND IsPrimary = true)" asks for one
    /// assignment that is both, where two separate quantifiers ANDed together ask only that each is true of some
    /// assignment, possibly different ones.
    /// </para>
    /// <para>
    /// The collection itself is not otherwise answerable: it is not bound as a property, so it takes no
    /// comparison and no index, and a caller naming it outside a quantifier is refused. Bind it with
    /// <see cref="BindProperty{TProperty}(Expression{Func{T, TProperty}}, string?)"/> as well if you also want
    /// it tested for null or indexed, under a different key.
    /// </para>
    /// <para>
    /// <b>Whether this reaches a database is the provider's business.</b> A quantifier becomes Any or All over
    /// the collection, which EF Core translates to EXISTS against a navigation collection. See the remarks on
    /// <see cref="Operator.Any"/> for what it means over one that is empty or missing.
    /// </para>
    /// </remarks>
    /// <typeparam name="TElement">what the collection holds</typeparam>
    /// <param name="selector">the collection on this entity</param>
    /// <param name="key">the name a caller refers to it by</param>
    /// <param name="configure">
    /// what may be asked about an element. Called once, on an empty set; a collection with nothing bound inside
    /// is refused, since no condition could ever be written against it
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the key is not one, or is already taken, or the property is not a collection, or nothing was bound inside
    /// </exception>
    public Inquiry<T> BindCollection<TElement>(
        Expression<Func<T, IEnumerable<TElement>?>> selector,
        string key,
        Action<CollectionBindingSet<TElement>> configure)
        where TElement : class
    {
        WeequeryException.ThrowIfNull(selector);
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);
        WeequeryException.ThrowIfNull(configure);

        // One name for one thing, whichever of the two lookups it lands in
        if (Bindings.ContainsKey(key) || Collections.ContainsKey(key))
        {
            throw new WeequeryException($"Binding already exists for '{key}'");
        }

        // Not added to Bindings: a collection answers a quantifier and nothing else, and putting it there would
        // offer it to every operator that cannot use it
        var collection = Binding<T>.Create(SharedBindingParameter, selector, bindings: null);

        var inner = new CollectionBindingSet<TElement>();
        configure(inner);

        if (inner.Count == 0)
        {
            throw new WeequeryException($"Nothing was bound inside '{key}', so no condition could be written about one of its elements");
        }

        Collections[key] = new CollectionBinding<T, TElement>(key, collection, inner.Bindings);

        return this;
    }

    /// <summary>
    /// Bind a value under a name, rather than a property. A caller can then compare a property against it by name
    /// without having to say what it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pairing this exists for is a comparison against a bound property, see
    /// <see cref="ConditionValue{T}"/>: the caller writes "Pay &gt; [Threshold]" and the application decides what
    /// Threshold is, per request, per tenant, or per anything else it knows and the caller does not.
    /// <code>
    /// .BindConstant("Threshold", payThreshold)
    /// .ApplyCondition("Pay &gt; [Threshold]")
    /// </code>
    /// </para>
    /// <para>
    /// As it is a constant value, a sort on it is refused.
    /// </para>
    /// <para>
    /// Deliberately not part of <see cref="BindingRequest"/>, as those are cached for the life of the process, 
    /// which may not be ideal for a value that may differ from one request to the next.
    /// </para>
    /// </remarks>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="key">the name a caller refers to it by</param>
    /// <param name="value">must not be null: a constant stands for a value, so it needs one</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> BindConstant<TValue>(string key, TValue value)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        Binding<T>.CreateConstant(SharedBindingParameter, key, value, Bindings);

        return this;
    }

    /// <summary>
    /// Bind the property with the provided path, if a key is not provided, it will be bound as the property path
    /// </summary>
    /// <param name="path"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    public Inquiry<T> BindProperty(string path, string? key = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(path);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        Binding<T>.Create(SharedBindingParameter, path, Bindings, key);

        return this;
    }

    /// <summary>
    /// Bind the properties in the list, if a key is not provided, they will be bound as the property path
    /// </summary>
    /// <remarks>
    /// A set of requests is resolved once for the process and kept, see <see cref="BindingSets"/>, so calling this
    /// per request costs a copy rather than a property path lookup per property. Adding to this Inquiry after it,
    /// with this or with <see cref="BindProperty(string, string?)"/>, works as it always did: everything binds
    /// against the same parameter either way.
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> BindProperties(IEnumerable<BindingRequest> bindingRequests)
    {
        WeequeryException.ThrowIfNull(bindingRequests);

        // Copied in rather than used as it stands, since this Inquiry's lookup can keep taking bindings after
        // this call and the kept set has to stay as it is
        foreach (var binding in BindingsFor(bindingRequests))
        {
            if (Bindings.ContainsKey(binding.Key)) { throw new WeequeryException($"Binding already exists for '{binding.Key}'"); }

            Bindings[binding.Key] = binding.Value;
        }

        return this;
    }

    /// <summary>
    /// Whether a property's type is one the caller asked to leave out, see
    /// <see cref="BindingResolutionSettings.IgnoreTypes"/>.
    /// </summary>
    /// <param name="type">the property's declared type</param>
    /// <param name="settings"></param>
    /// <returns></returns>
    private static bool ShouldIgnoreType(Type type, BindingResolutionSettings settings)
    {
        if (settings.IgnoreTypes.Count == 0) { return false; }
        if (settings.IgnoreTypes.Contains(type)) { return true; }
        if (!settings.IgnoreTypeWhenAssignable) { return false; }
        return settings.IgnoreTypes.Where(ignore => type.IsAssignableTo(ignore)).Any();
    }

    /// <summary>
    /// Whether a type holds anything worth walking into, which a container does not.
    /// </summary>
    /// <remarks>
    /// An array, a List, a Dictionary, anything a foreach would walk. What is *inside* one is not reachable from
    /// here, since this library does not filter into a collection, so all expansion yields is the container's own
    /// bookkeeping: Length, LongLength, Rank, SyncRoot, IsFixedSize, Count, Capacity. None of that is a question
    /// anyone meant to ask, and a provider will refuse to translate most of it. A string is one of these too,
    /// which is why Name.Length is not a key.
    /// </remarks>
    /// <param name="type"></param>
    /// <returns></returns>
    private static bool IsContainer(Type type)
    {
        // Arrays are covered by this too, every one of them implementing it
        return type.IsAssignableTo(typeof(System.Collections.IEnumerable));
    }

    /// <summary>
    /// Whether the walk should descend into a property, having already decided to bind it.
    /// </summary>
    /// <param name="type">the property's declared type</param>
    /// <param name="settings"></param>
    /// <param name="path">the property's whole path, which is what the ignore rules are matched against</param>
    /// <param name="ancestors">
    /// the types already open on the way here, see <see cref="ResolveBindables(int, BindingResolutionSettings)"/>
    /// </param>
    /// <returns></returns>
    private static bool ShouldExpandType(Type type, BindingResolutionSettings settings, string path, HashSet<Type> ancestors)
    {
        // a container holds its elements, which are not reachable, and its own bookkeeping, which is noise
        if (IsContainer(type)) { return false; }
        // the property has potential properties of its own. An interface counts: what it promises is reachable
        // through it, and a model that navigates by interface would otherwise resolve nothing below it
        if (!(type.IsClass || type.IsInterface)) { return false; }
        // if we have been directed to ignore child properties for this path
        if (settings.IgnorePaths.Contains($"{path}.")) { return false; }
        // if we have been directed not to expand this type
        if (settings.DoNotExpandTypes.Contains(type)) { return false; }
        // if this type is already open further up the same path, which is a cycle
        if (ancestors.Contains(type)) { return false; }

        return true;
    }

    /// <summary>
    /// The properties of a type that resolution will consider, before any of the settings are applied.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Readable, and not an indexer: an indexer has no path to bind, so taking one would refuse the whole model
    /// when it came to be bound.
    /// </para>
    /// <para>
    /// Names are made distinct, which matters twice. GetProperties on an interface returns what that interface
    /// declares and nothing it inherits, so the inherited ones are gathered separately and two interfaces may
    /// well promise the same name. And a derived class that shadows a property with <c>new</c> reports both.
    /// Either way a path can only mean one thing, so the first wins.
    /// </para>
    /// </remarks>
    /// <param name="type"></param>
    /// <returns></returns>
    private static IEnumerable<PropertyInfo> ReadableProperties(Type type)
    {
        IEnumerable<PropertyInfo> properties = type.GetProperties();

        if (type.IsInterface)
        {
            properties = properties.Concat(type.GetInterfaces().SelectMany(inherited => inherited.GetProperties()));
        }

        return properties
            .Where(prop => prop.CanRead && (prop.GetIndexParameters().Length == 0))
            .GroupBy(prop => prop.Name)
            .Select(group => group.First());
    }

    /// <summary>
    /// The key a resolved path is bound under, which is the path itself unless the language has claimed it.
    /// </summary>
    /// <remarks>
    /// A property named after an operator makes a key that a query could not tell from the operator, so binding
    /// it as it stands is refused, see <see cref="WeequeryException.ThrowIfNotBindingKey"/>. That is the right
    /// answer for a key someone chose and the wrong one for a whole model, which would otherwise resolve to
    /// nothing because one property happens to be called Contains. An underscore is a legal key character and no
    /// operator ends in one, so the suffix is always enough, and it is applied only where it is needed rather
    /// than to every key.
    /// <para>
    /// Only a whole key can collide: a nested "Lair.Contains" is not the operator to begin with, since the
    /// tokenizer reads a dotted path as one word.
    /// </para>
    /// </remarks>
    /// <param name="path">the resolved property path</param>
    /// <returns>the path, or the path with an underscore where the path is a reserved word</returns>
    private static string KeyFor(string path)
    {
        return QueryKeywords.IsReserved(path) ? $"{path}_" : path;
    }

    /// <summary>
    /// Walk one level of a type, adding a request for every readable property that survives the settings, and
    /// recursing into the ones that have properties of their own.
    /// </summary>
    /// <remarks>
    /// Properties are returned in path order.
    /// </remarks>
    /// <param name="bindings">the list being built, added to in place</param>
    /// <param name="type">the type to walk</param>
    /// <param name="depth">levels already descended, so 0 for the entity itself</param>
    /// <param name="maxDepth">how far down to go, already bounded by the caller</param>
    /// <param name="prefix">the path so far, empty at the top, which is what makes the keys dotted</param>
    /// <param name="settings"></param>
    /// <param name="ancestors">
    /// the types open on the path to here, which is what stops a cycle. A model where two types refer to each
    /// other has no bottom, and the walk would otherwise only be stopped by <paramref name="maxDepth"/>, one
    /// level of which multiplies the paths rather than adding to them
    /// </param>
    /// <returns>the same list, for the caller that started it</returns>
    private static IReadOnlyList<BindingRequest> ResolveBindables(List<BindingRequest> bindings, Type type, int depth, int maxDepth, string prefix, BindingResolutionSettings settings, HashSet<Type> ancestors)
    {
        // Open on the way in and closed on the way out, so the set is what is above this point on this path
        // rather than everything the walk has ever seen. Two properties of the same type are both expanded; the
        // same type twice down one chain is not.
        ancestors.Add(type);

        try
        {
            var properties = ReadableProperties(type).OrderBy(prop => prop.Name);
            foreach (var property in properties)
            {
                var pathName = (string.IsNullOrEmpty(prefix) ? property.Name : $"{prefix}.{property.Name}");

                // A value type with no builder cannot be bound at all, and taking it would refuse the whole model
                // over one property of a struct nobody meant to filter on. Skipped the way an indexer is.
                if (!ExpressionBuilder.CanBindPropertyType(property.PropertyType)) { continue; }

                if ((!settings.IgnorePaths.Contains(pathName)) && (!ShouldIgnoreType(property.PropertyType, settings)))
                {
                    bindings.Add(new(pathName, KeyFor(pathName)));

                    // if we haven't bottomed out, and settings say the property should be expanded
                    if ((depth < maxDepth) && (ShouldExpandType(property.PropertyType, settings, pathName, ancestors)))
                    {
                        ResolveBindables(bindings, property.PropertyType, depth + 1, maxDepth, pathName, settings, ancestors);
                    }
                }
            }
        }
        finally
        {
            ancestors.Remove(type);
        }

        return bindings;
    }

    /// <summary>
    /// Build a binding request for every readable property a type reaches, keyed by its path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the opposite of an allow-list, and it is worth stopping on.</b> Everywhere else in this library
    /// you say what may be asked about and everything else is refused. This says "all of it", so every property
    /// name on the type, and on every type it reaches, goes on the wire for a caller to read and filter by. That
    /// is a reasonable thing to want for an internal tool over a model you control, and an unreasonable thing to
    /// do to an entity with an audit trail, a password hash or another tenant's rows hanging off it. Use
    /// <see cref="BindingResolutionSettings"/> to subtract, or write the bindings out and know what they are.
    /// </para>
    /// <para>
    /// A nested property is keyed by its whole dotted path, "Lair.Capacity" rather than "Capacity", which is a
    /// legal key because a period is a legal key character, see
    /// <see cref="WeequeryException.ThrowIfNotBindingKey"/>. Paths are distinct by construction, so nothing here
    /// collides with anything else here; it can still collide with a binding already made by hand, see
    /// <see cref="BindResolve"/>.
    /// </para>
    /// <para>
    /// A property whose path spells an operator is bound with an underscore after it, so a model holding a
    /// property called Contains resolves it as "Contains_" rather than refusing the whole model, see
    /// <see cref="KeyFor"/>. Only a top level property can collide, a nested "Lair.Contains" being one word to
    /// the tokenizer and not the operator.
    /// </para>
    /// <para>
    /// Three things about the walk are worth knowing before you trust the result:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// It does not descend into a struct, so DateTime.Year is not reached this way and still has to be bound by
    /// hand, see <see cref="BindProperty{TProperty}(Expression{Func{T, TProperty}}, string[], string?)"/>. A
    /// collection is descended as the class it is rather than as its element type, so filtering into one is no
    /// more possible here than it is anywhere else in this library, and what you get from one is Count and
    /// Capacity, which a provider may well refuse to translate.
    /// </description></item>
    /// <item><description>
    /// A type already open on the path is not entered again, so a model that refers back to itself terminates
    /// rather than multiplying. Two properties of the same type are both expanded; the same type twice down one
    /// chain is not, so "Parent.Parent" is never resolved.
    /// </description></item>
    /// <item><description>
    /// The cycle guard bounds depth, not width. A wide model still grows with
    /// <paramref name="maxDepth"/> a level at a time, which is why the default is low rather than the limit.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="maxDepth">
    /// how many levels below the entity to reach, so 0 for its own properties and nothing nested, 1 for their
    /// properties as well. Defaults to 1; bounded to [0, 16] rather than refused, so a larger number is quietly
    /// the limit
    /// </param>
    /// <param name="settings">
    /// [OPT] what to leave out; null leaves out
    /// nothing but does not expand a string into its Length
    /// </param>
    /// <returns>the requests, in path order, ready for <see cref="BindProperties"/></returns>
    /// <exception cref="WeequeryException">a resolved path does not make a valid key</exception>
    public static IReadOnlyList<BindingRequest> ResolveBindables(int maxDepth = 1, BindingResolutionSettings? settings = null)
    {
        maxDepth = Math.Min(Math.Max(maxDepth, 0), 16); // bound to [0,16]

        // A caller who named no settings gets the standard ones. Handled here rather than in the copy
        // constructor, which is for copying something.
        settings = (settings is null) ? BindingResolutionSettings.Standard : new(settings);

        return ResolveBindables(new List<BindingRequest>(), typeof(T), 0, maxDepth, "", settings, new HashSet<Type>());
    }

    /// <summary>
    /// Bind every readable property this entity reaches, as <see cref="ResolveBindables(int, BindingResolutionSettings)"/> resolves them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read the warning on <see cref="ResolveBindables(int, BindingResolutionSettings)"/> first.</b> This is the allow-list saying yes to
    /// everything, which is a decision rather than a shortcut.
    /// </para>
    /// <para>
    /// Adds to whatever is already bound rather than replacing it, so a key resolved here that a
    /// <see cref="BindProperty(string, string?)"/> call already claimed is a duplicate and is refused, see
    /// <see cref="BindProperties"/>. Resolve first and <see cref="RemoveBinding"/> what you do not want, or bind
    /// by hand and do not call this.
    /// </para>
    /// </remarks>
    /// <param name="maxDepth">
    /// how many levels below the entity to reach. Defaults to 1, and bounded to [0, 16]; a deep model is worth
    /// resolving once and looking at before you raise it
    /// </param>
    /// <param name="settings">
    /// [OPT] what to leave out; null leaves nothing out, but does not expand a string into its Length
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a resolved path does not make a valid key, or two bindings claim one key</exception>
    public Inquiry<T> BindResolve(int maxDepth = 1, BindingResolutionSettings? settings = null)
    {
        var reqs = ResolveBindables(maxDepth, settings);

        BindProperties(reqs);

        return this;
    }

    /// <summary>
    /// Take a binding back off this Inquiry, so the key stops being answerable.
    /// </summary>
    /// <remarks>
    /// The pairing this exists for is <see cref="BindResolve"/>: bind everything, then subtract the handful you
    /// did not mean, for a model where that is shorter than naming the ones you did. Keys are matched without
    /// regard to case, as they are everywhere else, and removing one that was never bound is a no-op rather than
    /// an error, since the state afterwards is the state that was asked for either way.
    /// </remarks>
    /// <param name="key">the key to stop answering, which must not be null or empty</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the key is null or empty</exception>
    public Inquiry<T> RemoveBinding(string key)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);

        Bindings.Remove(key);

        return this;
    }

    /// <summary>
    /// Add a condition that will be applied to the query when built. Will be AND'ed with any other root conditions
    /// </summary>
    /// <param name="condition"></param>
    /// <returns></returns>
    public Inquiry<T> ApplyCondition(ICondition? condition)
    {
        if (condition is null) { return this; }

        Conditions.Add(condition);

        return this;
    }

    /// <summary>
    /// Parse a query string and add the condition it describes, to be applied when built. Will be AND'ed with any
    /// other root conditions
    /// </summary>
    /// <param name="query">eg. "(Pay &gt; 10000) AND NOT (Name StartsWith 'Temp')"</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to accept only the one spelling of each operator, so a caller sending
    /// <c>&amp;&amp;</c> or <c>IS NULL</c> is refused and told what to write. Null, the default, accepts every
    /// spelling, which is what this has always done. See <see cref="ConditionFunctions.ParseQuery"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the query is malformed, see <see cref="ConditionFunctions.ParseQuery"/></exception>
    public Inquiry<T> ApplyCondition(string query, QueryStyle? style = null)
    {
        var condition = ConditionFunctions.ParseQuery(query, style);
        if (condition is null) { return this; }

        Conditions.Add(condition);

        return this;
    }

    /// <summary>
    /// Add conditions that will be applied to the query when built, if more than one is provided, they will be wrapped in an AND statement
    /// </summary>
    /// <param name="conditions">null, or none, is a NOP. A null element will cause an Exception</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">one of the conditions is null</exception>
    public Inquiry<T> ApplyConditions(IEnumerable<ICondition>? conditions)
    {
        if (conditions is null) { return this; }

        int index = 0;
        foreach (var condition in conditions)
        {
            if (condition is null) { throw new WeequeryException($"{nameof(conditions)}[{index}] is null"); }

            Conditions.Add(condition);
            index++;
        }

        return this;
    }

    /// <summary>
    /// Add sort that will be applied to the query when built, sorts will apply in order given
    /// </summary>
    /// <param name="sort"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public Inquiry<T> ApplySort(Sort? sort)
    {
        if (sort is null) { return this; }

        WeequeryException.ThrowIfNullOrEmpty(sort.Field, $"{nameof(sort)}.{nameof(Sort.Field)}");

        Sorts.Add(sort);

        return this;
    }

    /// <summary>
    /// Parse a sort clause and add the sorts it describes, to be applied when built. They apply in the order
    /// written, each breaking ties in the one before.
    /// </summary>
    /// <remarks>
    /// The clause is a comma separated list of fields, each optionally followed by a direction, and may begin
    /// with ORDER BY. See <see cref="Sort.Parse"/> for the whole of it.
    /// <para>
    /// <paramref name="defaultSort"/> is worth supplying wherever the query is paged, since a page of an
    /// unordered query holds arbitrary rows, see <see cref="ApplyPagination"/>.
    /// </para>
    /// </remarks>
    /// <param name="sortString">eg. "Pay DESC, Name". Null, empty or whitespace takes <paramref name="defaultSort"/></param>
    /// <param name="defaultSort">what to sort by when the caller asked for nothing; null, or none, is a NOP</param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to take only the one word OrderBy prefix, refusing ORDER BY. Null, the
    /// default, takes both. See <see cref="Sort.Parse"/>
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the clause is malformed, see <see cref="Sort.Parse"/></exception>
    public Inquiry<T> ApplySorts(string? sortString, IEnumerable<Sort>? defaultSort = null, QueryStyle? style = null)
    {
        return ApplySorts(Sort.Parse(sortString, defaultSort, style));
    }

    /// <summary>
    /// Add sorts that will be applied to the query when built, sorts will apply in order given
    /// </summary>
    /// <param name="sorts">null, or none, is a NOP. A null element will cause an Exception</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// one of the sorts is null, or names no field.</exception>
    public Inquiry<T> ApplySorts(IEnumerable<Sort>? sorts)
    {
        if (sorts is null) { return this; }

        int index = 0;
        foreach (var sort in sorts)
        {
            if (sort is null) { throw new WeequeryException($"{nameof(sorts)}[{index}] is null"); }

            WeequeryException.ThrowIfNullOrEmpty(sort.Field, $"{nameof(sorts)}[{index}].{nameof(Sort.Field)}");

            Sorts.Add(sort);
            index++;
        }

        return this;
    }

    /// <summary>
    /// Apply paging that will be applied to the query when built
    /// </summary>
    /// <remarks>
    /// <para>
    /// Paging without a unique sort applied will yield undefined output
    /// </para>
    /// </remarks>
    /// <param name="pageSize">rows per page, must be &gt; 0</param>
    /// <param name="page">zero based page index, must be &gt;= 0</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// either argument is out of range, or combining would cause an integer overflow
    /// </exception>
    public Inquiry<T> ApplyPagination(int pageSize, int page)
    {
        if (pageSize <= 0) { throw new WeequeryException($"{nameof(pageSize)} must be > 0"); }
        if (page < 0) { throw new WeequeryException($"{nameof(page)} must be >= 0"); }

        long skip = (long)pageSize * page;
        if (skip > int.MaxValue)
        {
            throw new WeequeryException($"{nameof(pageSize)} {pageSize} * {nameof(page)} {page} exceeds {int.MaxValue}");
        }

        PageSize = pageSize;
        Page = page;

        return this;
    }

    /// <summary>
    /// The predicate for one condition, bounded where it is this process that will run it.
    /// </summary>
    /// <remarks>
    /// Only <see cref="Operator.IsMatch"/> cares, and only because the overload carrying a timeout is not one a
    /// provider translates, see <see cref="RegexTimeout"/>. A query over an in-memory sequence is evaluated here,
    /// so the bound applies; one over a provider is the database's to run under its own limits.
    /// </remarks>
    /// <param name="condition"></param>
    /// <returns></returns>
    private Expression<Func<T, bool>> Predicate(ICondition condition)
    {
        var predicate = ExpressionBuilder.BuildExpression(Bindings, condition, Collections);

        // LINQ to Objects, which is what AsQueryable over a list gives. Anything else is a provider that will be
        // handed the expression rather than running it here.
        return (Query.Provider is EnumerableQuery) ? RegexTimeout.Apply(predicate) : predicate;
    }

    /// <summary>
    /// The wrapped IQueryable with every condition applied, and nothing else.
    /// </summary>
    /// <remarks>
    /// The rows the caller's filter matched, before any ordering is imposed or any window taken of them. This is
    /// what <see cref="PagedQuery{T}.Matches"/> hands back to be counted.
    /// </remarks>
    /// <returns></returns>
    private IQueryable<T> Filtered()
    {
        switch (Conditions.Count)
        {
            case 0:
                return Query;

            case 1:
                return Query.Where(Predicate(Conditions.First()));

            default:
                // If >1 root condition was provided, wrap all root conditions inside an AND condition
                return Query.Where(Predicate(new ConjunctionCondition(Operator.And, Conditions)));
        }
    }

    /// <summary>
    /// The query with every sort applied, in the order they were given, each breaking ties in the one before.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// a sort names a field no binding claimed, a constant, or something with no ordering of its own
    /// </exception>
    private IQueryable<T> Sorted(IQueryable<T> query)
    {
        // Once the query has been sorted once, subsequent sorts must chain with ThenBy rather than restart with OrderBy
        bool alreadySorted = false;
        foreach (var sort in Sorts)
        {
            var binding = BindingLookup.Resolve(Bindings, sort.Field);

            // The same for every row, so there is nothing here to put in order
            if (binding.IsConstant)
            {
                throw new WeequeryException($"Cannot sort on '{sort.Field}', it is a constant");
            }

            // Refused here rather than left to the comparer
            if (!binding.IsOrderable)
            {
                throw new WeequeryException($"Cannot sort on '{sort.Field}', {binding.PropertyType.Name} has no ordering");
            }

            // Sort on the accessor's own type, not the unwrapped one, otherwise a Nullable<> property cannot
            // satisfy the Func<T, TKey> the sort methods want. Nullable<> keys sort fine, nulls first.
            Type keyType = binding.PropertyType;
            Expression key = binding.Accessor;

            if (binding.RequiresLinkCheck)
            {
                // The path steps through something that may not be there, so reading the key is only safe behind
                // the same guard a comparison gets. A row with a missing link has no key, which is a null, so a
                // value typed key has to be widened to hold one. Those rows sort first, as nulls do.
                keyType = ((keyType.IsValueType) && (!binding.PropertyIsWrappedByNullable)) ? typeof(Nullable<>).MakeGenericType(keyType) : keyType;

                Expression found = (keyType == binding.PropertyType) ? binding.Accessor : Expression.Convert(binding.Accessor, keyType);

                key = Expression.Condition(binding.LinkNotNullCheck, found, Expression.Constant(null, keyType));
            }

            var clause = SortMethods.For(sort.Direction, alreadySorted, typeof(T), keyType);

            // turn the binding accessor into something usable for the call
            var selector = Expression.Lambda(clause.SelectorType, key, SharedBindingParameter);

            // Add the call to the query's own expression and let the provider make a query of it, which is what
            // Queryable.OrderBy does with the arguments it is handed. Doing it here rather than calling that
            // through reflection is the same tree by the time the provider sees it, without the invoke.
            query = query.Provider.CreateQuery<T>(Expression.Call(null, clause.Method, query.Expression, Expression.Quote(selector)));

            alreadySorted = true;
        }

        return query;
    }

    /// <summary>
    /// The query narrowed to the requested page, or as it stands where no paging was asked for.
    /// </summary>
    /// <param name="query"></param>
    /// <returns></returns>
    private IQueryable<T> Windowed(IQueryable<T> query)
    {
        return (PageSize > 0) ? query.Skip(PageSize * Page).Take(PageSize) : query;
    }

    /// <summary>
    /// Apply all conditions, sorts, paging, etc to the wrapped IQueryable and return it
    /// </summary>
    /// <returns></returns>
    public IQueryable<T> Build()
    {
        return Windowed(Sorted(Filtered()));
    }

    /// <summary>
    /// Apply everything as <see cref="Build"/> does, and hand back that query together with the one that counts
    /// what the page is a page of.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the caller that has to answer "showing 21 to 40 of 387". The 387 is not something a page can be asked
    /// for — it is the size of the filtered set the window was taken from — so it is a second query over the same
    /// conditions, and this builds it alongside the first.
    /// <code>
    /// var (page, matches) = query.WithWeequery()
    ///     .BindProperties(MinionBindings)
    ///     .ApplyCondition(request.Filter)
    ///     .ApplySorts(request.Sort, DefaultSort)
    ///     .ApplyPagination(request.PageSize, request.Page)
    ///     .BuildPaged();
    ///
    /// var total = await matches.CountAsync();
    /// var rows  = await page.ToListAsync();
    /// </code>
    /// </para>
    /// <para>
    /// <b>Neither query has run.</b> Counting is left to the caller rather than done here, for two reasons. It is
    /// a database round trip, and the method that makes it without blocking a thread is
    /// <c>CountAsync</c>, which belongs to Entity Framework Core and not to this library — Weequery takes no
    /// dependency on whatever is going to execute the query, and doing the count for you would mean either
    /// taking one or calling the synchronous <c>Count</c> in code that ought to be awaiting. It also stays true
    /// to what <see cref="Build"/> promises, which is a query and no execution, so both halves compose with
    /// whatever else you had planned.
    /// </para>
    /// <para>
    /// Count <see cref="PagedQuery{T}.Matches"/> and not <see cref="PagedQuery{T}.Page"/>: the page is windowed,
    /// so counting it gives the size of the page, which you already know.
    /// </para>
    /// <para>
    /// Where <see cref="ApplyPagination"/> was never called there is no window, the page is the whole filtered
    /// result, and the count agrees with its length. That is not an error, but it is a round trip asking a
    /// question the rows already answer.
    /// </para>
    /// </remarks>
    /// <returns>the page, and the query counting everything the conditions matched; never null, neither half null</returns>
    /// <exception cref="WeequeryException">
    /// whatever <see cref="Build"/> would throw, and at the same point: the conditions and sorts are resolved
    /// against the bindings here, not when either query is enumerated
    /// </exception>
    public PagedQuery<T> BuildPaged()
    {
        // Shared, so the count is over exactly the rows the page was taken from and cannot drift from it
        var matches = Filtered();

        return new PagedQuery<T>(Windowed(Sorted(matches)), matches);
    }

    // FIXME - BuildElasticsearch()  ???
    // FIXME - BuildOData() ???

    /// <summary>
    /// Build the predicate for a condition without needing an IQueryable, for use with Where, Any and friends.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Where the resulting expression is evaluated changes what the substring operators match. Handed to EF Core
    /// it becomes SQL and the column's collation applies; run against an in-memory collection it uses the
    /// framework's string methods, where StartsWith and EndsWith are culture sensitive and Contains is ordinal.
    /// See the remarks on <see cref="Operator"/> for the detail and for how to get agreement between the two.
    /// </para>
    /// <para>
    /// Every predicate built for one entity type is built over the same parameter, which is what lets the bindings
    /// be resolved once and reused. Independent predicates do not care, but a predicate from here nested inside
    /// another over the same type — a predicate over Minion used inside "minion =&gt; minion.Peers.Any(...)", say —
    /// would have the inner parameter shadow the outer, so the inner test would read the inner element. Build the
    /// outer lambda by hand around this one, rather than combining two of these.
    /// </para>
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public static Expression<Func<T, bool>> BuildExpression(IEnumerable<BindingRequest> bindingRequests, ICondition condition)
    {
        WeequeryException.ThrowIfNull(bindingRequests);
        WeequeryException.ThrowIfNull(condition);

        return ExpressionBuilder.BuildExpression(BindingsFor(bindingRequests), condition);
    }

    /// <summary>
    /// The binding sets built for this entity type, keyed by the requests that produced them.
    /// </summary>
    private static readonly ConcurrentDictionary<string, Dictionary<string, Binding<T>>> BindingSets = new();

    /// <summary>
    /// How many distinct binding sets to hold. Sets come from code, so an application has a handful and this is
    /// never reached; the cap is only here so that a caller composing sets dynamically cannot grow the cache
    /// without bound. Past it, bindings are built per call.
    /// </summary>
    private const int MaxCachedBindingSets = 64;

    /// <summary>
    /// The bindings for a set of requests, built once and kept, see <see cref="BindingSets"/>.
    /// </summary>
    /// <param name="bindingRequests"></param>
    /// <returns>a lookup that must be treated as read only, since it is shared</returns>
    /// <exception cref="WeequeryException">a request names a property that cannot be bound, or two claim one key</exception>
    private static Dictionary<string, Binding<T>> BindingsFor(IEnumerable<BindingRequest> bindingRequests)
    {
        // Read once: the requests may be a lazy sequence, and the key has to describe the same set that gets built
        var requests = (bindingRequests as IReadOnlyList<BindingRequest>) ?? bindingRequests.ToList();

        var key = CacheKey(requests);
        if (BindingSets.TryGetValue(key, out var cached)) { return cached; }

        // Against the shared parameter, so these compose with anything else bound for this type
        Dictionary<string, Binding<T>> bindings = BindingLookup.Create<T>();
        foreach (var bindingDefinition in requests)
        {
            Binding<T>.Create(SharedBindingParameter, bindingDefinition.PropertyPath, bindings, bindingDefinition.Key);
        }

        // Two threads meeting on the same new set both build one, and either will do
        if (BindingSets.Count < MaxCachedBindingSets) { BindingSets.TryAdd(key, bindings); }

        return bindings;
    }

    /// <summary>
    /// Describes a set of requests exactly, so two sets share an entry only when they would build the same
    /// bindings. The separators cannot appear in a path or a key, both of which are SQL names, dotted for a path.
    /// </summary>
    /// <param name="requests"></param>
    /// <returns></returns>
    private static string CacheKey(IReadOnlyList<BindingRequest> requests)
    {
        StringBuilder builder = new();

        foreach (var request in requests)
        {
            builder.Append(request.PropertyPath).Append('>').Append(request.Key).Append('|');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Compile a condition to a plain delegate, for filtering objects already in memory.
    /// </summary>
    /// <remarks>
    /// This is always in-memory evaluation, so the substring operators follow the framework's string comparison
    /// rules rather than any database collation: StartsWith and EndsWith are culture sensitive, against
    /// CultureInfo.CurrentCulture, while Contains is ordinal. A condition run through here can therefore match a
    /// different set of items than the same condition run against a database. See the remarks on
    /// <see cref="Operator"/>.
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <param name="condition"></param>
    /// <returns></returns>
    public static Func<T, bool> BuildDelegate(IEnumerable<BindingRequest> bindingRequests, ICondition condition)
    {
        // Nothing is going to translate this one, so an IsMatch in it is bounded by MatchTimeout
        return RegexTimeout.Apply(BuildExpression(bindingRequests, condition)).Compile();
    }
}