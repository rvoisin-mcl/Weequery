using System.Linq.Expressions;

namespace Weequery.Interfaces;

/// <summary>
/// A binding holding one value, which is what an operator can be asked about.
/// </summary>
/// <remarks>
/// <para>
/// Everything here exists to compare the value, and comparing it means seeing past a <see cref="Nullable{T}"/>
/// first: the guard on <see cref="IBinding.NotNullCheck"/> has already decided there is a value, so the
/// comparison wants the unwrapped form rather than the wrapper.
/// </para>
/// <para>
/// A collection is deliberately not one of these. It holds many values where an operator wants one, so there is
/// nothing to unwrap and nothing to compare, and the only thing to be asked of it is a quantifier. Keeping the
/// two apart is what stops a collection reaching the expression builders at all, rather than reaching them and
/// being turned away.
/// </para>
/// </remarks>
internal interface IValueBinding : IBinding
{
    /// <summary>
    /// If the type <see cref="IBinding.Accessor"/> returns is a <see cref="Nullable{T}"/>
    /// </summary>
    bool PropertyIsWrappedByNullable { get; }

    /// <summary>
    /// The accessor with any <see cref="Nullable{T}"/> stepped through, so an expression built from it compares
    /// the value rather than the wrapper
    /// </summary>
    Expression UnwrappedAccessor { get; }

    /// <summary>
    /// If <see cref="UnwrappedPropertyType"/> is an enum, which orders by its values rather than its names
    /// </summary>
    bool UnwrappedPropertyTypeIsEnum { get; }

    /// <summary>
    /// The type <see cref="UnwrappedAccessor"/> returns, so int for an int?
    /// </summary>
    Type UnwrappedPropertyType { get; }
}
