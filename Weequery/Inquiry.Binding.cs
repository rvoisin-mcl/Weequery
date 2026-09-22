using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using Weequery.Bindings;

namespace Weequery;

// The declarations: which properties a caller may filter, sort and project on, and nothing else. Each one
// returns a new Inquiry rather than changing this one, see Copy.
public partial class Inquiry<T> where T : class
{
    /// <summary>
    /// Bind the property indicated by the selector func, if a key is not provided, it will be bound as the property path
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    /// <param name="use">[OPT] what the binding may be used for, all three by default, see <see cref="BindingUse"/></param>
    /// <param name="convert">[OPT] a normalisation applied to its values, see <see cref="ValueConverter"/></param>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Inquiry<T> BindProperty<TProperty>(Expression<Func<T, TProperty>> selector, string? key = null, BindingUse use = BindingUse.All, ValueConverter? convert = null)
    {
        var next = Copy();

        Binding<T>.Create(SharedBindingParameter, selector, next.Bindings, key, use, convert);

        return next.RefuseDuplicateKeys();
    }

    /// <summary>
    /// Bind the property reached by following the selector and then the segments after it, for a path a selector
    /// cannot write on its own. If a key is not provided, the full path is used.
    /// </summary>
    /// <remarks>
    /// The case this exists for is a path through a <see cref="Nullable{T}"/>: C# will not compile
    /// <c>(x) =&gt; x.BirthDate.Year</c> against a <c>DateTime?</c>, since a Nullable exposes only its own members,
    /// and writing <c>(x) =&gt; x.BirthDate!.Value.Year</c> instead unwraps rather than reaches through, binding a
    /// plain int with no null of its own. Selecting BirthDate and naming "Year" as a segment binds
    /// "BirthDate.Year" exactly as <see cref="BindProperty(string, string?, BindingUse, ValueConverter)"/> would, while the compiler still
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
    /// <param name="use">[OPT] what the binding may be used for, all three by default, see <see cref="BindingUse"/></param>
    /// <param name="convert">[OPT] a normalisation applied to its values, see <see cref="ValueConverter"/></param>
    /// <exception cref="WeequeryException"></exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Inquiry<T> BindProperty<TProperty>(Expression<Func<T, TProperty>> selector, string[] segments, string? key = null, BindingUse use = BindingUse.All, ValueConverter? convert = null)
    {
        var next = Copy();

        Binding<T>.Create(SharedBindingParameter, selector, segments, next.Bindings, key, use, convert);

        return next.RefuseDuplicateKeys();
    }

    /// <summary>
    /// Bind a collection, and declare what may be asked about one of its elements, so a caller can ask if
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
    /// <b>The inside is its own allow-list.</b> Properties bound within the collection are not visible outside it
    /// </para>
    /// <para>
    /// The collection is only bound for the specified tests, it cannot be used for direct comparisions unless it
    /// is also bound with <see cref="BindProperty{TProperty}(Expression{Func{T, TProperty}}, string?, BindingUse, ValueConverter)"/> 
    /// under a different key.
    /// </para>
    /// <para>
    /// How EF Core chooses to interpret this or not is up to the provider. A quantifier becomes Any or All over
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
    /// the key is invalid, is already in use, the property is not a collection, or nothing was bound inside
    /// </exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
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

        if (Bindings.ContainsKey(key) || Collections.ContainsKey(key))
        {
            throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{key}'");
        }

        var collection = Binding<T>.Create(SharedBindingParameter, selector, bindings: null);

        var inner = new CollectionBindingSet<TElement>();
        configure(inner);

        if (inner.Count == 0)
        {
            throw new WeequeryException(WeequeryError.BindingInvalid, $"Nothing was bound inside '{key}', so no condition could be written about one of its elements");
        }

        var next = Copy();

        next.Collections[key] = new CollectionBinding<T, TElement>(key, collection, inner.Bindings);

