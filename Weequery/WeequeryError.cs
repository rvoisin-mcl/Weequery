namespace Weequery;

/// <summary>
/// Why a <see cref="WeequeryException"/> was thrown, as something a caller can branch on rather than read.
/// </summary>
/// <remarks>
/// <para>
/// A message names the offending input and is written to be read by a person, which means it is free to change
/// when a clearer wording turns up. Anything that has to <b>act</b> on the reason — a handler deciding between a
/// 400 and a 500, a test asserting what was refused — needs something that does not, and this is it. It also
/// reaches <see cref="System.Exception.HResult"/>, see <see cref="WeequeryException.HResultFor"/>, for the
/// callers that only see an exception across a boundary that has already thrown the type away.
/// </para>
/// <para>
/// <b>One value per kind of refusal, not per throw.</b> There are a few hundred places that refuse something and
/// far fewer reasons to; what a caller wants to know is which reason, and pinning a line number would only make
/// every future edit a breaking change. Where a test needs to tell two refusals of the same kind apart, the
/// message is still there to look at.
/// </para>
/// <para>
/// <b>These are on the wire once released.</b> A value's number is part of the HResult a caller may have written
/// down, so add to the end and do not renumber. Nothing is removed either: a reason that stops being reachable
/// leaves its number spent.
/// </para>
/// </remarks>
public enum WeequeryError
{
    /// <summary>
    /// No reason was given. What the message-only constructors produce, so an exception thrown by a caller's own
    /// code, or by a corner of this library that has not been given a reason yet, is still distinguishable from
    /// one that has.
    /// </summary>
    Unspecified = 0,

    /// <summary>A required argument was null, or was a string that was null or empty</summary>
    ArgumentMissing = 1,

    /// <summary>
    /// An argument was present and not usable: a negative page, an empty list where one element is the minimum,
    /// a window whose size and offset overflow.
    /// </summary>
    ArgumentInvalid = 2,

    /// <summary>
    /// A key is not one: not a valid unquoted SQL name, not a period separated sequence of them, holding brackets,
    /// or spelling a word the query language reads as an operator. See
    /// <see cref="WeequeryException.ThrowIfNotBindingKey"/>.
    /// </summary>
    KeyInvalid = 3,

    /// <summary>Two things claim one key, whichever of the two lookups they would land in</summary>
    KeyTaken = 4,

    /// <summary>
    /// A condition, sort or projection named something nothing declared. The one a request handler most wants to
    /// tell apart, since it is the caller's fault and nobody else's.
    /// </summary>
    UnboundField = 5,

    /// <summary>
    /// A property path or a selector does not describe a reachable property: malformed text, an index that is not
    /// a constant, an index on something that is not a collection, a selector that is not a chain of members.
    /// </summary>
    PathInvalid = 6,

    /// <summary>
    /// A binding cannot be made or cannot be used as asked: a property type nothing can build against, a
    /// converter declared for a different type, a constant asked to sort.
    /// </summary>
    BindingInvalid = 7,

    /// <summary>
    /// The operator is a real one and does not apply here: ordering a bool, a substring test on a number, a
    /// quantifier over something that is not a bound collection.
    /// </summary>
    OperatorUnsupported = 8,

    /// <summary>
    /// The operator is not one at all in this position: a value outside the enumeration, a conjunction holding
    /// something other than AND or OR, a quantifier expected and something else found.
    /// </summary>
    OperatorInvalid = 9,

    /// <summary>The operator was given the wrong number of operands</summary>
    OperandCount = 10,

    /// <summary>
    /// A value will not become what the property holds: text that is not a number, a date, a member of the
    /// enumeration, or that parses to nothing.
    /// </summary>
    ValueInvalid = 11,

    /// <summary>A <see cref="ValueConverter"/> threw, or turned a value into nothing</summary>
    ConversionFailed = 12,

    /// <summary>A query string will not parse: an unterminated quote, a missing operand, text left over</summary>
    QuerySyntax = 13,

    /// <summary>A condition nests deeper than <see cref="ConditionNesting.MaxDepth"/></summary>
    NestingTooDeep = 14,

    /// <summary>
    /// The condition is valid and the target cannot say it: an operator with no equivalent in the Query DSL or in
    /// OData, a quantifier inside a quantifier where the target addresses one path at a time.
    /// </summary>
    NotTranslatable = 15,

    /// <summary>
    /// A guard on something that should not be reachable. Not the caller's fault and not worth catching: if one
    /// of these arrives, the bug is here.
    /// </summary>
    Internal = 16,

    /// <summary>
    /// The call is a real one and cannot be made here: two features asked for that contradict each other, or a
    /// step taken in an order that leaves nothing sensible to do. The programmer's mistake rather than the
    /// caller's, so worth telling apart from the reasons a request handler answers with a bad request.
    /// </summary>
    UsageInvalid = 17,
}
