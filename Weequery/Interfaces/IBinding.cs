using System.Linq.Expressions;

namespace Weequery.Interfaces;

/// <summary>
/// What every binding has: where it lives on the entity, how to reach it, and whether reaching it is safe.
/// </summary>
/// <remarks>
/// <para>
/// This is the part a value binding and a collection binding genuinely share. Both name a property by its path,
/// both reach it by an expression off the entity's parameter, and both can sit at the end of a path that runs
/// through something missing, so both have to say when reading is safe. What they do with what they find is
/// where they part company, see <see cref="IValueBinding"/> and <see cref="ICollectionBinding{TClass}"/>.
/// </para>
/// <para>
/// Non-generic on purpose. The entity type is a type argument on the concrete bindings, and the code that only
/// wants to ask a binding about itself has no business knowing it.
/// </para>
/// </remarks>
internal interface IBinding
{
    /// <summary>
    /// Where the property lives on the entity. A nested one is its whole dotted path, "Lair.Capacity" rather
    /// than "Capacity".
    /// </summary>
    string PropertyPath { get; }

    /// <summary>
    /// Expression to reach the property, given an instance of the entity
    /// </summary>
    Expression Accessor { get; }

    /// <summary>
    /// The type <see cref="Accessor"/> returns
    /// </summary>
    Type PropertyType { get; }

    /// <summary>
    /// If the type <see cref="Accessor"/> returns can hold a null
    /// </summary>
    bool AccessorIsNullable { get; }

    /// <summary>
    /// The parameter <see cref="Accessor"/> hangs off, which is shared by every binding for one entity type so
    /// that predicates built from several of them compose
    /// </summary>
    ParameterExpression Parameter { get; }

    /// <summary>
    /// If anything about this binding can be missing: the property itself, or a link the path passed through.
    /// False means <see cref="Accessor"/> is safe to read on its own.
    /// </summary>
    bool RequiresNullCheck { get; }

    /// <summary>
    /// True when the property, and every link on the path to it, has a value.
    /// </summary>
    /// <remarks>
    /// The links come first and it short circuits, so this is also what makes the accessor safe to evaluate: a
    /// path through a missing link, or an index past the end of a list, is answered rather than thrown.
    /// </remarks>
    Expression NotNullCheck { get; }
}
