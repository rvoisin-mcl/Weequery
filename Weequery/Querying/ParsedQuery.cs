using System.Diagnostics.CodeAnalysis;
using Weequery.Interfaces;
using Weequery.Parsing;

namespace Weequery;

/// <summary>
/// A condition, a sort clause, and a projection read from a string
/// </summary>
/// <remarks>
/// <para>
/// Supports multiple styles for backwards compatibility, but new development should restrict to <see cref="QueryStyle.Native"/>
/// </para>
/// <code>
/// var (condition, sorts, fields) = ParsedQuery.Parse("Pay &gt; 10000 OrderBy Pay DESC Select Name, Pay");
/// </code>
/// <para>
/// Deconstructs into two as well, for the caller who asked for no projection and wants none back.
/// </para>
/// </remarks>
/// <param name="Condition">what to filter by, or null where the string asked for no filtering</param>
/// <param name="Sorts">what to sort by, in the order they apply; never null, and empty where nothing was asked</param>
/// <param name="Projection">
/// which fields to read back; never null, and <see cref="Weequery.Projection.None"/> where nothing was asked,
/// which is the whole row
/// </param>
public record ParsedQuery(ICondition? Condition, List<Sort> Sorts, Projection? Projection)
{
    /// <summary>
    /// A condition and a sort clause, with nothing read back but the whole row
    /// </summary>
    /// <param name="condition"></param>
    /// <param name="sorts"></param>
    public ParsedQuery(ICondition? condition, List<Sort> sorts) : this(condition, sorts, Weequery.Projection.None)
    { }

    /// <summary>
    /// Held so the null a caller can pass becomes the nothing they meant, on a <c>with</c> as on a ctor.
    /// </summary>
    private readonly Projection Fields = Projection ?? Weequery.Projection.None;

    /// <summary>
    /// <inheritdoc cref="ParsedQuery" path="/param[@name='Projection']/node()"/>
    /// </summary>
    [AllowNull]
    public Projection Projection
    {
        get { return Fields; }

        init { Fields = value ?? Weequery.Projection.None; }
    }

    /// <summary>
    /// The two halves this used to hold, for the callers who only ever wanted those
    /// </summary>
    /// <param name="condition"></param>
    /// <param name="sorts"></param>
    public void Deconstruct(out ICondition? condition, out List<Sort> sorts)
    {
        condition = Condition;
        sorts = Sorts;
    }

    /// <summary>
    /// All three parts.
    /// </summary>
    /// <remarks>
    /// Written out rather than left to the record, so the projection comes back as the non-null it always is.
    /// The one the record would have generated takes its nullability from the parameter, which is nullable only
    /// so that a caller may leave it out.
    /// </remarks>
    /// <param name="condition"></param>
    /// <param name="sorts"></param>
    /// <param name="projection"></param>
    public void Deconstruct(out ICondition? condition, out List<Sort> sorts, out Projection projection)
    {
        condition = Condition;
        sorts = Sorts;
        projection = Projection;
    }