        return next;
    }

    /// <summary>
    /// Refuse a property binding if a collection is using the same key
    /// </summary>
    /// <returns>the copy it was called on, so it can be returned from the binding call</returns>
    /// <exception cref="WeequeryException">a key now names both a property and a collection</exception>
    private Inquiry<T> RefuseDuplicateKeys()
    {
        if (Collections.Count == 0) { return this; }

        foreach (var key in Collections.Keys)
        {
            if (Bindings.Remove(key))
            {
                throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{key}', which is bound as a collection");
            }
        }

        return this;
    }

    /// <summary>
    /// Bind a constant value rather than a property.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The caller writes "Pay &gt; [Threshold]" and the application decides what
    /// Threshold is, per request, per tenant, or per anything else it knows and the caller does not.
    /// <code>
    /// .BindConstant("Threshold", payThreshold)
    /// .ApplyCondition("Pay &gt; [Threshold]")
    /// </code>
    /// </para>
    /// <para>
    /// As it is a constant value, it cannot be used to sort on
    /// </para>
    /// </remarks>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="key">the name a caller refers to it by</param>
    /// <param name="value">must not be null: a constant stands for a value, so it needs one</param>
    /// <param name="use">[OPT] what the binding may be used for, all three by default, see <see cref="BindingUse"/></param>
    /// <param name="convert">[OPT] a normalisation applied to its value, see <see cref="ValueConverter"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Inquiry<T> BindConstant<TValue>(string key, TValue value, BindingUse use = BindingUse.Test | BindingUse.Projection, ValueConverter? convert = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        var next = Copy();

        Binding<T>.CreateConstant(SharedBindingParameter, key, value, next.Bindings, use, convert);

        return next.RefuseDuplicateKeys();
    }

    /// <summary>
    /// Bind the property with the provided path, if a key is not provided, it will be bound as the property path
    /// </summary>
    /// <param name="path"></param>
    /// <param name="key"></param>
    /// <returns></returns>
    /// <param name="use">[OPT] what the binding may be used for, all three by default, see <see cref="BindingUse"/></param>
    /// <param name="convert">[OPT] a normalisation applied to its values, see <see cref="ValueConverter"/></param>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Inquiry<T> BindProperty(string path, string? key = null, BindingUse use = BindingUse.All, ValueConverter? convert = null)
    {
        WeequeryException.ThrowIfNullOrEmpty(path);
        WeequeryException.ThrowIfNotNullButEmpty(key);
        WeequeryException.ThrowIfNotBindingKey(key);

        var next = Copy();

        Binding<T>.Create(SharedBindingParameter, path, next.Bindings, key, use, convert);

        return next.RefuseDuplicateKeys();
    }

    /// <summary>
    /// Bind the properties in the list, if a key for one is not provided, it will be bound as the property path
    /// </summary>
    /// <remarks>
    /// <para>
    /// A set of requests can be resolved once for the process and cached, see <see cref="BindingSetCache{T}"/>
    /// </para>
    /// <para>
    /// A request for a key already bound to the <b>same property</b> is merged rather than refused: the uses are
    /// added together and a converter either side named is kept, so binding the same set twice, or a broad set
    /// and then a narrow one, adds rather than colliding. A key standing for a <i>different</i> property is a
    /// conflict and is refused.
    /// </para>
    /// </remarks>
    /// <param name="bindingRequests"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a key points to a different property, or attempts to merge distinct converters</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Inquiry<T> BindProperties(IEnumerable<BindingRequest> bindingRequests)
    {
        WeequeryException.ThrowIfNull(bindingRequests);

        var next = Copy();

        foreach (var binding in BindingSetCache<T>.For(bindingRequests, SharedBindingParameter))
        {
            if (next.Bindings.TryGetValue(binding.Key, out var existing))
            {
                if (!Binding<T>.IsSameBinding(existing, binding.Value)) { throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{binding.Key}'"); }

                next.Bindings[binding.Key] = Binding<T>.Merged(existing, binding.Value, binding.Key);

                continue;
            }

            next.Bindings[binding.Key] = binding.Value;
        }

        return next.RefuseDuplicateKeys();
    }

    /// <summary>
    /// Build a binding request for every readable property a type reaches, keyed by its path.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An allow-list, where the list is <b>ALL OF IT</b> Every property name on the type, and on every type
    /// it reaches, goes on the wire for a caller to read and filter by. Use
    /// <see cref="BindingResolutionSettings"/> to determine what types or paths should be skipped.
    /// </para>
    /// <para>
    /// Paths are distinct by construction, so nothing here collides with anything else here; but can still
    /// collide with a binding already made by hand, see <see cref="BindResolve"/>.
    /// </para>
    /// <para>
    /// A property whose path conflicts with an query keyword is bound with an underscore after it, so a model holding a
    /// property called Contains resolves it as "Contains_" rather than refusing the whole model, see
    /// <see cref="BindingResolver.KeyFor"/>.
    /// </para>
    /// <para>
    /// Things to know:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// It will not descend into a struct, so DateTime.Year is not reached this way and must be bound by
    /// hand, see <see cref="BindProperty{TProperty}(Expression{Func{T, TProperty}}, string[], string?, BindingUse, ValueConverter)"/>. A
    /// collection is descended as the class it is rather than as its element type, and what you get from one is Count and
    /// Capacity, which a provider may well refuse to translate.
    /// </description></item>
    /// <item><description>
    /// A type already above it the path will not be revisited, so Parent.Child.Parent will not resolve
    /// </description></item>
    /// <item><description>
    /// The cycle guard bounds depth, not width.
    /// </description></item>
    /// </list>
    /// </remarks>
    /// <param name="maxDepth">
    /// how many levels below the entity to reach, 0 for immediate properties, 1 for their
    /// properties as well. Defaults to 1; bounded to [0, 16]
    /// </param>
    /// <param name="settings">
    /// [OPT] what to leave out; by default only string properties will not not be collected
    /// </param>
    /// <param name="use">[OPT] what the binding may be used for, everything by default, see <see cref="BindingUse"/></param>
    /// <returns>the requests, in path order, ready for <see cref="BindProperties"/></returns>
    /// <exception cref="WeequeryException">a resolved path does not make a valid key</exception>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Per entity type is the point: the bound is one per T and the builders close over T's bindings, so Inquiry<T> is where a caller already is when it needs them.")]
    public static IReadOnlyList<BindingRequest> ResolveBindables(int maxDepth = 1, BindingResolutionSettings? settings = null, BindingUse use = BindingUse.All)
    {
        maxDepth = Math.Min(Math.Max(maxDepth, 0), 16); // bound to [0,16]
        settings = (settings is null) ? BindingResolutionSettings.Default : new(settings); // use defaults if nothing provided

        var resolved = BindingResolver.ResolveBindables(new List<BindingRequest>(), typeof(T), 0, maxDepth, "", settings, new HashSet<Type>());

        return (use == BindingUse.All) ? resolved : [.. resolved.Select(request => new BindingRequest(request.PropertyPath, request.Key, use))];
    }

    /// <summary>
    /// Bind every readable property this entity reaches, as <see cref="ResolveBindables(int, BindingResolutionSettings, BindingUse)"/> resolves them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read the warning on <see cref="ResolveBindables(int, BindingResolutionSettings, BindingUse)"/> first.</b> With default settings, this will bind everything.
    /// </para>
    /// <para>
    /// Adds to whatever is already bound rather than replacing it. A key this resolves that a
    /// <see cref="BindProperty(string, string?, BindingUse, ValueConverter)"/> call already claimed for the
    /// <b>same property</b> is merged rather than refused: the uses are added, and a converter either
    /// side named is kept (or both, if the converter is the same instance). Only a key standing for a 
    /// <i>different</i> property is refused, see <see cref="BindProperties"/>.
    /// </para>
    /// </remarks>
    /// <param name="maxDepth">
    /// how many levels below the entity to reach. Defaults to 1, and bounded to [0, 16]
    /// </param>
    /// <param name="settings">
    /// [OPT] what to leave out; null leaves nothing out, but will not bind string properties
    /// </param>
    /// <param name="use">[OPT] what the binding may be used for, everything by default, see <see cref="BindingUse"/></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a resolved path does not make a valid key, or two bindings claim one key</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Inquiry<T> BindResolve(int maxDepth = 1, BindingResolutionSettings? settings = null, BindingUse use = BindingUse.All)
    {
        var reqs = ResolveBindables(maxDepth, settings, use);

        return BindProperties(reqs);
    }

    /// <summary>
    /// Take a binding back off this Inquiry, or narrow the scope of its use.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The pairing this exists for is <see cref="BindResolve"/>: bind everything, then subtract the handful you
    /// do not want.
    /// </para>
    /// <para>
    /// <b>Naming a use removes the use rather than the whole binding</b>, which is the counterweight to a
    /// binding's use only ever widening, see <see cref="Binding{TClass}.Widened"/>
    /// <code>
    /// .BindResolve()
    /// .RemoveBinding("Pay", BindingUse.Condition | BindingUse.Sort)   // still readable, no longer testable
    /// .RemoveBinding("PasswordHash")                                  // gone entirely
    /// </code>
    /// </para>
    /// <para>
    /// Bound collections are only ever testable, so any Remove against one will remove it entirely
    /// </para>
    /// </remarks>
    /// <param name="key">the binding name</param>
    /// <param name="use">
    /// [OPT] what to stop it being used for, <see cref="BindingUse.All"/> by default
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the key is null or empty</exception>
    public Inquiry<T> RemoveBinding(string key, BindingUse use = BindingUse.All)
    {
        WeequeryException.ThrowIfNullOrEmpty(key);

        var next = Copy();

        if (next.Bindings.TryGetValue(key, out var existing))
        {
            var narrowed = existing.Narrowed(use);

            // No uses left, remove the binding entirely
            if (narrowed.Use == BindingUse.None)
            {
                next.Bindings.Remove(key);
            }
            else
            {
                next.Bindings[key] = narrowed;
            }
        }

        if (use.HasFlag(BindingUse.Test))
        {
            next.Collections.Remove(key);
        }

        return next;
    }

    /// <summary>
    /// Batch variant <see cref="RemoveBinding(string, BindingUse)"/>
    /// </summary>
    /// <param name="keys">the binding names</param>
    /// <param name="use">
    /// [OPT] what to stop it being used for, <see cref="BindingUse.All"/> by default
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the key is null or empty</exception>
    public Inquiry<T> RemoveBindings(IEnumerable<string> keys, BindingUse use = BindingUse.All)
    {
        WeequeryException.ThrowIfNull(keys);

        if (!keys.Any()) { return this; } // NOP

        var next = Copy();

        foreach (var key in keys)
        {
            if (next.Bindings.TryGetValue(key, out var existing))
            {
                var narrowed = existing.Narrowed(use);

                // No uses left, remove the binding entirely
                if (narrowed.Use == BindingUse.None)
                {
                    next.Bindings.Remove(key);
                }
                else
                {
                    next.Bindings[key] = narrowed;
                }
            }

            if (use.HasFlag(BindingUse.Test))
            {
                next.Collections.Remove(key);
            }
        }

        return next;
    }

}
