namespace Weequery;

internal enum QueryTokenKind
{
    /// <summary>Grouping open, '('</summary>
    GroupOpen,

    /// <summary>Grouping close, ')'</summary>
    GroupClose,

    /// <summary>Name open, '['. Quotes a field name, or names a bound property where a value is expected.</summary>
    BracketOpen,

    /// <summary>Name close, ']'</summary>
    BracketClose,

    /// <summary>Value list separator, ','</summary>
    Separator,

    /// <summary>'&amp;&amp;' or 'AND'</summary>
    And,

    /// <summary>'||' or 'OR'</summary>
    Or,

    /// <summary>'!' or 'NOT'</summary>
    Not,

    /// <summary>Comparison symbol, already normalized to its canonical form (so '=' arrives as '==')</summary>
    Symbol,

    /// <summary>Unquoted run of characters: a field name, a named operator, or a bare literal</summary>
    Word,

    /// <summary>Quoted literal, with escapes already resolved</summary>
    Text,
}
