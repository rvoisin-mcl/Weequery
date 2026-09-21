using System.Linq.Expressions;
using System.Reflection;
using Weequery.Parsing;

namespace Weequery.Bindings;

// Turning a property path or a selector lambda into an accessor expression: the steps a path breaks into, the
// member lookup each one needs, and the null checks the links along the way carry with them.
internal partial class Binding<TClass>
{
    private static Type GetMemberType(MemberExpression expression)
    {
        switch (expression.Member.MemberType)
        {
            case MemberTypes.Field:
                return ((FieldInfo)expression.Member).FieldType;

            case MemberTypes.Property:
                return ((PropertyInfo)expression.Member).PropertyType;

            case MemberTypes.Event: // would be: ((EventInfo)Accessor.Member).EventHandlerType;
            case MemberTypes.Method: // would be: ((MethodInfo)Accessor.Member).ReturnType;
            default:
                throw new WeequeryException(WeequeryError.BindingInvalid, $"Could not generate member for expression {expression}");
        }
    }

    private record GetPropertyExpressionRecord(Expression Expression, Type ExpressionType, List<Expression> LinkChecks);

    /// <summary>
    /// One step of a binding path: a property name, and the index to read from it if the path named one.
    /// </summary>
    /// <param name="Name">the property</param>
    /// <param name="Index">what to read out of it, or null to take the property itself</param>
    private record PathStep(string Name, string? Index);

    /// <summary>
    /// Split a binding path into its steps, respecting brackets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// "Lair.Capacity" is two steps and "Assignments[0].LairID" also two, the first of them indexed. A . inside
    /// a dictionary index will be understood part of the key and not a path seperator
    /// </para>
    /// </remarks>
    /// <param name="propertyPath"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the brackets do not close, or a step has no name</exception>
    private static List<PathStep> PathSteps(string propertyPath)
    {
        List<PathStep> steps = new();

        var name = new System.Text.StringBuilder();
        string? index = null;

        void Finish()
        {
            if (name.Length == 0) { throw new WeequeryException(WeequeryError.PathInvalid, $"Property path '{propertyPath}' has a empty stop"); } // // Binding..Child

            steps.Add(new(name.ToString(), index));
            name.Clear();
            index = null;
        }

        for (var i = 0; i < propertyPath.Length; i++)
        {
            var ch = propertyPath[i];

            if (ch == '[')
            {
                var close = propertyPath.IndexOf(']', i);
                if (close < 0) { throw new WeequeryException(WeequeryError.PathInvalid, $"Property path '{propertyPath}' has an unclosed '['"); } // Binding[

                if (index is not null) { throw new WeequeryException(WeequeryError.PathInvalid, $"Property path '{propertyPath}' contains multiple indexes in the same stop"); } // Binding[x][y]

                index = propertyPath[(i + 1)..close];
                if (index.Length == 0) { throw new WeequeryException(WeequeryError.PathInvalid, $"Property path '{propertyPath}' has an empty index"); } // Binding[]

                i = close;
                continue;
            }

            if (ch == '.')
            {
                Finish();
                continue;
            }

            if (index is not null) { throw new WeequeryException(WeequeryError.PathInvalid, $"Property path '{propertyPath}' has text after an index"); } // Binding[x]BlahBlah

            name.Append(ch);
        }

        Finish();

        return steps;
    }

    private static bool IsNullable(Type type)
    {
        return type.IsGenericType && (type.GetGenericTypeDefinition() == typeof(Nullable<>));
    }

    /// <summary>
    /// If this type contains a property by this name. Matches how <see cref="Expression.PropertyOrField"/>
    /// </summary>
    private static bool HasMember(Type type, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase | BindingFlags.FlattenHierarchy;

        return (type.GetProperty(name, flags) is not null) || (type.GetField(name, flags) is not null);
    }

