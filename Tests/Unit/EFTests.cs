using Tests.Common;
using Weequery;

namespace Tests.Unit;

public class EFTests
{
    public static readonly BindingRequest[] LairBindings =
    [
        new(nameof(Lair.LairID), null),
        new(nameof(Lair.Name), null),
        new(nameof(Lair.Capacity), null),
    ];

    public static readonly BindingRequest[] AssignmentBindings =
    [
        new(nameof(LairAssignment.LairID), null),
        new(nameof(LairAssignment.Lair.Name), "Lair"),
        new(nameof(LairAssignment.MinionID), null),
        new(nameof(LairAssignment.Minion.Name), "Minion"),
    ];

    [Fact]
    public void DoesExpressionBehaveCorrectly()
    {
        var context = DBContext.GenerateMinionTestSet();

        try
        {
            var cond = new OneValueCondition<bool>(Operator.Equals, nameof(Minion.IsActive), true);
            var exp = Weequery.Inquiry<Minion>.BuildExpression(Minion.Bindings, cond);

            Assert.True(context.Minions.WithWeequery().BindProperties(Minion.Bindings).ApplyCondition(cond).Build().Any());
            //Assert.True(context.Where(exp).Count() == 3);
        }
        finally
        {
            // Seeded databases are files, and one left behind per run adds up
            TestDatabase.Drop(context);
        }
    }
}