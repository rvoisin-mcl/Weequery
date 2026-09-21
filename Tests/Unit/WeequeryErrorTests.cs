using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// The reason an exception carries, which is what a caller branches on and what a test asserts instead of reading
/// the message.
/// </summary>
/// <remarks>
/// A message names the offending input and is written for a person, so it is free to be reworded. Anything that
/// has to act on the reason needs something that is not, and that is <see cref="WeequeryError"/>, reachable as
/// <see cref="WeequeryException.Error"/>, and as <see cref="System.Exception.HResult"/> for the callers who only
/// see the base type.
/// </remarks>
public class WeequeryErrorTests
{
    private static WeequeryError[] All()
    {
        return Enum.GetValues<WeequeryError>();
    }

    // ---------- the numbers, which are a promise ----------

    /// <summary>
    /// Pinned one by one rather than counted, so adding a reason is free and renumbering one is not.
    /// </summary>
    /// <remarks>
    /// The values reach callers inside an HResult, which is a number someone may have written down in a handler
    /// or a log filter. Moving one silently changes what an old comparison matches. Append and the test passes;
    /// reorder and it does not, which is the whole point of it.
    /// </remarks>
    [Fact]
    public void TheNumbersAreOnTheWireAndDoNotMove()
    {
        Assert.Equal(0, (int)WeequeryError.Unspecified);
        Assert.Equal(1, (int)WeequeryError.ArgumentMissing);
        Assert.Equal(2, (int)WeequeryError.ArgumentInvalid);
        Assert.Equal(3, (int)WeequeryError.KeyInvalid);
        Assert.Equal(4, (int)WeequeryError.KeyTaken);
        Assert.Equal(5, (int)WeequeryError.UnboundField);
        Assert.Equal(6, (int)WeequeryError.PathInvalid);
        Assert.Equal(7, (int)WeequeryError.BindingInvalid);
        Assert.Equal(8, (int)WeequeryError.OperatorUnsupported);
        Assert.Equal(9, (int)WeequeryError.OperatorInvalid);
        Assert.Equal(10, (int)WeequeryError.OperandCount);
        Assert.Equal(11, (int)WeequeryError.ValueInvalid);
        Assert.Equal(12, (int)WeequeryError.ConversionFailed);
        Assert.Equal(13, (int)WeequeryError.QuerySyntax);
        Assert.Equal(14, (int)WeequeryError.NestingTooDeep);
        Assert.Equal(15, (int)WeequeryError.NotTranslatable);
        Assert.Equal(16, (int)WeequeryError.Internal);
        Assert.Equal(17, (int)WeequeryError.UsageInvalid);
    }

    [Fact]
    public void NoTwoReasonsShareANumber()
    {
        Assert.Equal(All().Length, All().Distinct().Count());
        Assert.Equal(All().Length, (from error in All() select WeequeryException.HResultFor(error)).Distinct().Count());
    }

    // ---------- the HResult layout, which is not ours to choose ----------

    /// <summary>
    /// Bit 31 says failure and bit 29 says customer defined. Without the second one the number would be claiming
    /// to be a Microsoft code, and the facility would mean something it does not.
    /// </summary>
    [Fact]
    public void EveryHResultIsAFailureAndSaysItIsOurs()
    {
        foreach (var error in All())
        {
            var hresult = unchecked((uint)WeequeryException.HResultFor(error));

            Assert.True((hresult & 0x80000000) != 0, $"{error} is not marked as a failure");
            Assert.True((hresult & 0x20000000) != 0, $"{error} is not marked as customer defined");
            Assert.Equal(0x004u, (hresult >> 16) & 0x1FFF);
        }
    }

    [Fact]
    public void TheHResultIsTheReasonInTheLowBits()
    {
        Assert.Equal(unchecked((int)0xA0040005), WeequeryException.HResultFor(WeequeryError.UnboundField));
        Assert.Equal(unchecked((int)0xA0040000), WeequeryException.HResultFor(WeequeryError.Unspecified));
    }

    // ---------- what an exception carries ----------

