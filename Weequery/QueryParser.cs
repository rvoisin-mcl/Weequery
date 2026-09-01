using Weequery.Interfaces;

namespace Weequery;

/// <summary>
/// Recursive descent parser for the query language. The grammar is:
/// <code>
/// expression  := disjunction
/// disjunction := conjunction ( ('||' | 'OR') conjunction )*
/// conjunction := unary ( ('&amp;&amp;' | 'AND') unary )*
/// unary       := ('!' | 'NOT') unary | primary
/// primary     := '(' expression ')' | comparison
/// comparison  := field operator operands?
/// field       := WORD | QUOTED | '[' WORD ']'
/// operands    := operand | '(' (operand (',' operand)*)? ')' | operand 'AND' operand
/// operand     := literal | '[' WORD ']'
/// literal     := WORD | QUOTED
/// </code>
/// Brackets name a bound property and parentheses hold a list, wherever either appears: 'Pay &gt; 10000' compares
/// against a number, 'Pay &gt; [Salary]' against the property bound as Salary, and 'Pay IsIn (1, 2, [Salary])'
/// against any of the three. Every comparison comes back over string, in whichever of the four shapes its
/// operator calls for, with each operand carrying whether the brackets made it the name of a property, see
/// <see cref="ConditionShape"/> and <see cref="ConditionValue{T}"/>.
/// '&amp;&amp;' binds tighter than '||', matching SQL and C#, and parentheses group freely.
/// <para>
/// Two limits apply, for two different reasons. <see cref="MaxSyntaxDepth"/> bounds the text, since parsing is
/// recursive and the stack is finite, and <see cref="ConditionNesting.MaxDepth"/> bounds the condition that comes
/// out of it, which is the limit a caller is actually held to and the one every other walk over a condition
/// applies. Both are checked, so a query that parses is a query that can be packed, written and built.
/// </para>
/// </summary>
internal sealed class QueryParser
{
    private static readonly Dictionary<string, Operator> OperatorLookup = new(StringComparer.OrdinalIgnoreCase)
    {
        { "IsNull", Operator.IsNull },
        { "IsNotNull", Operator.IsNotNull },
        { "==", Operator.Equals },
        { "!=", Operator.NotEqual },
        { "<", Operator.LessThan },
        { "<=", Operator.LessThanOrEqual },
        { ">", Operator.GreaterThan },
        { ">=", Operator.GreaterThanOrEqual },
        { "StartsWith", Operator.StartsWith },
        { "DoesNotStartWith", Operator.DoesNotStartWith },
        { "EndsWith", Operator.EndsWith },
        { "DoesNotEndWith", Operator.DoesNotEndWith },
        { "Contains", Operator.Contains },
        { "DoesNotContain", Operator.DoesNotContain },
        { "IsMatch", Operator.IsMatch },
        { "DoesNotMatch", Operator.DoesNotMatch },
        { "IsIn", Operator.IsIn },
        { "IsNotIn", Operator.IsNotIn },
        { "IsBetween", Operator.IsBetween },
        { "IsNotBetween", Operator.IsNotBetween },

        // SQL spellings. The multi-word ones (IS NULL, IS NOT NULL, NOT IN, NOT BETWEEN) cannot live in a lookup
        // keyed on a single token, so ParseSqlPhrase handles those.
        { "In", Operator.IsIn },
        { "Between", Operator.IsBetween },
    };

    /// <summary>
    /// How deeply the *text* may nest before parsing gives up. Every group and every negation is one level.
    /// <para>
    /// This is a guard on the parse stack, not the contract on conditions: parsing is recursive descent, so text
    /// nesting a few thousand deep would overflow the stack, which cannot be caught and takes the process with
    /// it. What a caller is actually held to is <see cref="ConditionNesting.MaxDepth"/>, checked against the tree
    /// once it has been built, and the two are not the same count. Text can nest without building anything, as
    /// the redundant parentheses in "(((A)))" do, and it can build without nesting, as precedence does in
    /// "A &amp;&amp; B || C &amp;&amp; D". So this sits well above the condition limit, with room for the
    /// parentheses <see cref="QueryWriter"/> puts around every comparison and for any a caller writes by hand.
    /// A query whose condition is within the limit is never refused here.
    /// </para>
    /// </summary>
    private const int MaxSyntaxDepth = 64;

    private readonly List<QueryToken> Tokens;
    private readonly string Query;
    private int Index;
    private int Depth;

    private QueryParser(List<QueryToken> tokens, string query)
    {
        Tokens = tokens;
        Query = query;
    }

