using System.Linq.Expressions;

namespace Weequery.Interfaces;

internal interface IBinding
{
    /// <summary>
    /// Note: If this is a nested property, this will be X.Y.Z, instead of the direct property name
    /// </summary>
    public string PropertyPath { get; }

    /// <summary>
    /// Expression to get the property value, given an instance of TClass
    /// </summary>
    public MemberExpression Accessor { get; }

    /// <summary>
    /// Type returned by Accessor
    /// </summary>
    public Type PropertyType { get; }

    /// <summary>
    /// If the type returned by Accessor is a nullable type
    /// </summary>
    public bool AccessorIsNullable { get; }

    /// <summary>
    /// If the type returned by Accessor is contained is a Nullable
    /// </summary>
    public bool PropertyIsWrappedByNullable { get; }

    /// <summary>
    /// Simplify creating expressions against Nullable properties, steps through .Value if present
    /// </summary>
    public Expression UnwrappedAccessor { get; }

    /// <summary>
    /// If the type returned by Accessor is an Enum
    /// </summary>
    public bool UnwrappedPropertyTypeIsEnum { get; }

    /// <summary>
    /// Typically the same as AccessorType, if .Accessor returns a Nullable, this will be the underlying type, so int? => int
    /// </summary>
    public Type UnwrappedPropertyType { get; }

    /// <summary>
    /// Input expression, this will provide the instance of TClass being provided
    /// </summary>
    public ParameterExpression Parameter { get; }

}