    /// <summary>
    /// Read a condition, a sort clause and a projection from one string, each introduced by its own word.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The separators are what tell the parts apart, so unlike the clauses <see cref="Sort.Parse"/> and
    /// <see cref="Weequery.Projection.Parse"/> read on their own, here each is required to introduce its own.
    /// Without any of them the whole string is a condition. Every part may be left out:
    /// </para>
    /// <code>
    /// Pay > 10000 OrderBy Pay DESC Select Name, Pay   all three
    /// Pay > 10000 ORDER BY Pay DESC, Name             a condition and sorts, the two word spelling
    /// Pay > 10000 Select Name, Pay                    a condition and a projection, and no sorting
    /// Pay > 10000                                     a condition, and whatever default sort was given
    /// OrderBy Pay DESC Select Name                    sorts and a projection, and no filtering at all
    /// Select Name, Pay                                a projection, and nothing else
    /// </code>
    /// <para>
    /// <b>The order is fixed</b>, condition then sorts then projection, because that is the order the parts are
    /// read in and there is nothing in the text to tell them apart other than where they sit. A Select before an
    /// OrderBy is refused rather than reordered.
    /// </para>
    /// <para>
    /// Where each split falls is found by reading the part before it and seeing where it stops, not by searching
    /// the text, so a value that spells a separator is still a value: <c>Name = 'Select'</c> is one comparison
    /// and no projection. See <see cref="QueryParser.ParseLeading"/> and
    /// <see cref="SortParser.ParseLeading"/>.
    /// </para>
    /// <para>
    /// A bare OrderBy or Select where a part could begin is read as the separator rather than as a field, which
    /// is why no binding may be named for either, see <see cref="QueryKeywords"/>. ORDER and BY are read only
    /// together and so are not reserved: a field named Order is filtered and sorted on like any other.
    /// </para>
    /// </remarks>
    /// <param name="query">null, empty or whitespace gives no condition and <paramref name="defaultSort"/></param>
    /// <param name="defaultSort">
    /// what to sort by where the string named no sorts, copied rather than kept. Worth supplying wherever the
    /// query is paged, see <see cref="Inquiry{T}.ApplyPagination"/>.
    /// </param>
    /// <param name="style">
    /// <see cref="QueryStyle.Native"/> to hold every part to the strict grammar, so one spelling per operator in
    /// the condition and the one word OrderBy for the separator. Null, the default, accepts every spelling,
    /// which is what reading has always done. Select is one word in every style, so nothing about it changes.
    /// See <see cref="ConditionFunctions.ParseQuery"/>
    /// </param>
    /// <returns>never null, though every part of it may be empty</returns>
    /// <exception cref="WeequeryException">a part is malformed, or spells something a way the style refuses</exception>
    public static ParsedQuery Parse(string? query, IEnumerable<Sort>? defaultSort = null, QueryStyle style = QueryStyle.Native)
    {
        var tokens = QueryTokenizer.Tokenize(query ?? string.Empty, style);

        if (tokens.Count == 0) { return new ParsedQuery(null, [.. defaultSort ?? []]); }

        // No condition, only what a separator introduces
        if (StartsAPart(tokens, 0, style))
        {
            var (leadingSorts, leadingFields) = ParseTail(query!, defaultSort, style);

            return new ParsedQuery(null, leadingSorts, leadingFields);
        }

        var condition = QueryParser.ParseLeading(tokens, query!, out var stopped, style);

        // A condition and nothing after it
        if (stopped >= tokens.Count) { return new ParsedQuery(condition, [.. defaultSort ?? []]); }

        // Check for unexpected text between the condition and whatever follows it. A style that refuses the
        // spelling that is there throws from here rather than returning zero, so the separator a caller did write
        // is named instead of being reported as a stray word. Reported against the whole query, since that is
        // the text the caller sent.
        if (!StartsAPart(tokens, stopped, style))
        {
            throw new WeequeryException(WeequeryError.QuerySyntax, QueryText.Describe(query!, $"Unexpected '{tokens[stopped].Text}'", tokens[stopped].Position));
        }

        var (sorts, fields) = ParseTail(query![tokens[stopped].Position..], defaultSort, style);

        return new ParsedQuery(condition, sorts, fields);
    }

    /// <summary>
    /// If a separator sits at this point, so if what follows is a part rather than stray text
    /// </summary>
    /// <param name="tokens"></param>
    /// <param name="index">where to look</param>
    /// <param name="style"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the style refuses the spelling that is there</exception>
    private static bool StartsAPart(List<QueryToken> tokens, int index, QueryStyle style = QueryStyle.Native)
    {
        return (SortParser.PrefixLength(tokens, index, style) > 0) || (ProjectionParser.PrefixLength(tokens, index) > 0);
    }