    /// <summary>
    /// Parse a query string into a condition tree.
    /// </summary>
    /// <param name="query"></param>
    /// <returns>null if the query is empty or whitespace</returns>
    /// <exception cref="WeequeryException">
    /// the query is malformed, or the condition it describes nests deeper than
    /// <see cref="ConditionNesting.MaxDepth"/>
    /// </exception>
    public static ICondition? Parse(string query)
    {
        var tokens = QueryTokenizer.Tokenize(query);
        if (tokens.Count == 0) { return null; }

        var condition = ParseLeading(tokens, query, out var stopped);

        // Anything left over means the query was not a single well-formed expression (eg. "(A) (B)")
        if (stopped < tokens.Count)
        {
            throw new WeequeryException(QueryText.Describe(query, $"Unexpected '{tokens[stopped].Text}'", tokens[stopped].Position));
        }

        return condition;
    }

    /// <summary>
    /// Read the condition at the front of a token stream, stopping wherever it ends rather than insisting it is
    /// the whole of the text.
    /// </summary>
    /// <remarks>
    /// What <see cref="ParsedQuery"/> needs to split a combined string: where the condition stops is where the
    /// sort clause begins, and letting the parser find it is exact where searching the text for ORDER BY would
    /// not be. A value can spell anything, so <c>Name == 'ORDER BY'</c> holds those two words without the query
    /// having a sort clause at all.
    /// </remarks>
    /// <param name="tokens">at least one, since an empty stream has no condition to read</param>
    /// <param name="query">the text the tokens came from, for the errors to point into</param>
    /// <param name="stopped">index of the first token the condition did not take, so tokens.Count when it took them all</param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">the condition is malformed, or nests too deep</exception>
    internal static ICondition? ParseLeading(List<QueryToken> tokens, string query, out int stopped)
    {
        var parser = new QueryParser(tokens, query);

        var condition = parser.ParseDisjunction();

        stopped = parser.Index;

        if (ConditionNesting.IsTooDeep(condition))
        {
            throw new WeequeryException($"{ConditionNesting.TooDeep().Message}: '{QueryText.Excerpt(query, 0)}'");
        }

        return condition;
    }

    private bool AtEnd { get { return Index >= Tokens.Count; } }

    private QueryToken Current { get { return Tokens[Index]; } }

    private bool Check(QueryTokenKind kind)
    {
        return (!AtEnd) && (Current.Kind == kind);
    }

    private bool Match(QueryTokenKind kind)
    {
        if (!Check(kind)) { return false; }

        Index++;
        return true;
    }

    private QueryToken Take(QueryTokenKind kind, string expected)
    {
        if (!Check(kind)) { throw new WeequeryException(Describe($"Expected {expected}", PositionOfCurrentOrEnd)); }

        return Tokens[Index++];
    }

    private int PositionOfCurrentOrEnd { get { return AtEnd ? Query.Length : Current.Position; } }

    /// <summary>
    /// Build an error message that points at the offending part of the query, see <see cref="QueryText"/>
    /// </summary>
    private string Describe(string message, int position)
    {
        return QueryText.Describe(Query, message, position);
    }

    private ICondition ParseDisjunction()
    {
        var first = ParseConjunction();
        if (!Check(QueryTokenKind.Or)) { return first; }

        // Flatten 'A || B || C' into a single Or over three operands rather than nesting
        List<ICondition> operands = new() { first };
        while (Match(QueryTokenKind.Or)) { operands.Add(ParseConjunction()); }

        return new ConjunctionCondition(Operator.Or, operands);
    }

    private ICondition ParseConjunction()
    {
        var first = ParseUnary();
        if (!Check(QueryTokenKind.And)) { return first; }

        List<ICondition> operands = new() { first };
        while (Match(QueryTokenKind.And)) { operands.Add(ParseUnary()); }

        return new ConjunctionCondition(Operator.And, operands);
    }

    private ICondition ParseUnary()
    {
        // Right associative, so '!!A' is legal and means A
        if (Check(QueryTokenKind.Not))
        {
            var position = Current.Position;
            Index++;

            Descend(position);
            var operand = ParseUnary();
            Depth--;

            return new NotCondition(Operator.Not, operand);
        }

        return ParsePrimary();
    }

    private ICondition ParsePrimary()
    {
        if (Check(QueryTokenKind.GroupOpen))
        {
            var position = Current.Position;
            Index++;

            Descend(position);
            var inner = ParseDisjunction();
            Depth--;

            Take(QueryTokenKind.GroupClose, "')'");

            return inner;
        }

        return ParseComparison();
    }

    /// <summary>
    /// Step into one more level of text, refusing to go past <see cref="MaxSyntaxDepth"/>. Paired with a
    /// decrement of <see cref="Depth"/> once the nested expression has been read, so the count is of enclosing
    /// levels rather than of levels seen.
    /// </summary>
    /// <param name="position">index of the token that opened the level, so the error points at it</param>
    /// <exception cref="WeequeryException">the text nests deeper than the limit</exception>
    private void Descend(int position)
    {
        if (Depth >= MaxSyntaxDepth)
        {
            throw new WeequeryException(Describe($"Grouping nested deeper than the limit of {MaxSyntaxDepth}", position));
        }

        Depth++;
    }

