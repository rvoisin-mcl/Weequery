using System.Text;
using Weequery.Interfaces;

namespace Weequery.OData;

/// <summary>
/// Which revision of OData to write for, since two of the operators only exist in the later one.
/// </summary>
public enum ODataVersion
{
    /// <summary>
    /// OData 4.0. <see cref="Operator.IsIn"/> is expanded into a chain of <c>or</c>, and
    /// <see cref="Operator.IsMatch"/> is refused, both because 4.0 has no operator for them.
    /// </summary>
    V4,

    /// <summary>
    /// OData 4.01, which added the <c>in</c> operator and the <c>matchesPattern</c> function. The default, since
    /// it is what most services have spoken for years, and the one that keeps a long list short.
    /// </summary>
    V401,
}

/// <summary>
/// Turns a Weequery condition into an OData <c>$filter</c> expression.
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// var condition = ConditionFunctions.ParseQuery("IsActive = true AND Pay &gt; 10000");
///
/// ODataFilter.Write(condition, fields);
/// // "(Active eq true and Salary gt 10000)"
/// </code>
/// </para>
/// <para>
/// What comes out is the value of <c>$filter</c>, unencoded. Percent encoding it is the job of whatever puts it
/// on a query string, and doing it here would mean doing it twice, see <see cref="ODataQuery"/>.
/// </para>
/// <para>
/// <b>Nulls are translated, not assumed.</b> OData does not agree with Weequery about a null on the right of a
/// negative comparison, and it says so explicitly: "null values are equal to null and not equal to any other
/// value", so <c>Alias ne 'Ghost'</c> is <b>true</b> of a record with no alias. In Weequery it is false, because
/// every comparison carries a guard and a null satisfies nothing except <see cref="Operator.IsNull"/>. So every
/// negative operator is written with its guard beside it:
/// <code>
/// Alias &lt;&gt; 'Ghost'    (Alias ne null and Alias ne 'Ghost')
/// </code>
/// Services vary in how much of this they get right on their own, particularly for the string functions, and a
/// guard that a service would have applied anyway costs nothing but a few characters. Depending on it would cost
/// a different answer per service.
/// </para>
/// <para>
/// <see cref="Operator.Not"/> is the other half of the rule and is deliberately <b>not</b> guarded: negating a
/// condition negates its guard too, so <c>NOT (Alias = 'Ghost')</c> is meant to bring the records with no alias
/// back, and a bare <c>not</c> is exactly right for it.
/// </para>
/// </remarks>
public static class ODataFilter
{
    /// <summary>
    /// The <c>$filter</c> expression for a condition.
    /// </summary>
    /// <param name="condition">null gives the empty string, which is what no filtering means</param>
    /// <param name="fields">the allow-list, deciding what a key may name and how a value is written</param>
    /// <param name="version">[OPT] which revision to write for, 4.01 by default</param>
    /// <returns>the expression, unencoded; empty where there is nothing to filter by</returns>
    /// <exception cref="WeequeryException">
    /// a key is not declared, an operator does not fit the field's kind or the version, or the condition uses
    /// something OData has no answer for
    /// </exception>
    public static string Write(ICondition? condition, ODataFieldSet fields, ODataVersion version = ODataVersion.V401)
    {
        WeequeryException.ThrowIfNull(fields);

        return (condition is null) ? string.Empty : new ODataTranslator(fields, version).Run(condition);
    }
}

