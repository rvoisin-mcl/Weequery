namespace Weequery;

/// <param name="Kind">What sort of token this is</param>
/// <param name="Text">Token content. For <see cref="QueryTokenKind.Text"/> the surrounding quotes are stripped and escapes resolved</param>
/// <param name="Position">Index into the source query the token started at, used for error reporting</param>
internal readonly record struct QueryToken(QueryTokenKind Kind, string Text, int Position);