    private ICondition ParseComparison()
    {
        string field = ParseField();
        Operator op = ParseOperator(field);
        var required = ConditionFunctions.GetNumberOfValuesRequiredForOperation(op);

        // 'X == null' is an accepted spelling of 'X IsNull'. Only an unquoted null counts, so a
        // string field can still be compared against the literal text 'null'.
        if (((op == Operator.Equals) || (op == Operator.NotEqual))
            && Check(QueryTokenKind.Word)
            && string.Equals(Current.Text, "null", StringComparison.OrdinalIgnoreCase))
        {
            Index++;
            return new NoValueCondition((op == Operator.Equals) ? Operator.IsNull : Operator.IsNotNull, field);
        }

        // Brackets among the operands make an operand a comparison against another bound property rather than
        // against text, whatever the operator: "Pay > [Salary]", "Pay IsBetween ([Floor], [Ceiling])",
        // "Pay IsIn (1, [Cap])". The operator settles the rest: how many operands it holds is which of the four
        // comparison types it becomes, see ConditionShape.
        var operands = ParseOperands(field, op, required);

        return ConditionFunctions.BuildComparison(op, field, operands);
    }

    /// <summary>
    /// A field is a bare word (which may be a dotted property path) or a bracket quoted word, so that the
    /// output of a conditions ToString can be fed back in.
    /// </summary>
    private string ParseField()
    {
        if (Match(QueryTokenKind.BracketOpen))
        {
            var bracketed = Take(QueryTokenKind.Word, "a field name");
            Take(QueryTokenKind.BracketClose, "']'");

            return bracketed.Text;
        }

        // A quoted field name. Binding keys reject whitespace, but they can still hold punctuation the tokenizer
        // treats as a delimiter, and a condition can name a field that no binding has claimed, so a condition
        // written out by QueryWriter may need this to read back. Unambiguous: a comparison always starts with a
        // field, so a quoted string here can only be one.
        if (Check(QueryTokenKind.Text)) { return Tokens[Index++].Text; }

        return Take(QueryTokenKind.Word, "a field name").Text;
    }

    /// <summary>
    /// The SQL operators that are written as more than one word, so cannot be looked up by a single token.
    /// <para>
    /// Unambiguous here: this is only ever called where an operator is expected, and a field has already been read,
    /// so a NOT in this position cannot be a negation and an IS cannot be anything else.
    /// </para>
    /// </summary>
    /// <returns>false if the tokens at hand are not one of these phrases</returns>
    private bool TryParseSqlPhrase(string field, out Operator op)
    {
        op = default;

        // IS NULL, IS NOT NULL
        if (Check(QueryTokenKind.Word) && string.Equals(Current.Text, "IS", StringComparison.OrdinalIgnoreCase))
        {
            var isPosition = Current.Position;
            Index++;

            var negated = Match(QueryTokenKind.Not);

            if (!(Check(QueryTokenKind.Word) && string.Equals(Current.Text, "NULL", StringComparison.OrdinalIgnoreCase)))
            {
                throw new WeequeryException(Describe($"Expected NULL after IS{(negated ? " NOT" : string.Empty)} for field '{field}'", isPosition));
            }

            Index++;
            op = negated ? Operator.IsNotNull : Operator.IsNull;
            return true;
        }

        // NOT IN, NOT BETWEEN
        if (Check(QueryTokenKind.Not))
        {
            var notPosition = Current.Position;
            Index++;

            if (Check(QueryTokenKind.Word) && string.Equals(Current.Text, "IN", StringComparison.OrdinalIgnoreCase))
            {
                Index++;
                op = Operator.IsNotIn;
                return true;
            }

            if (Check(QueryTokenKind.Word) && string.Equals(Current.Text, "BETWEEN", StringComparison.OrdinalIgnoreCase))
            {
                Index++;
                op = Operator.IsNotBetween;
                return true;
            }

            throw new WeequeryException(Describe($"Expected IN or BETWEEN after NOT for field '{field}'", notPosition));
        }

        return false;
    }

    private Operator ParseOperator(string field)
    {
        if (AtEnd) { throw new WeequeryException(Describe($"Expected an operator for field '{field}'", Query.Length)); }

        if (TryParseSqlPhrase(field, out var phrase)) { return phrase; }

        var token = Current;
        if ((token.Kind != QueryTokenKind.Symbol) && (token.Kind != QueryTokenKind.Word))
        {
            throw new WeequeryException(Describe($"Expected an operator for field '{field}' but found '{token.Text}'", token.Position));
        }

        if (!OperatorLookup.TryGetValue(token.Text, out var op))
        {
            throw new WeequeryException(Describe($"Unknown operator '{token.Text}'", token.Position));
        }

        Index++;
        return op;
    }

