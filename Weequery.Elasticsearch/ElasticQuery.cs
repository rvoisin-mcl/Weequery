using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Weequery.Interfaces;

namespace Weequery.Elasticsearch;

/// <summary>
/// Turns a Weequery condition into Elasticsearch Query DSL.
/// </summary>
/// <remarks>
/// <para>
/// <code>
/// var condition = ConditionFunctions.ParseQuery("IsActive = true AND Pay &gt; 10000");
///
/// var query = ElasticQuery.Build(condition, fields);
/// // { "bool": { "filter": [ { "term": { "active": true } }, { "range": { "salary": { "gt": 10000 } } } ] } }
/// </code>
/// </para>
/// <para>
/// What comes out goes in the <c>query</c> slot of a search body, see <see cref="ElasticSearchBody"/>. It is
/// plain JSON, so it does not care which client puts it there.
/// </para>
/// <para>
/// <b>Nulls are translated, not assumed.</b> Weequery's operators carry a guard: a comparison is true only where
/// the property has a value, so a null satisfies nothing except <see cref="Operator.IsNull"/>, and the negative
/// operators do not catch one either. Elasticsearch agrees for free on the positive operators, because a
/// document missing a field matches no <c>term</c> and no <c>range</c>. It does <b>not</b> agree on the negative
/// ones: a bare <c>must_not</c> matches documents that have no such field at all. So those are written with an
/// <c>exists</c> beside them, which is the difference between this and a naive translation.
/// </para>
/// <para>
/// <see cref="Operator.Not"/> is the other half of the same rule, and it is deliberately not guarded: negating a
/// condition negates its guard too, so <c>NOT (Alias = 'Ghost')</c> is meant to bring the documents with no alias
/// back, and a bare <c>must_not</c> is exactly right for it.
/// </para>
/// </remarks>
public static class ElasticQuery
{
    /// <summary>
    /// The Query DSL for a condition.
    /// </summary>
    /// <param name="condition">null gives <c>match_all</c>, which is what no filtering means</param>
    /// <param name="fields">the allow-list, which decides what a key may name and how a value is written</param>
    /// <returns>a fresh object every call, safe to put in a larger body and mutate</returns>
    /// <exception cref="WeequeryException">
    /// a key is not declared, an operator does not fit the field's kind, a value will not parse, or the condition
    /// uses something the Query DSL has no answer for
    /// </exception>
    public static JsonObject Build(ICondition? condition, ElasticFieldSet fields)
    {
        WeequeryException.ThrowIfNull(fields);

        return (condition is null) ? ElasticTranslator.MatchAll() : new ElasticTranslator(fields).Run(condition);
    }

    /// <summary>
    /// The Query DSL for a condition, as JSON text.
    /// </summary>
    /// <param name="condition">null gives <c>match_all</c></param>
    /// <param name="fields"></param>
    /// <param name="indented">[OPT] true to write it readably, for a log or a test</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">whatever <see cref="Build(ICondition, ElasticFieldSet)"/> would throw</exception>
    public static string ToJson(ICondition? condition, ElasticFieldSet fields, bool indented = false)
    {
        return Build(condition, fields).ToJsonString(new JsonSerializerOptions { WriteIndented = indented });
    }

}

/// <summary>
/// The translation itself, which is <see cref="ConditionTranslator{TResult, TScope}"/> filled in with what the
/// Query DSL happens to spell things as.
/// </summary>
/// <remarks>
/// The scope is the nested path in force, or null at the top: a nested query addresses one path, so a field has
/// to be declared under the one being quantified over to be reachable inside it.
/// </remarks>
/// <param name="fields">the allow-list</param>
internal sealed class ElasticTranslator(ElasticFieldSet fields) : ConditionTranslator<JsonObject, string?>
{
    /// <inheritdoc/>
    protected override string Dialect { get { return "the Query DSL"; } }

    /// <summary>The whole condition, from the top, where nothing is nested yet</summary>
    internal JsonObject Run(ICondition condition)
    {
        return Translate(condition, null);
    }

    /// <inheritdoc/>
    protected override JsonObject Negate(JsonObject operand)
    {
        // Not guarded, and that is the point: negating a condition negates its guard with it, so this is meant
        // to bring back the documents the inner test could not answer for
        return MustNot(operand);
    }