/// <summary>
/// The translation itself, which is <see cref="ConditionTranslator{TResult, TScope}"/> filled in with what OData
/// happens to spell things as.
/// </summary>
/// <remarks>
/// The scope is the lambda in force, or null at the top: inside a quantifier every path hangs off the lambda's
/// variable, so both the variable and the collection it binds have to travel down with it.
/// </remarks>
/// <param name="fields">the allow-list</param>
/// <param name="version">which revision to write for</param>
internal sealed class ODataTranslator(ODataFieldSet fields, ODataVersion version)
    : ConditionTranslator<string, (string Variable, string Collection)?>
{
    /// <inheritdoc/>
    protected override string Dialect { get { return "a $filter"; } }

    /// <summary>The whole condition, from the top, where no lambda is in force</summary>
    internal string Run(ICondition condition)
    {
        return Translate(condition, null);
    }

    /// <inheritdoc/>
    protected override string Negate(string operand)
    {
        // Not guarded, and that is the point: negating a condition negates its guard with it
        return $"not {operand}";
    }


    /// <inheritdoc/>
    protected override string Combine(Operator conjunction, IReadOnlyList<string> parts)
    {
        // The identities: AND over nothing matches everything, OR over nothing matches nothing. OData has no
        // keyword for either, so they are written as expressions that are trivially true and trivially false.
        if (parts.Count == 0) { return (conjunction == Operator.And) ? "true" : "false"; }

        if (parts.Count == 1) { return parts[0]; }

        var joiner = (conjunction == Operator.And) ? " and " : " or ";

        // Parenthesised whatever the precedence would have been, so a tree reads back as the tree it was
        return $"({string.Join(joiner, parts)})";
    }

    /// <summary>
    /// A quantifier, which OData spells as a lambda on the collection.
    /// </summary>
    /// <remarks>
    /// <code>
    /// Any   Assignments/any(d1: d1/LairID eq 5)
    /// All   Assignments/all(d1: d1/LairID eq 5)
    /// None  not Assignments/any(d1: d1/LairID eq 5)
    /// </code>
    /// <para>
    /// <c>all</c> over an empty collection is true and <c>any</c> over one is false, which is what Weequery's
    /// quantifiers mean of nothing as well, so the three line up without any help.
    /// </para>
    /// </remarks>
    /// <inheritdoc/>
    protected override string Quantify(QuantifiedCondition quantified, (string Variable, string Collection)? lambda, int depth)
    {
        if (lambda is not null)
        {
            throw new WeequeryException(WeequeryError.NotTranslatable, $"'{quantified.Field}' is quantified inside another quantifier. OData can nest lambdas, but a Weequery collection declares no collections of its own, so there is nothing here that could have been meant");
        }

        var field = fields.Resolve(quantified.Field, "quantified over");

        if (field.Kind != ODataFieldKind.Collection)
        {
            throw new WeequeryException(WeequeryError.OperatorUnsupported, $"'{quantified.Field}' is declared as {field.Kind} rather than {nameof(ODataFieldKind.Collection)}, so there is nothing for {quantified.Operator} to quantify over");
        }

        // The variable a lambda binds. Its name only has to be legal and not shadow anything, and nothing can
        // nest here, so one name will do.
        const string variable = "d1";

        var inner = Translate(quantified.Condition, (variable, field.Key), ConditionNesting.Descend(depth));

        return quantified.Operator switch
        {
            Operator.Any => $"{field.Field}/any({variable}: {inner})",
            Operator.All => $"{field.Field}/all({variable}: {inner})",
            Operator.None => $"not {field.Field}/any({variable}: {inner})",

            _ => throw new WeequeryException(WeequeryError.OperatorInvalid, $"Operator {quantified.Operator} is not a quantifier"),
        };
    }

    // ---------- comparisons ----------

    /// <inheritdoc/>
    protected override string Compare(IBoundCondition condition, (string Variable, string Collection)? lambda)
    {
        if (condition.Index is not null)
        {
            throw new WeequeryException(WeequeryError.NotTranslatable, $"'{condition.Field}[{condition.Index}]' indexes a collection, and a $filter has no way to address one element of one. Ask about the elements with a quantifier, or declare the element's own path as a field");
        }

        var field = fields.Resolve(condition.Field, $"asked '{condition.Operator}'");

        if (field.Kind == ODataFieldKind.Collection)
        {
            throw new WeequeryException(WeequeryError.OperatorUnsupported, $"'{field.Key}' is a collection, so '{condition.Operator}' cannot be asked of it. Use Any, All or None with a condition about one element");
        }

        if (field.Collection != lambda?.Collection)
        {
            throw new WeequeryException(WeequeryError.OperatorUnsupported, (lambda is null)
                ? $"'{field.Key}' is declared inside the collection '{field.Collection}', so it can only be asked about within a quantifier over it"
                : $"'{field.Key}' is not declared inside '{lambda.Value.Collection}', so it cannot be asked about inside that quantifier");
        }

        // Inside a lambda every path hangs off its variable, and outside there is nothing to hang off
        var path = (lambda is null) ? field.Field : $"{lambda.Value.Variable}/{field.Field}";

        var operands = condition.StringifyOperands();

        var values = new List<string>();

        foreach (var operand in operands)
        {
            // The other side may be another declared field rather than a value, which OData writes as a path and
            // is one of the few things it does that SQL and the Query DSL both struggle with
            values.Add(operand.NamesProperty ? Path(operand.Value, lambda) : Literal(field, operand.Value, condition));
        }

        return condition.Operator switch
        {
            Operator.IsNull => $"{path} eq null",
            Operator.IsNotNull => $"{path} ne null",

            Operator.Equals => $"{path} eq {Only(values, condition)}",
            Operator.NotEqual => Guarded(path, $"{path} ne {Only(values, condition)}"),

            Operator.LessThan => Relational(field, path, "lt", values, condition),
            Operator.LessThanOrEqual => Relational(field, path, "le", values, condition),
            Operator.GreaterThan => Relational(field, path, "gt", values, condition),
            Operator.GreaterThanOrEqual => Relational(field, path, "ge", values, condition),

            Operator.IsBetween => Between(field, path, values, condition),
            Operator.IsNotBetween => Guarded(path, $"not {Between(field, path, values, condition)}"),

            Operator.IsIn => In(path, values),
            Operator.IsNotIn => Guarded(path, $"not {In(path, values)}"),

            Operator.StartsWith => Function(field, "startswith", path, values, condition),
            Operator.DoesNotStartWith => Guarded(path, $"not {Function(field, "startswith", path, values, condition)}"),

            Operator.EndsWith => Function(field, "endswith", path, values, condition),
            Operator.DoesNotEndWith => Guarded(path, $"not {Function(field, "endswith", path, values, condition)}"),

            Operator.Contains => Function(field, "contains", path, values, condition),
            Operator.DoesNotContain => Guarded(path, $"not {Function(field, "contains", path, values, condition)}"),

            Operator.IsMatch => Matches(field, path, values, condition),
            Operator.DoesNotMatch => Guarded(path, $"not {Matches(field, path, values, condition)}"),

            _ => throw new WeequeryException(WeequeryError.NotTranslatable, $"Operator {condition.Operator} has no representation in a $filter"),
        };
    }

    /// <summary>
    /// A negative operator: the test, and the property required to have a value.
    /// </summary>
    /// <remarks>
    /// The difference between this and a naive translation. OData says a null is "not equal to any other value",
    /// so <c>Alias ne 'Ghost'</c> returns the records with no alias where Weequery's does not. The
    /// <c>ne null</c> puts the guard back, and does it the same way whatever the service would have done alone.
    /// </remarks>
    private static string Guarded(string path, string test)
    {
        return $"({path} ne null and {test})";
    }

    private static string Relational(ODataField field, string path, string op, List<string> values, IBoundCondition condition)
    {
        RequireOrderable(field, condition);

        return $"{path} {op} {Only(values, condition)}";
    }

    private static string Between(ODataField field, string path, List<string> values, IBoundCondition condition)
    {
        RequireOrderable(field, condition);

        if (values.Count != 2)
        {
            throw new WeequeryException(WeequeryError.OperandCount, $"Operator {condition.Operator} on field '{condition.Field}' needs two values but got {values.Count}");
        }

        // Inclusive of both ends, which is what IsBetween means in Weequery
        return $"({path} ge {values[0]} and {path} le {values[1]})";
    }

    /// <summary>
    /// A list, as the <c>in</c> operator where the version has one and as a chain of <c>or</c> where it does not.
    /// </summary>
    /// <remarks>
    /// The expansion is what 4.0 leaves you, and it is worth knowing that a long list becomes a long URL: a
    /// thousand values, which is what Weequery will carry, is not something every server will accept on a query
    /// string however it is spelled.
    /// </remarks>
    private string In(string path, List<string> values)
    {
        // An empty list matches nothing, in OData as in Weequery, and "false" is how that is said
        if (values.Count == 0) { return "false"; }

        if (version == ODataVersion.V401) { return $"{path} in ({string.Join(",", values)})"; }

        return $"({string.Join(" or ", from value in values select $"{path} eq {value}")})";
    }

    private static string Function(ODataField field, string name, string path, List<string> values, IBoundCondition condition)
    {
        RequireTextual(field, condition);

        return $"{name}({path},{Only(values, condition)})";
    }

    /// <summary>
    /// A regular expression, which only 4.01 has a function for.
    /// </summary>
    /// <remarks>
    /// <c>matchesPattern</c> is ECMAScript syntax by specification, and it is one of the more thinly implemented
    /// parts of 4.01, so a service may answer 501 to a filter that is perfectly legal.
    /// </remarks>
    private string Matches(ODataField field, string path, List<string> values, IBoundCondition condition)
    {
        RequireTextual(field, condition);

        if (version != ODataVersion.V401)
        {
            throw new WeequeryException(WeequeryError.NotTranslatable, $"Operator {condition.Operator} needs the matchesPattern function, which OData 4.01 added and {version} does not have");
        }

        return $"matchesPattern({path},{Only(values, condition)})";
    }

    // ---------- the small pieces ----------

    /// <summary>
    /// The path of a field being compared against, rather than a value. Held to the same scope as the left side,
    /// since a lambda's variable does not reach outside it and nothing outside reaches in.
    /// </summary>
    private string Path(string key, (string Variable, string Collection)? lambda)
    {
        var split = ConditionFunctions.SplitIndex(key);

        if (split.Index is not null)
        {
            throw new WeequeryException(WeequeryError.NotTranslatable, $"'{key}' indexes a collection, and a $filter has no way to address one element of one");
        }

        var field = fields.Resolve(split.Key, "compared against");

        if (field.Collection != lambda?.Collection)
        {
            throw new WeequeryException(WeequeryError.OperatorUnsupported, $"'{field.Key}' cannot be compared against here: it is declared {(field.Collection is null ? "outside any collection" : $"inside '{field.Collection}'")}, and this comparison is {(lambda is null ? "outside one" : $"inside '{lambda.Value.Collection}'")}");
        }

        return (lambda is null) ? field.Field : $"{lambda.Value.Variable}/{field.Field}";
    }

    private static void RequireTextual(ODataField field, IBoundCondition condition)
    {
        if (field.IsTextual) { return; }

        throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Operator {condition.Operator} is unsupported for '{field.Key}', which is declared as {field.Kind}: it matches text");
    }

    private static void RequireOrderable(ODataField field, IBoundCondition condition)
    {
        if (field.IsOrderable) { return; }

        throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Operator {condition.Operator} is unsupported for '{field.Key}', which is declared as {field.Kind} and has no ordering");
    }

    /// <summary>
    /// A value written the way its kind is written in OData.
    /// </summary>
    /// <remarks>
    /// Every value reaches here as text, which is how a condition carries them whatever they started as, so this
    /// is the same reading against a declared type the expression builder does against a property's. Nothing is
    /// reformatted beyond what the syntax needs: a date is passed through, because a service reads it against its
    /// own model and this could only guess.
    /// </remarks>
    private static string Literal(ODataField field, string text, IBoundCondition condition)
    {
        switch (field.Kind)
        {
            case ODataFieldKind.String:
                return Quote(text);

            case ODataFieldKind.Boolean:
                // True/False is how Weequery writes one, and OData wants it lower case
                return bool.TryParse(text, out var flag)
                    ? (flag ? "true" : "false")
                    : throw new WeequeryException(WeequeryError.ValueInvalid, $"'{text}' is not a boolean, and '{field.Key}' under {condition.Operator} is declared as {field.Kind}");

            case ODataFieldKind.Duration:
                return $"duration{Quote(text)}";

            case ODataFieldKind.Enum:
                // Qualified where the field named its type, which is what the specification asks for, and quoted
                // otherwise, which many services take anyway
                return (field.EnumType is null) ? Quote(text) : $"{field.EnumType}{Quote(text)}";

            default:
                // Numbers, Guids, dates and times all go bare. Refusing what is plainly not one of them here
                // rather than letting a service answer 400 for a reason it cannot explain.
                return Bare(field, text, condition);
        }
    }

    /// <summary>
    /// A literal OData writes without quotes. Checked for the characters that would end it early rather than
    /// parsed, since a date's own format is the service's business and there are several of them.
    /// </summary>
    private static string Bare(ODataField field, string text, IBoundCondition condition)
    {
        if ((text.Length == 0) || text.Any(character => char.IsWhiteSpace(character) || (character == '\'') || (character == ',') || (character == '(') || (character == ')')))
        {
            throw new WeequeryException(WeequeryError.ValueInvalid, $"'{text}' cannot be written as a bare {field.Kind} literal for '{field.Key}': it holds something that would end the expression early");
        }

        return text;
    }

    /// <summary>
    /// A string literal. A quote inside one is doubled, which is how OData escapes it, and is the whole of the
    /// escaping the syntax has.
    /// </summary>
    private static string Quote(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('\'');

        foreach (var character in value)
        {
            if (character == '\'') { quoted.Append('\''); }

            quoted.Append(character);
        }

        return quoted.Append('\'').ToString();
    }
}