    /// <summary>
    /// Read the operands for a comparison and check the count against what the operator accepts.
    /// </summary>
    /// <remarks>
    /// Each operand comes back as the text it was written as, carrying whether the brackets made it the name of a
    /// property, which is the form a condition holds its operands in, see <see cref="ConditionValue{T}"/>.
    /// </remarks>
    /// <param name="field"></param>
    /// <param name="op"></param>
    /// <param name="required"></param>
    /// <returns></returns>
    private List<ConditionValue<string>> ParseOperands(string field, Operator op, ConditionFunctions.NumberOfValuesRequired required)
    {
        List<ConditionValue<string>> values = new();

        // One operand, as whichever of the two the brackets said it was
        void Read()
        {
            var text = ParseOperand(field, out var isNamed);

            values.Add(isNamed ? ConditionValue.Binding(text) : ConditionValue.Raw(text));
        }

        if (required.Maximum == 0)
        {
            // IsNull / IsNotNull take no value at all
            return values;
        }

        int position = PositionOfCurrentOrEnd;

        // A list is written in parentheses, ('a', 'b'), which is what a conditions ToString emits, so a
        // printed condition reads back. In operand position a '(' can only ever start a list, so there is nothing
        // to disambiguate here; a '[' is always a property name, see ParseOperand.
        if (Check(QueryTokenKind.GroupOpen))
        {
            Index++;

            if (!Check(QueryTokenKind.GroupClose))
            {
                Read();
                while (Match(QueryTokenKind.Separator)) { Read(); }
            }

            Take(QueryTokenKind.GroupClose, "',' or ')'");
        }
        else if ((op == Operator.IsBetween) || (op == Operator.IsNotBetween))
        {
            // SQL writes a range as "BETWEEN low AND high" rather than as a list. In operand position that AND can
            // only be the separator, so there is nothing to disambiguate, and a following AND is still read as the
            // conjunction: "Pay BETWEEN 1 AND 5 AND IsActive == true" splits where SQL splits it.
            Read();

            if (Match(QueryTokenKind.And)) { Read(); }
        }
        else
        {
            Read();
        }

        if (values.Count < required.Minimum)
        {
            throw new WeequeryException(Describe($"Operator '{ConditionFunctions.GetOperationString(op)}' on field '{field}' needs at least {required.Minimum} value(s) but got {values.Count}", position));
        }

        if (values.Count > required.Maximum)
        {
            throw new WeequeryException(Describe($"Operator '{ConditionFunctions.GetOperationString(op)}' on field '{field}' accepts at most {required.Maximum} value(s) but got {values.Count}", position));
        }

        return values;
    }

    /// <summary>
    /// One thing a field can be compared against: a literal, or a bracketed name that says the operand is another
    /// bound property rather than a value, see <see cref="ConditionValue{T}"/>.
    /// </summary>
    /// <param name="field"></param>
    /// <param name="isNamed">true if the operand named a bound property rather than being a value</param>
    /// <returns>the text either way, which is the name for a property</returns>
    private string ParseOperand(string field, out bool isNamed)
    {
        isNamed = false;

        if (!Check(QueryTokenKind.BracketOpen)) { return ParseLiteral(field); }

        isNamed = true;

        var open = Current.Position;
        Index++;

        // The brackets hold one bare name and nothing else. A list in them is the shape someone reaching for one
        // is most likely to write, so it gets its own message: "Expected ']'" would not say where to go instead.
        if (Check(QueryTokenKind.Text)) { throw new WeequeryException(ListInBrackets(field, open)); }

        var name = Take(QueryTokenKind.Word, $"the name of a bound property for field '{field}'").Text;

        if (Check(QueryTokenKind.Separator)) { throw new WeequeryException(ListInBrackets(field, open)); }

        Take(QueryTokenKind.BracketClose, "']'");

        return name;
    }

    /// <summary>
    /// The message for a list written in brackets, which is what the language used to accept and now does not
    /// </summary>
    private string ListInBrackets(string field, int position)
    {
        return Describe($"The list of values for '{field}' must be contained in parentheses, as (a, b)", position);
    }

    private string ParseLiteral(string field)
    {
        if (AtEnd) { throw new WeequeryException(Describe($"Expected a value for field '{field}'", Query.Length)); }

        var token = Current;
        if ((token.Kind != QueryTokenKind.Word) && (token.Kind != QueryTokenKind.Text))
        {
            throw new WeequeryException(Describe($"Expected a value for field '{field}' but found '{token.Text}'", token.Position));
        }

        Index++;
        return token.Text;
    }
}