    /// <summary>
    /// Everything after the condition: an optional sort clause, then an optional projection.
    /// </summary>
    /// <remarks>
    /// Given its own text rather than an offset into the query, so the two clause parsers read the same strings
    /// they would have been handed on their own and their messages point where they always did.
    /// </remarks>
    /// <param name="tail">the text from the first separator onwards</param>
    /// <param name="defaultSort">what to sort by where the tail named no sorts</param>
    /// <param name="style"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">either clause is malformed</exception>
    private static (List<Sort> Sorts, Projection Projection) ParseTail(string tail, IEnumerable<Sort>? defaultSort, QueryStyle style = QueryStyle.Native)
    {
        var tokens = QueryTokenizer.Tokenize(tail, style);

        if (tokens.Count == 0) { return ([.. defaultSort ?? []], Weequery.Projection.None); }

        // A projection with no sorts in front of it, so the default sort stands in as it does anywhere else
        if (ProjectionParser.PrefixLength(tokens, 0) > 0)
        {
            return ([.. defaultSort ?? []], FieldsAfter(tail, tokens, 0));
        }

        var sorts = SortParser.ParseLeading(tokens, tail, defaultSort, out var stopped, style);

        if (stopped >= tokens.Count) { return (sorts, Weequery.Projection.None); }

        if (ProjectionParser.PrefixLength(tokens, stopped) == 0)
        {
            throw new WeequeryException(WeequeryError.QuerySyntax, QueryText.Describe(tail, $"Unexpected '{tokens[stopped].Text}'", tokens[stopped].Position));
        }

        return (sorts, FieldsAfter(tail, tokens, stopped));
    }

    /// <summary>
    /// The field list a Select introduces, which has to name at least one field.
    /// </summary>
    /// <remarks>
    /// A Select with nothing after it is refused rather than read as the projection that names nothing. The
    /// second is a real thing, see <see cref="Weequery.Projection.None"/>, and it is what leaving the Select off
    /// says; a caller who typed the word and then stopped meant to name something.
    /// </remarks>
    /// <param name="text">the text the tokens came from</param>
    /// <param name="tokens"></param>
    /// <param name="select">where the Select sits</param>
    /// <param name="style"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">nothing follows the Select, or what does is malformed</exception>
    private static Projection FieldsAfter(string text, List<QueryToken> tokens, int select, QueryStyle style = QueryStyle.Native)
    {
        if ((select + 1) >= tokens.Count)
        {
            throw new WeequeryException(WeequeryError.QuerySyntax, QueryText.Describe(text, $"'{ProjectionParser.Prefix}' names no fields", tokens[select].Position));
        }

        return Weequery.Projection.Parse(text[tokens[select + 1].Position..], style);
    }

    /// <summary>
    /// Write out as a combined string, in the format that <see cref="Parse"/> expects.
    /// </summary>
    /// <remarks>
    /// Each part is written only where there is one, and the separators are what hold them apart, so what comes
    /// out reads back as what went in.
    /// </remarks>
    /// <param name="style">
    /// which spelling the condition uses for the operators that have more than one. It does not reach the sorts
    /// or the projection, which have none, and of the sort separator it decides only how it is spelled, never if
    /// there is one: <see cref="QueryStyle.Native"/> writes OrderBy, every other style ORDER BY
    /// </param>
    /// <returns>the empty string where there is no condition, no sort and no projection</returns>
    /// <exception cref="WeequeryException">
    /// the condition cannot be written, or a sort names no field. See <see cref="ConditionFunctions.ToQuery"/>
    /// and <see cref="SortFunctions.ToQuery(IEnumerable{Sort}, QueryStyle)"/>
    /// </exception>
    public string ToQuery(QueryStyle style = QueryStyle.Native)
    {
        List<string> parts = [];

        if (Condition is not null) { parts.Add(Condition.ToQuery(style)); }

        // The clause with no prefix on it, which every style but SQL writes, so the separator can be put on in
        // this style's spelling. An empty list gives the empty string, so this is also the test for having any.
        // Unlike the standalone clause, the separator here is not optional: it is one of the things telling the
        // parts apart, see Parse
        var clause = Sorts.ToQuery(QueryStyle.Native);

        if (clause.Length > 0) { parts.Add($"{SortFunctions.Separator(style)} {clause}"); }

        if (!Projection.IsEmpty) { parts.Add($"{ProjectionParser.Prefix} {Projection.ToQuery()}"); }

        return string.Join(" ", parts);
    }

    /// <summary>
    /// Renders as the text it would be written as, see <see cref="ToQuery"/>
    /// </summary>
    /// <returns></returns>
    public override string ToString()
    {
        return ToQuery();
    }
}