    // ---------- containers ----------

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "ElasticQuery.Build and ElasticSearchBody.Build declare this, and they are the only way here; ConditionTranslator does not require it of a translator, so this override cannot say that it does")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "ElasticQuery.Build and ElasticSearchBody.Build declare this, and they are the only way here; ConditionTranslator does not require it of a translator, so this override cannot say that it does")]
    protected override JsonObject Combine(Operator conjunction, IReadOnlyList<JsonObject> parts)
    {
        var clauses = new JsonArray();
        foreach (var part in parts) { clauses.Add(part); }

        if (conjunction == Operator.And)
        {
            // No operands is the identity of AND, which matches everything. "filter" rather than "must" because
            // a filter is not scored and this is a filter: nothing here asks how well a document matched.
            return (clauses.Count == 0) ? MatchAll() : Bool(new JsonObject { ["filter"] = clauses });
        }

        if (conjunction == Operator.Or)
        {
            // And the identity of OR is matching nothing
            if (clauses.Count == 0) { return MustNot(MatchAll()); }

            // Without minimum_should_match a "should" beside no other clause is optional rather than required,
            // which would quietly match everything
            return Bool(new JsonObject { ["should"] = clauses, ["minimum_should_match"] = 1 });
        }

        throw new WeequeryException(WeequeryError.OperatorInvalid, $"Operator {conjunction} is invalid for {nameof(IConjunctionCondition)}");
    }

    /// <summary>
    /// A quantifier, which is a <c>nested</c> query over the path its fields live under.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Elasticsearch flattens objects in an array unless they are mapped <c>nested</c>, and a flattened array
    /// cannot answer "one element that is both of these", which is the whole reason Weequery's quantifier holds
    /// one condition rather than several tests. So a quantified field has to declare its nested path, and if the
    /// index does not map it nested the query will run and answer the weaker question.
    /// </para>
    /// <para>
    /// The three read as they do in Weequery, once you know that a nested query is "some element matches":
    /// <code>
    /// Any   nested(inner)
    /// None  must_not nested(inner)
    /// All   must_not nested(must_not inner)      no element fails, so all of them pass
    /// </code>
    /// The shape for All is also what makes it true of a document with no elements at all, which is what All
    /// means of nothing here and in Weequery both.
    /// </para>
    /// </remarks>
    /// <inheritdoc/>
    protected override JsonObject Quantify(QuantifiedCondition quantified, string? nested, int depth)
    {
        if (nested is not null)
        {
            throw new WeequeryException(WeequeryError.NotTranslatable, $"'{quantified.Field}' is quantified inside another quantifier, and a nested query addresses one path at a time. Flatten the condition, or declare the inner fields against the outer path");
        }

        var path = fields.Resolve(quantified.Field, "quantified over").Nested
            ?? throw new WeequeryException(WeequeryError.BindingInvalid, $"'{quantified.Field}' is declared without a nested path, so there is nothing for {quantified.Operator} to quantify over. Give the {nameof(ElasticField)} its 'nested' argument");

        var inner = Translate(quantified.Condition, path, ConditionNesting.Descend(depth));

        // A JsonNode belongs to one parent, so the inner query is put in a tree exactly once on each of these
        // paths rather than built into one shape and then reused inside another
        return quantified.Operator switch
        {
            Operator.Any => Nested(path, inner),

            Operator.None => MustNot(Nested(path, inner)),

            // "No element fails it", which is the only way to say "every element passes" over nested documents
            Operator.All => MustNot(Nested(path, MustNot(inner))),

            _ => throw new WeequeryException(WeequeryError.OperatorInvalid, $"Operator {quantified.Operator} is not a quantifier"),
        };
    }

    private static JsonObject Nested(string path, JsonObject query)
    {
        return new JsonObject { ["nested"] = new JsonObject { ["path"] = path, ["query"] = query } };
    }

    // ---------- comparisons ----------

    /// <inheritdoc/>
    [UnconditionalSuppressMessage("AOT", "IL3050:RequiresDynamicCode", Justification = "ElasticQuery.Build and ElasticSearchBody.Build declare this, and they are the only way here; ConditionTranslator does not require it of a translator, so this override cannot say that it does")]
    [UnconditionalSuppressMessage("Trimming", "IL2026:RequiresUnreferencedCode", Justification = "ElasticQuery.Build and ElasticSearchBody.Build declare this, and they are the only way here; ConditionTranslator does not require it of a translator, so this override cannot say that it does")]
    protected override JsonObject Compare(IBoundCondition condition, string? nested)
    {
        if (condition.Index is not null)
        {
            throw new WeequeryException(WeequeryError.NotTranslatable, $"'{condition.Field}[{condition.Index}]' indexes a collection, and the Query DSL has no way to address one element of one: an array is flattened into the field. Declare the element's own path as a field of its own");
        }

        var field = fields.Resolve(condition.Field, $"asked '{condition.Operator}'");

        if ((nested is not null) && (field.Nested != nested))
        {
            throw new WeequeryException(WeequeryError.BindingInvalid, $"'{field.Key}' is not declared under the nested path '{nested}', so it cannot be asked about inside that quantifier");
        }

        var operands = condition.StringifyOperands();

        if (operands.Any(operand => operand.NamesProperty))
        {
            throw new WeequeryException(WeequeryError.NotTranslatable, $"'{condition.Field}' is compared against another field, and the Query DSL cannot compare two fields without a script. Compare against a value");
        }

        var values = (from operand in operands select operand.Value).ToList();

        return condition.Operator switch
        {
            Operator.IsNull => MustNot(Exists(field)),
            Operator.IsNotNull => Exists(field),

            Operator.Equals => Equality(field, values, condition),
            Operator.NotEqual => Guarded(field, Equality(field, values, condition)),

            Operator.LessThan => Range(field, condition, ("lt", values)),
            Operator.LessThanOrEqual => Range(field, condition, ("lte", values)),
            Operator.GreaterThan => Range(field, condition, ("gt", values)),
            Operator.GreaterThanOrEqual => Range(field, condition, ("gte", values)),

            Operator.IsBetween => Between(field, condition, values),
            Operator.IsNotBetween => Guarded(field, Between(field, condition, values)),

            Operator.IsIn => Terms(field, values),
            Operator.IsNotIn => Guarded(field, Terms(field, values)),

            Operator.StartsWith => Prefix(field, condition, values),
            Operator.DoesNotStartWith => Guarded(field, Prefix(field, condition, values)),

            Operator.EndsWith => Wildcard(field, condition, values, "*{0}"),
            Operator.DoesNotEndWith => Guarded(field, Wildcard(field, condition, values, "*{0}")),

            Operator.Contains => Wildcard(field, condition, values, "*{0}*"),
            Operator.DoesNotContain => Guarded(field, Wildcard(field, condition, values, "*{0}*")),

            Operator.IsMatch => Regexp(field, condition, values),
            Operator.DoesNotMatch => Guarded(field, Regexp(field, condition, values)),

            _ => throw new WeequeryException(WeequeryError.NotTranslatable, $"Operator {condition.Operator} has no representation in the Query DSL"),
        };
    }

    /// <summary>
    /// A negative operator: the test negated, and the field required to be there.
    /// </summary>
    /// <remarks>
    /// The whole difference between this and a naive translation. Weequery's negative operators do not catch a
    /// null: <c>Alias &lt;&gt; 'Ghost'</c> passes over the documents with no alias rather than returning them,
    /// and a bare <c>must_not</c> would return exactly those. The <c>exists</c> puts the guard back.
    /// </remarks>
    private static JsonObject Guarded(ElasticField field, JsonObject test)
    {
        return Bool(new JsonObject
        {
            ["filter"] = new JsonArray(Exists(field)),
            ["must_not"] = new JsonArray(test),
        });
    }

    private static JsonObject Equality(ElasticField field, List<string> values, IBoundCondition condition)
    {
        var value = Only(values, condition);

        // term against an analysed field looks for the whole string among its tokens and finds nothing, so text
        // gets the nearest honest answer instead and the analyser decides what counts as equal
        return (field.Kind == ElasticFieldKind.Text)
            ? new JsonObject { ["match_phrase"] = new JsonObject { [field.Field] = value } }
            : new JsonObject { ["term"] = new JsonObject { [field.Field] = Value(field, value, condition) } };
    }

    private static JsonObject Range(ElasticField field, IBoundCondition condition, params (string Bound, List<string> Values)[] bounds)
    {
        RequireOrderable(field, condition);

        var body = new JsonObject();

        foreach (var (bound, values) in bounds) { body[bound] = Value(field, Only(values, condition), condition); }

        return new JsonObject { ["range"] = new JsonObject { [field.Field] = body } };
    }

    private static JsonObject Between(ElasticField field, IBoundCondition condition, List<string> values)
    {
        RequireOrderable(field, condition);

        if (values.Count != 2)
        {
            throw new JsonException($"Operator {condition.Operator} on field '{condition.Field}' needs two values but got {values.Count}");
        }

        // Inclusive of both ends, which is what IsBetween means in Weequery
        var body = new JsonObject
        {
            ["gte"] = Value(field, values[0], condition),
            ["lte"] = Value(field, values[1], condition),
        };

        return new JsonObject { ["range"] = new JsonObject { [field.Field] = body } };
    }

    [RequiresDynamicCode("An Elasticsearch query is built as System.Text.Json nodes, which reflect over the values they are given. Use the source generator, and give it the value types these conditions carry")]
    [RequiresUnreferencedCode("An Elasticsearch query is built as System.Text.Json nodes, which reflect over the values they are given. Use the source generator, and give it the value types these conditions carry")]
    private static JsonObject Terms(ElasticField field, List<string> values)
    {
        var list = new JsonArray();

        // An empty list matches nothing, in the Query DSL as in Weequery, so it needs no special case
        foreach (var value in values) { list.Add(Value(field, value, null)); }

        return new JsonObject { ["terms"] = new JsonObject { [field.Field] = list } };
    }

    private static JsonObject Prefix(ElasticField field, IBoundCondition condition, List<string> values)
    {
        RequireTextual(field, condition);

        return new JsonObject { ["prefix"] = new JsonObject { [field.Field] = Only(values, condition) } };
    }

    /// <summary>
    /// The substring operators, which the Query DSL has no operator for and answers with a wildcard.
    /// </summary>
    /// <remarks>
    /// A leading wildcard cannot use the index and is scanned, which is slow on a large one. That is the Query
    /// DSL's own trade rather than this library's, and it is the reason Elasticsearch suggests an ngram analyser
    /// where substring search matters. The value is escaped so a caller cannot smuggle their own wildcard in.
    /// </remarks>
    private static JsonObject Wildcard(ElasticField field, IBoundCondition condition, List<string> values, string pattern)
    {
        RequireTextual(field, condition);

        var value = string.Format(CultureInfo.InvariantCulture, pattern, EscapeWildcard(Only(values, condition)));

        return new JsonObject { ["wildcard"] = new JsonObject { [field.Field] = value } };
    }

    private static JsonObject Regexp(ElasticField field, IBoundCondition condition, List<string> values)
    {
        RequireTextual(field, condition);

        // Passed through as written. Elasticsearch's regular expressions are its own dialect, anchored whole
        // rather than searched, so a pattern that works in .NET does not always mean the same thing here
        return new JsonObject { ["regexp"] = new JsonObject { [field.Field] = Only(values, condition) } };
    }

    private static JsonObject Exists(ElasticField field)
    {
        return new JsonObject { ["exists"] = new JsonObject { ["field"] = field.Field } };
    }

    // ---------- the small pieces ----------

    private static JsonObject Bool(JsonObject body)
    {
        return new JsonObject { ["bool"] = body };
    }

    private static JsonObject MustNot(JsonObject query)
    {
        return Bool(new JsonObject { ["must_not"] = new JsonArray(query) });
    }

    internal static JsonObject MatchAll()
    {
        return new JsonObject { ["match_all"] = new JsonObject() };
    }

    private static void RequireTextual(ElasticField field, IBoundCondition condition)
    {
        if (field.IsTextual) { return; }

        throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Operator {condition.Operator} is unsupported for '{field.Key}', which is declared as {field.Kind}");
    }

    private static void RequireOrderable(ElasticField field, IBoundCondition condition)
    {
        if (field.IsOrderable) { return; }

        throw new WeequeryException(WeequeryError.OperatorUnsupported, $"Operator {condition.Operator} is unsupported for '{field.Key}', which is declared as {field.Kind}");
    }

    /// <summary>
    /// A value written the way the field's kind wants it: a JSON number for a number, a boolean for a boolean,
    /// and text for everything else.
    /// </summary>
    /// <remarks>
    /// Every value reaches here as text, which is how a condition carries them whatever they started as, so this
    /// is the same reading against a declared type that the expression builder does against a property's. A date
    /// stays text on purpose: Elasticsearch parses it against the mapping's own format, which is more than this
    /// could know.
    /// </remarks>
    private static JsonValue Value(ElasticField field, string text, IBoundCondition? condition)
    {
        var what = (condition is null) ? $"'{field.Key}'" : $"'{field.Key}' under {condition.Operator}";

        switch (field.Kind)
        {
            case ElasticFieldKind.Number:
                if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole)) { return JsonValue.Create(whole); }
                if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var real)) { return JsonValue.Create(real); }

                throw new WeequeryException(WeequeryError.ValueInvalid, $"'{text}' is not a number, and {what} is declared as {field.Kind}");

            case ElasticFieldKind.Boolean:
                // True/False is how Weequery writes one, and bool.TryParse reads either case
                if (bool.TryParse(text, out var flag)) { return JsonValue.Create(flag); }

                throw new WeequeryException(WeequeryError.ValueInvalid, $"'{text}' is not a boolean, and {what} is declared as {field.Kind}");

            default:
                return JsonValue.Create(text);
        }
    }

    /// <summary>
    /// Escape what a wildcard query treats as a wildcard, so a value holding one is looked for rather than
    /// obeyed. A caller writing "Contains '*'" means the character.
    /// </summary>
    private static string EscapeWildcard(string value)
    {
        return value.Replace("\\", "\\\\").Replace("*", "\\*").Replace("?", "\\?");
    }
}
