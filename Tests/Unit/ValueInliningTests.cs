using System.Linq.Expressions;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// A compiled delegate reads its values as constants rather than out of the boxes that exist to become query
/// parameters, and selects exactly what the uncompiled expression selects.
/// </summary>
/// <remarks>
/// <para>
/// The boxes are what make EF Core parameterize a value instead of writing it into the SQL, which is what gives
/// one query plan per filter shape rather than one per filter value. Nothing reads them where the predicate is
/// compiled and run here, so that path writes the values in directly: cheaper to compile, cheaper per row, and
/// the same answer.
/// </para>
/// <para>
/// <b>"The same answer" is the whole of what has to be proved</b>, so most of this file is the same condition
/// run both ways over the same rows. The interesting failures would be quiet ones — a value arriving as its
/// underlying type instead of its enum, a null failing to construct, a converted accessor rewritten by accident —
/// which is why the cases below are a type per test rather than one condition.
/// </para>
/// </remarks>
public class ValueInliningTests
{
    private static readonly List<Minion> Minions = [.. MinionTestData.Minions()];

    /// <summary>
    /// The names a condition selects through the compiled delegate, and the names the same condition selects
    /// through the expression compiled as it was built. These must not differ.
    /// </summary>
    private static void Agree(string query)
    {
        var condition = ConditionFunctions.ParseQuery(query)!;

        var inlined = Inquiry<Minion>.BuildDelegate(Minion.Bindings, condition);
        var boxed = Inquiry<Minion>.BuildExpression(Minion.Bindings, condition).Compile();

        string[] throughDelegate = [.. from minion in Minions where inlined(minion) select minion.Name];
        string[] throughExpression = [.. from minion in Minions where boxed(minion) select minion.Name];

        Assert.Equal(throughExpression, throughDelegate);
    }

    [Theory]
    [InlineData("Pay > 10000")]
    [InlineData("Pay IsBetween (8000, 12000)")]
    [InlineData("Pay = 0")]
    [InlineData("Morale < 0")]
    [InlineData("Morale IsIn (5, -3, 127)")]
    public void ANumericValueSelectsTheSameRowsInlinedAsBoxed(string query)
    {
        Agree(query);
    }

    [Theory]
    [InlineData("Name StartsWith 'Al'")]
    [InlineData("Name Contains 'i'")]
    [InlineData("Name EndsWith 'Fox'")]
    [InlineData("Alias = 'Ghost'")]
    [InlineData("Alias <> 'Ghost'")]
    [InlineData("Alias IsNull")]
    [InlineData("Alias IsIn ('Ghost', 'Snake')")]
    [InlineData("Name IsMatch '^Ali'")]
    [InlineData("Name DoesNotMatch '^Ali'")]
    public void AStringValueSelectsTheSameRowsInlinedAsBoxed(string query)
    {
        Agree(query);
    }

    [Theory]
    [InlineData("IsActive = true")]
    [InlineData("IsActive = false")]
    [InlineData("IsVetted = true")]
    [InlineData("IsVetted IsNull")]
    public void ABooleanValueSelectsTheSameRowsInlinedAsBoxed(string query)
    {
        Agree(query);
    }

    [Theory]
    [InlineData("HireDate > 2020-01-01")]
    [InlineData("BirthDate < 1995-01-01")]
    [InlineData("FireDate IsNotNull")]
    [InlineData("ReviewDate > 2024-01-01")]
    [InlineData("ShiftStart < 12:00:00")]
    public void ADateOrTimeValueSelectsTheSameRowsInlinedAsBoxed(string query)
    {
        Agree(query);
    }

    /// <summary>
    /// An enum is the case most likely to break quietly: the comparison converts both sides to the underlying
    /// type, so a value written back as an int rather than as the enum would still compile and still run.
    /// </summary>
    [Theory]
    [InlineData("Classification = 'Irreplacable'")]
    [InlineData("Classification <> 'Irreplacable'")]
    [InlineData("Classification IsIn ('Irreplacable', 'Expendible')")]
    [InlineData("Classification > 'Expendible'")]
    public void AnEnumValueSelectsTheSameRowsInlinedAsBoxed(string query)
    {
        Agree(query);
    }

    [Theory]
    [InlineData("(IsActive = true AND Pay > 5000) OR Alias IsNull")]
    [InlineData("NOT (Alias = 'Ghost')")]
    [InlineData("IsActive = true AND Pay > 10000 AND Alias StartsWith 'G'")]
    public void ACompoundConditionSelectsTheSameRowsInlinedAsBoxed(string query)
    {
        Agree(query);
    }

    /// <summary>
    /// A comparison of two properties has no value to box on either side, so there is nothing here to rewrite and
    /// the accessors must be left exactly as they are.
    /// </summary>
    [Fact]
    public void AFieldComparisonIsUntouched()
    {
        Agree("HireDate < [FireDate]");
    }

    /// <summary>
    /// A value that spells a null is a structural constant rather than a caller's value, so it was never boxed
    /// and there is nothing here for the rewrite to find. Worth pinning: a rewrite that reached the wrong
    /// constants would break exactly this.
    /// </summary>
    [Fact]
    public void ANullTestIsUntouched()
    {
        var condition = ConditionFunctions.ParseQuery("Alias IsNull")!;

        Assert.Equal(0, BoxesIn(Inquiry<Minion>.BuildExpression(Minion.Bindings, condition)));

        Agree("Alias IsNull");
    }

    /// <summary>
    /// The bounded IsMatch overload the in-memory path swaps in carries constants of its own, put there after the
    /// expression was built. Inlining runs last so it sees them, and must leave them alone.
    /// </summary>
    [Fact]
    public void ABoundedMatchStillMatches()
    {
        Agree("Name IsMatch 'Fox$'");
        Agree("Name DoesNotMatch 'Fox$'");
    }

    // ---------- and the boxes stay where a provider will read them ----------

    private sealed class BoxHunter : ExpressionVisitor
    {
        public int Found;

        protected override Expression VisitConstant(ConstantExpression node)
        {
            if ((node.Value is not null) && node.Value.GetType().Name.StartsWith("ValueBox")) { Found++; }

            return base.VisitConstant(node);
        }
    }

    private static int BoxesIn(Expression expression)
    {
        var hunter = new BoxHunter();
        hunter.Visit(expression);

        return hunter.Found;
    }

    /// <summary>
    /// The regression this guards, and the reason the rewrite is on one path rather than everywhere: an
    /// expression bound for a provider must still reach its values indirectly, or EF writes them into the SQL as
    /// literals and every distinct value gets a query plan of its own. See EFParameterizationTests for what that
    /// costs.
    /// </summary>
    [Fact]
    public void AnExpressionBuiltForAProviderKeepsItsBoxes()
    {
        var condition = ConditionFunctions.ParseQuery("IsActive = true AND Pay > 10000 AND Alias StartsWith 'G'")!;

        Assert.Equal(3, BoxesIn(Inquiry<Minion>.BuildExpression(Minion.Bindings, condition)));
    }

    /// <summary>
    /// And the query an Inquiry hands back keeps them too, however it was built.
    /// </summary>
    [Fact]
    public void TheQueryAnInquiryBuildsKeepsItsBoxes()
    {
        var query = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("Pay > 10000")
            .Build();

        Assert.Equal(1, BoxesIn(query.Expression));
    }
}