    /// <summary>
    /// Given a input and an property path, build a expression chain to return value of the final link
    /// <para>
    /// A path may reach into a Nullable&lt;T&gt;, so "BirthDate.Year" on a DateTime? is built as
    /// "BirthDate.Value.Year". Each nullable link is recorded as it is passed, and
    /// <see cref="NotNullCheck"/> turns those into the guard every operator is built on, so the unwrap is never
    /// reached for a null and the member behaves as a nullable in its own right.
    /// </para>
    /// </summary>
    /// <param name="parameter"></param>
    /// <param name="propertyPath"></param>
    /// <returns></returns>
    private static GetPropertyExpressionRecord GetPropertyExpression(ParameterExpression parameter, string propertyPath)
    {
        WeequeryException.ThrowIfNull(parameter);
        WeequeryException.ThrowIfNullOrEmpty(propertyPath);

        // One step along the path, which is PropertyOrField except when the type is an interface.
        static Expression StepInto(Expression on, string segment)
        {
            if (on.Type.IsInterface && (on.Type.GetProperty(segment) is null))
            {
                foreach (var declaring in on.Type.GetInterfaces())
                {
                    var inherited = declaring.GetProperty(segment);

                    if (inherited is not null) { return Expression.Property(on, inherited); }
                }
            }

            return Expression.PropertyOrField(on, segment);
        }

        // Build member expression from the provided path
        Expression exp = parameter;
        Type expType = typeof(TClass);
        List<Expression> linkChecks = new();

        foreach (var step in PathSteps(propertyPath))
        {
            // A Nullable<T> exposes only its own HasValue and Value, so getting a member of T means going
            // through .Value first: "BirthDate.Year" has to be built as "BirthDate.Value.Year". An explicitly
            // written .Value or .HasValue is left alone, since those are members of the Nullable itself.
            if (IsNullable(exp.Type) && (!HasMember(exp.Type, step.Name)))
            {
                linkChecks.Add(Expression.Property(exp, "HasValue")); // guard the unwrap that follows
                exp = Expression.Property(exp, "Value");
            }
            else if ((exp != parameter) && (!exp.Type.IsValueType))
            {
                linkChecks.Add(Expression.NotEqual(exp, Expression.Constant(null, exp.Type)));
            }

            try
            {
                exp = StepInto(exp, step.Name);
                expType = exp.Type;
            }
            catch (ArgumentException ex)
            {
                throw new WeequeryException(WeequeryError.PathInvalid, $"Could not resolve '{step.Name}' of property path '{propertyPath}' on {exp.Type.Name}", ex);
            }

            // An index in the path reads one element and carries on from it. We will treat the element as a nullable,
            // exactly as one named by a condition does, so a path that indexes outside the collection past the end is a path
            // is a null, and not an exception
            if (step.Index is not null)
            {
                var indexed = IndexInto(exp, exp.Type, step.Index, $"{step.Name}", linkChecks);

                exp = indexed.Access;
                expType = indexed.ElementType;
            }
        }

        // MemberExpression for an a plain old ordinary path
        var memberType = (exp is MemberExpression member) ? GetMemberType(member) : expType;

        return new(exp, memberType, linkChecks);
    }

    private record IndexOfRecord(Expression Source, string Index);

    /// <summary>
    /// An index the compiler wrote into the selector, and what it was taken from.
    /// </summary>
    /// <remarks>
    /// Ther are 3 paths that will lead here, A list or a dictionary indexes through a call
    /// to the indexer's getter, <c>get_Item</c>; an array is its own <see cref="ExpressionType.ArrayIndex"/> node;
    /// and an <see cref="IndexExpression"/> turns up where a tree was built by hand rather than compiled.
    /// <para>
    /// The index must be a constant value
    /// </para>
    /// </remarks>
    /// <param name="node"></param>
    /// <returns>what was indexed and the index as text, or null if there is no index</returns>
    private static IndexOfRecord? IndexOf(Expression? node)
    {
        if (node is MethodCallExpression call
            && (call.Object is not null)
            && (call.Arguments.Count == 1)
            && call.Method.Name.Equals("get_Item", StringComparison.Ordinal))
        {
            return new(call.Object, ConstantIndex(call.Arguments[0], node));
        }

        if ((node is BinaryExpression binary) && (binary.NodeType == ExpressionType.ArrayIndex))
        {
            return new(binary.Left, ConstantIndex(binary.Right, node));
        }

        if (node is IndexExpression indexed && (indexed.Object is not null) && (indexed.Arguments.Count == 1))
        {
            return new(indexed.Object, ConstantIndex(indexed.Arguments[0], node));
        }

        return null;
    }