    [Fact]
    public void AnExceptionReportsTheReasonBothWays()
    {
        var error = new WeequeryException(WeequeryError.UnboundField, "nobody declared it");

        Assert.Equal(WeequeryError.UnboundField, error.Error);
        Assert.Equal(WeequeryException.HResultFor(WeequeryError.UnboundField), error.HResult);
        Assert.Equal("nobody declared it", error.Message);
    }

    [Fact]
    public void AnInnerExceptionSurvivesTheReason()
    {
        var inner = new InvalidOperationException("underneath");
        var error = new WeequeryException(WeequeryError.ConversionFailed, "on top", inner);

        Assert.Equal(WeequeryError.ConversionFailed, error.Error);
        Assert.Equal(WeequeryException.HResultFor(WeequeryError.ConversionFailed), error.HResult);
        Assert.Same(inner, error.InnerException);
    }

    /// <summary>
    /// The constructors that predate the reason still work and say so, rather than claiming a reason they were
    /// never given
    /// </summary>
    [Fact]
    public void TheMessageOnlyConstructorsReportUnspecified()
    {
        Assert.Equal(WeequeryError.Unspecified, new WeequeryException("no reason given").Error);
        Assert.Equal(WeequeryError.Unspecified, new WeequeryException("no reason given", new Exception()).Error);
    }

    // ---------- and what the library actually throws ----------

    /// <summary>
    /// End to end, over the paths a caller's input reaches: the reason is set where it is thrown, not only where
    /// it is constructed by hand.
    /// </summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public void ARefusalCarriesItsReason(WeequeryError expected, string _, Action refuse)
    {
        Assert.Equal(expected, Assert.Throws<WeequeryException>(refuse).Error);
    }

    public static TheoryData<WeequeryError, string, Action> Refusals()
    {
        return new()
        {
            { WeequeryError.UnboundField, "a condition naming nothing", () => Bound().ApplyCondition("Nonexistent = 1").Build() },
            { WeequeryError.UnboundField, "a sort naming nothing", () => Bound().ApplySorts("Nonexistent").Build() },
            { WeequeryError.QuerySyntax, "a query that will not parse", () => ConditionFunctions.ParseQuery("Pay >") },
            { WeequeryError.QuerySyntax, "an unterminated quote", () => ConditionFunctions.ParseQuery("Name = 'Al") },
            { WeequeryError.ValueInvalid, "text where a number belongs", () => Bound().ApplyCondition("Pay > 'lots'").Build() },
            { WeequeryError.OperatorUnsupported, "ordering a bool", () => Bound().ApplyCondition("IsActive > true").Build() },
            { WeequeryError.KeyInvalid, "a key the language has claimed", () => new BindingRequest(nameof(Minion.Name), "And") },
            { WeequeryError.KeyTaken, "two bindings under one key", () => Bound().BindProperty(minion => minion.Pay, nameof(Minion.Name)) },
            { WeequeryError.ArgumentMissing, "a null where one is required", () => Bound().ApplySorts((IEnumerable<Sort>)[null!]) },
            { WeequeryError.ArgumentInvalid, "a page size and page that cannot be combined", () => Bound().ApplyPagination(1000, int.MaxValue) },
            { WeequeryError.PathInvalid, "a path that resolves to nothing", () => Bound().BindProperty("Nowhere.At.All") },
            { WeequeryError.NestingTooDeep, "a condition past the depth limit", () => ConditionFunctions.ParseQuery(TooDeep()) },
        };
    }

    private static Inquiry<Minion> Bound()
    {
        return MinionTestData.Minions().WithWeequery().BindProperties(Minion.Bindings);
    }

    /// <summary>
    /// A query nested past what <see cref="ConditionNesting.MaxDepth"/> allows.
    /// </summary>
    /// <remarks>
    /// Each level has to hold a conjunction of its own. Brackets alone will not do it: parentheses around a
    /// single comparison group the text without nesting the condition, so they collapse and the tree stays one
    /// deep however many of them are written.
    /// </remarks>
    /// <returns></returns>
    private static string TooDeep()
    {
        var query = "Pay > 0";

        for (int level = 1; level <= (ConditionNesting.MaxDepth + 2); level++)
        {
            query = $"(Pay > {level} AND {query})";
        }

        return query;
    }
}
