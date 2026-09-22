using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
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

        if (Collections.ContainsKey(key))
        {
            throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{key}'");
        }

        var collection = Binding<T>.Create(SharedBindingParameter, selector, bindings: null);

        // One key may be a property and a collection at once, so long as they are the same property: that is what
        // lets a null test, an index and a quantifier all answer to it. A different property wanting the name is not
        if (Bindings.TryGetValue(key, out var property) && (property.PropertyPath != collection.PropertyPath))
        {
            throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{key}'");
        }

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
    /// Bind a collection and <b>resolve</b> what may be asked about one of its elements, rather than declaring
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The same call as the one taking a configuring action, with the element's allow-list walked out of the
    /// element type by <see cref="ResolveBindables(int, BindingResolutionSettings, BindingUse)"/> instead of
    /// written by hand:
    /// <code>
    /// .BindCollection(minion =&gt; minion.LairAssignments, "Assignments")
    /// .ApplyCondition("Assignments Any (Lair.Name = 'Volcano')")
    /// </code>
    /// </para>
    /// <para>
    /// <b>The depth is counted from the element</b>, exactly as the other one counts from the entity, and it
    /// defaults to the same 1. On a link table that is usually what you want: 0 binds only the element's own
    /// columns, which for a row that exists to join two things is a pair of ids and little else, where 1 reaches
    /// through to the far side and is the second hop of the join.
    /// <code>
    /// .BindCollection(minion =&gt; minion.LairAssignments, "Assignments", 0)   // LairID, MinionID, Lair, Minion
    /// .BindCollection(minion =&gt; minion.LairAssignments, "Assignments")      // ...and Lair.Name, Lair.Capacity
    /// </code>
    /// </para>
    /// <para>
    /// <b>Read the warning on <see cref="ResolveBindables(int, BindingResolutionSettings, BindingUse)"/>, which
    /// applies here twice over.</b> This opens the element type, and at the default depth the one past it, so
    /// everything either of them can reach is nameable inside the quantifier. Resolve it once and read what you
    /// got before you ship it, or declare the inside by hand where the element is anything you would not publish.
    /// </para>
    /// <para>
    /// There is no <see cref="BindingUse"/> here because an element has none: it is tested, and never sorted on
    /// or read back, so the only question is whether it is nameable at all.
    /// </para>
    /// </remarks>
    /// <typeparam name="TElement">what the collection holds</typeparam>
    /// <param name="selector">the collection property</param>
    /// <param name="key">the name a caller writes the quantifier against</param>
    /// <param name="maxDepth">
    /// [OPT] how many levels below the element to reach. Defaults to 1, and bounded to [0, 16]
    /// </param>
    /// <param name="settings">
    /// [OPT] what to leave out, matched against paths inside the element rather than inside the entity
    /// </param>
    /// <returns>a copy carrying the binding</returns>
    /// <exception cref="WeequeryException">
    /// the key is invalid or already in use, the property is not a collection, or the element resolved to
    /// nothing, which is the same refusal declaring an empty set gets
    /// </exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Inquiry<T> BindCollection<TElement>(
        Expression<Func<T, IEnumerable<TElement>?>> selector,
        string key,
        int maxDepth = 1,
        BindingResolutionSettings? settings = null)
        where TElement : class
    {
        // The element is an entity as far as resolution is concerned, so this is the same walk, one level down.
        // An element resolving to nothing falls through to the refusal the declaring form already gives.
        return BindCollection(selector, key, inner => inner.BindProperties(Inquiry<TElement>.ResolveBindables(maxDepth, settings)));
    }

    /// <summary>
    /// Bind a set of collections that resolution found, see <see cref="ResolveBindableCollections"/>.
    /// </summary>
    /// <remarks>
    /// The collection half of <see cref="BindProperties"/>. Each request names a path, the key it answers to and
    /// what may be asked about one of its elements, and the element type is only known at run time, so this is
    /// the one binding call that cannot be written with a type argument.
    /// </remarks>
    /// <param name="requests">what to bind; an empty set binds nothing and is not an error</param>
    /// <returns>a copy carrying them</returns>
    /// <exception cref="WeequeryException">a key is already a collection, or a path does not resolve</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Inquiry<T> BindCollections(IEnumerable<CollectionBindingRequest> requests)
    {
        WeequeryException.ThrowIfNull(requests);

        var next = Copy();

        foreach (var request in requests)
        {
            if (next.Collections.ContainsKey(request.Key))
            {
                throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{request.Key}'");
            }

            // The element type is a Type rather than a type argument here, so the one call needing it closed has
            // to be closed by hand
            typeof(Inquiry<T>)
                .GetMethod(nameof(AddCollection), BindingFlags.NonPublic | BindingFlags.Instance)!
                .MakeGenericMethod(request.ElementType)
                .Invoke(next, [request]);
        }

        return next.RefuseDuplicateKeys();
    }

    /// <summary>
    /// Add one resolved collection in place, which is safe because the caller is holding a copy
    /// </summary>
    /// <typeparam name="TElement">what the collection holds</typeparam>
    /// <param name="request">the path, the key and what an element answers</param>
    /// <exception cref="WeequeryException">the path does not resolve, or nothing was bound inside</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    private void AddCollection<TElement>(CollectionBindingRequest request)
        where TElement : class
    {
        var collection = Binding<T>.Create(SharedBindingParameter, request.PropertyPath, bindings: null);

        var inner = new CollectionBindingSet<TElement>();
        inner.BindProperties(request.Elements);

        if (inner.Count == 0)
        {
            throw new WeequeryException(WeequeryError.BindingInvalid, $"Nothing was bound inside '{request.Key}', so no condition could be written about one of its elements");
        }

        Collections[request.Key] = new CollectionBinding<T, TElement>(request.Key, collection, inner.Bindings);
    }

    /// <summary>
    /// Refuse a property binding that claims a collection's key for a <b>different</b> property
    /// </summary>
    /// <remarks>
    /// <para>
    /// One key may be both, and on a collection it usually is: the same key answers a null test and an index as
    /// a property, and the quantifiers as a collection, because those are three questions about one thing rather
    /// than three things. What is refused is a key standing for two <i>different</i> properties, which is the
    /// same refusal two property bindings would get.
    /// </para>
    /// </remarks>
    /// <returns>the copy it was called on, so it can be returned from the binding call</returns>
    /// <exception cref="WeequeryException">a key names a collection and some other property</exception>
    private Inquiry<T> RefuseDuplicateKeys()
    {
        if (Collections.Count == 0) { return this; }

        foreach (var collection in Collections.Values)
        {
            if (Bindings.TryGetValue(collection.Key, out var property) && (property.PropertyPath != collection.PropertyPath))
            {
                Bindings.Remove(collection.Key);

                throw new WeequeryException(WeequeryError.KeyTaken, $"Binding already exists for '{collection.Key}', which is bound as a collection of '{collection.PropertyPath}'");
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
    /// What resolution would bind as collections, and what it would let a quantifier ask about one element.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The collection half of <see cref="ResolveBindables(int, BindingResolutionSettings, BindingUse)"/>, and
    /// the same warning applies twice over: this opens the element type as well, so every readable property of
    /// everything a collection holds becomes nameable inside the quantifier. Resolve it once, print it, and read
    /// what you got.
    /// <code>
    /// foreach (var found in Inquiry&lt;Minion&gt;.ResolveBindableCollections(collectionDepth: 1))
    /// {
    ///     Console.WriteLine($"{found.Key}: {string.Join(", ", found.Elements.Select(e =&gt; e.Key))}");
    /// }
    /// </code>
    /// </para>
    /// <para>
    /// Every path here also comes back from <see cref="ResolveBindables(int, BindingResolutionSettings, BindingUse)"/>
    /// as an ordinary property, and both are bound: one key answering a null test and an index as a property,
    /// and the quantifiers as a collection.
    /// </para>
    /// </remarks>
    /// <param name="maxDepth">[OPT] how far into the entity to look for collections, bounded to [0,16]</param>
    /// <param name="collectionDepth">[OPT] how far into an element to go, 0 being its own properties, bounded to [0,16]</param>
    /// <param name="settings">[OPT] what to leave out, see <see cref="BindingResolutionSettings"/></param>
    /// <returns>every collection found, in path order; never null</returns>
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    [SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "Per entity type is the point: what is resolved is the collections of T, so Inquiry<T> is where a caller already is when they need them.")]
    public static IReadOnlyList<CollectionBindingRequest> ResolveBindableCollections(int maxDepth = 1, int collectionDepth = 0, BindingResolutionSettings? settings = null)
    {
        List<CollectionBindingRequest> collections = [];

        BindingResolver.ResolveBindables(new List<BindingRequest>(), typeof(T), 0,
            Math.Min(Math.Max(maxDepth, 0), 16), "",
            (settings is null) ? BindingResolutionSettings.Default : new(settings),
            new HashSet<Type>(), collections, Math.Min(Math.Max(collectionDepth, 0), 16));

        return collections;
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
    /// <para>
    /// <b>It descends into collections</b>, which is what makes a quantifier work without declaring one. Every
    /// collection of objects it meets is bound twice over, and both are the same key: as a property, which is
    /// what an index reads one element out of, and as a collection, which is what answers
    /// <c>Assignments Any (...)</c>. Two questions about one thing, under one name.
    /// </para>
    /// <para>
    /// For "has no elements", <c>Assignments None (...)</c> is the question that means it. A quantifier is total
    /// and answers the same for an absent collection as for an empty one, where a null test on the collection
    /// itself would distinguish two things a database will not.
    /// <code>
    /// .BindResolve()                        // Assignments Any (LairID = 5)
    /// .BindResolve(collectionDepth: 1)      // ...and Assignments Any (Lair.Name = 'Volcano')
    /// </code>
    /// </para>
    /// <para>
    /// <b>The element depth defaults to 0, and that is deliberate.</b> It is the element's own properties, which
    /// on a link table is the pair of ids and the two things they point at, and it is where the cost stops being
    /// small: a depth of 1 on an entity with three collections took one model from 18 nameable keys to 124, the
    /// far side of every link table being the whole of another entity. Ask for the depth where you want the
    /// second hop, and read what you got.
    /// </para>
    /// <para>
    /// Not every sequence is one. A <c>List&lt;string&gt;</c>, a dictionary, an array of numbers and a string
    /// stay ordinary bindings, a quantifier naming a property of an element and none of those having one worth
    /// naming. Index those instead.
    /// </para>
    /// <para>
    /// For the property list alone, with nothing entered, bind
    /// <see cref="ResolveBindables(int, BindingResolutionSettings, BindingUse)"/> yourself through
    /// <see cref="BindProperties"/>, which is what this did before it descended.
    /// </para>
    /// </remarks>
    /// <param name="maxDepth">
    /// how many levels below the entity to reach. Defaults to 1, and bounded to [0, 16]
    /// </param>
    /// <param name="settings">
    /// [OPT] what to leave out; null leaves nothing out, but will not bind string properties
    /// </param>
    /// <param name="use">[OPT] what the binding may be used for, everything by default, see <see cref="BindingUse"/></param>
    /// <param name="collectionDepth">
    /// [OPT] how far into a collection element to resolve, 0 being the element own properties. Bounded to [0, 16]
    /// </param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">a resolved path does not make a valid key, or two bindings claim one key</exception>
    [RequiresDynamicCode(AotMessages.RuntimeGenerics)]
    [RequiresUnreferencedCode(AotMessages.BoundByName)]
    public Inquiry<T> BindResolve(int maxDepth = 1, BindingResolutionSettings? settings = null, BindingUse use = BindingUse.All, int collectionDepth = 0)
    {
        var next = BindProperties(ResolveBindables(maxDepth, settings, use));

        var collections = ResolveBindableCollections(maxDepth, collectionDepth, settings);

        return (collections.Count == 0) ? next : next.BindCollections(collections);
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