    /// <summary>
    /// A path from the part a selector could reach and the segments named after it. Will join segments with . unless
    /// the segment is an index
    /// </summary>
    /// <param name="head">the path the selector reached</param>
    /// <param name="segments">the rest, in order</param>
    /// <returns></returns>
    private static string JoinSegments(string head, string[] segments)
    {
        var path = new System.Text.StringBuilder(head);

        foreach (var segment in segments)
        {
            if (!segment.StartsWith('[')) { path.Append('.'); }

            path.Append(segment);
        }

        return path.ToString();
    }

    /// <summary>
    /// The index as a string
    /// </summary>
    /// <param name="argument">what the selector indexed by</param>
    /// <param name="node">the indexing expression it came from</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the index is not a constant</exception>
    private static string ConstantIndex(Expression argument, Expression node)
    {
        if (Unwrap(argument) is not ConstantExpression constant)
        {
            throw new WeequeryException(WeequeryError.PathInvalid, $"'{node}' index is not a constant");
        }

        return ValueFormat.ToInvariantString(constant.Value);
    }

    /// <summary>
    /// Get the path the property represents. <c>(x) =&gt; x.Lair.Capacity</c> will give gives "Lair.Capacity" and
    /// <c>(x) =&gt; x.Slots[0].Weight</c> gives "Slots[0].Weight".
    /// <para>
    /// Read off the member chain rather than out of the lambda's text. Exists primarily for debugging purposes. 
    /// A selector whose property type is not TProperty exactly is wrapped in a conversion, so <c>(x) =&gt; x.Pay</c>
    /// and <c>(x) =&gt; (object)x.Pay</c> print differently while meaning the same path. Stepping over the 
    /// wrappers is easier than recognising them in a string.
    /// </para>
    /// </summary>
    /// <typeparam name="TProperty"></typeparam>
    /// <param name="selector"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the selector is invalid</exception>
    private static string GetPropertyPath<TProperty>(Expression<Func<TClass, TProperty>> selector)
    {
        List<string> segments = new();

        var node = Unwrap(selector.Body);

        // Walk from the property back to the parameter, so the segments come out reversed and an index attaches
        // to the segment it was read from
        string? pendingIndex = null;

        while (true)
        {
            if (node is MemberExpression member)
            {
                segments.Add((pendingIndex is null) ? member.Member.Name : $"{member.Member.Name}[{pendingIndex}]");
                pendingIndex = null;

                node = Unwrap(member.Expression);

                continue;
            }

            // If we found an index, hold on to it until the property it belongs to comes next.
            var idxOf = IndexOf(node);
            if (idxOf is not null)
            {
                if (pendingIndex is not null) { throw new WeequeryException(WeequeryError.PathInvalid, $"Could not extract path from '{selector}': it includes adjacent indexes"); } // No multi-dim [x][y]

                pendingIndex = idxOf.Index;

                node = Unwrap(idxOf.Source);

                continue;
            }

            break;
        }

        // The chain must end at the selectors own parameter.
        if ((segments.Count == 0) || (node != selector.Parameters[0]))
        {
            throw new WeequeryException(WeequeryError.PathInvalid, $"Could not extract path from '{selector}', it must select a property of {typeof(TClass).Name}");
        }

        segments.Reverse(); // flip to the expected ordering

        return string.Join(".", segments);
    }

    /// <summary>
    /// Step over any boxing coversions
    /// </summary>
    /// <param name="expression"></param>
    /// <returns></returns>
    private static Expression? Unwrap(Expression? expression)
    {
        while ((expression is UnaryExpression unary) && (expression.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked or ExpressionType.TypeAs))
        {
            expression = unary.Operand;
        }

        return expression;
    }

}
