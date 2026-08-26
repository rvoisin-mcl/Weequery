using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;
using Weequery.Interfaces;

namespace Tests.Unit;

/// <summary>
/// A bound property can be one of the things a range or a list compares against, and the operands can be mixed:
/// "Pay IsBetween (8000, [Ceiling])" is a range with one end from the query and one from the row.
/// <para>
/// The brackets mean the same thing here as they do for a single comparison, which is what let the list operators
/// have them: a list is written in parentheses, so a bracket is always a property wherever it appears.
/// </para>
/// </summary>
public class FieldRangeAndListTests
{
    private static Inquiry<Minion> Query()
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .BindConstant("Floor", 8000m)
            .BindConstant("Ceiling", 12000m);
    }

    private static string[] Matching(string query)
    {
        return Query()
            .ApplyCondition(query)
            .Build()
            .ToList()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();
    }

    // Pay: Alice 12000, Bob 0, Charlie 19000, David 8000

    // ---------- a range ----------

    [Fact]
    public void ARangeCanBeBoundedByProperties()
    {
        Assert.Equal(["Alice", "David"], Matching("Pay IsBetween ([Floor], [Ceiling])"));
        Assert.Equal(["Bob", "Charlie"], Matching("Pay IsNotBetween ([Floor], [Ceiling])"));
    }

    /// <summary>
    /// Only one end has to be a property, and the other is read and parameterized as any other value is
    /// </summary>
    [Fact]
    public void OneEndOfARangeIsEnough()
    {
        Assert.Equal(["Alice", "David"], Matching("Pay IsBetween (8000, [Ceiling])"));
        Assert.Equal(["Alice", "David"], Matching("Pay IsBetween ([Floor], 12000)"));
    }

    /// <summary>
    /// The SQL spelling reads the brackets too, since they are read where an operand is expected rather than
    /// anywhere in particular
    /// </summary>
    [Fact]
    public void TheSqlSpellingOfARangeTakesThemToo()
    {
        Assert.Equal(["Alice", "David"], Matching("Pay BETWEEN [Floor] AND [Ceiling]"));
        Assert.Equal(["Bob", "Charlie"], Matching("Pay NOT BETWEEN [Floor] AND [Ceiling]"));
    }

    /// <summary>
    /// The range that made this worth having: one bounded by the row's own values rather than by anything the
    /// caller knows
    /// </summary>
    [Fact]
    public void ARangeCanBeBoundedByTheRowItself()
    {
        var born = MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("BirthDate IsBetween (2000-01-01, [HireDate])")
            .Build()
            .Select(minion => minion.Name.Split(' ')[0])
            .Order()
            .ToArray();

        Assert.Equal(["Alice", "David"], born);
    }

    // ---------- a list ----------

    [Fact]
    public void AListCanHoldProperties()
    {
        Assert.Equal(["Alice", "David"], Matching("Pay IsIn ([Floor], [Ceiling])"));
        Assert.Equal(["Bob", "Charlie"], Matching("Pay IsNotIn ([Floor], [Ceiling])"));
    }

    [Fact]
    public void AListCanMixValuesAndProperties()
    {
        Assert.Equal(["Alice", "David"], Matching("Pay IsIn (8000, [Ceiling])"));
        Assert.Equal(["Alice", "Charlie", "David"], Matching("Pay IsIn (8000, 19000, [Ceiling])"));
        Assert.Equal(["Bob"], Matching("Pay IsNotIn (8000, 19000, [Ceiling])"));
    }

    /// <summary>
    /// A single value is a list of one, which is the shape a property on its own arrives in
    /// </summary>
    [Fact]
    public void AListOfOnePropertyWorks()
    {
        Assert.Equal(["Alice"], Matching("Pay IsIn ([Ceiling])"));
    }

    // ---------- the null rule ----------

    /// <summary>
    /// A property with no value fails its own test rather than taking the row with it, because the tests are ORed
    /// and one that fails among others that pass changes nothing. That is what a database answers: Bob's name is
    /// in the list, so the row comes back despite his null Alias, since TRUE OR UNKNOWN is TRUE.
    /// </summary>
    [Fact]
    public void AMissingPropertyDoesNotHideARowThatMatchedAnyway()
    {
        // Bob is the one with no Alias
        Assert.Contains("Bob", Matching("Name IsIn ('Bob Samuelson', [Alias])"));

        // and it is still the only thing that matched, the null having matched nothing of its own
        Assert.Equal(["Bob"], Matching("Name IsIn ('Bob Samuelson', [Alias])"));
        Assert.Empty(Matching("Name IsIn ('Nobody At All', [Alias])"));
    }

    /// <summary>
    /// Negated, that stops holding: NOT of a failed test is a match, where NOT of SQL's UNKNOWN is still no match.
    /// So IsNotIn keeps the rule the rest of the operators follow, a row with nothing to compare matches nothing,
    /// which is what makes it agree with SQL's own NOT IN.
    /// </summary>
    [Fact]
    public void AMissingPropertyStillHidesARowFromTheNegativeOperator()
    {
        // Bob's name is not in the list, but his Alias is null, so NOT IN cannot say he is not in it either
        Assert.Equal(["Alice", "Charlie", "David"], Matching("Name IsNotIn ('Nobody At All', [Alias])"));
    }

    /// <summary>
    /// The whole of the above, checked against the database's own answer rather than against what it is expected
    /// to be: the same rows for the same question, whether Weequery hands it to a provider, evaluates it in memory,
    /// or the SQL is written out by hand.
    /// </summary>
    [Theory]
    // a value matches while a property is null, which is the case the two disagree on if IsIn is guarded in front
    [InlineData("Name IsIn ('Bob Samuelson', [Alias])", "\"Name\" IN ('Bob Samuelson') OR \"Name\" = \"Alias\"")]
    [InlineData("Name IsIn ('Nobody At All', [Alias])", "\"Name\" IN ('Nobody At All') OR \"Name\" = \"Alias\"")]
    [InlineData("Name IsIn ([Alias], [CauseForDeparture])", "\"Name\" = \"Alias\" OR \"Name\" = \"CauseForDeparture\"")]
    // and the shapes where a null still has to suppress the row
    [InlineData("Name IsNotIn ('Nobody At All', [Alias])", "\"Name\" NOT IN ('Nobody At All', \"Alias\")")]
    [InlineData("Name IsNotIn ('Bob Samuelson', [Alias])", "\"Name\" NOT IN ('Bob Samuelson', \"Alias\")")]
    [InlineData("Name IsBetween ('A', [Alias])", "\"Name\" BETWEEN 'A' AND \"Alias\"")]
    [InlineData("Name IsNotBetween ('A', [Alias])", "\"Name\" NOT BETWEEN 'A' AND \"Alias\"")]
    [InlineData("Name > [Alias]", "\"Name\" > \"Alias\"")]
    public void ItAnswersWhatTheDatabaseAnswers(string query, string where)
    {
        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
        {
            var table = context.Model.FindEntityType(typeof(Minion))!.GetTableName();

            var byHand = TestDatabase
                .HandWrittenQuery<string>(context, $"SELECT \"Name\" AS \"Value\" FROM \"{table}\" WHERE {where}")
                .ToList()
                .Order();

            var throughTheProvider = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(query)
                .Build()
                .Select(minion => minion.Name)
                .ToList()
                .Order();

            var inMemory = MinionTestData.Minions()
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(query)
                .Build()
                .Select(minion => minion.Name)
                .ToList()
                .Order();

            Assert.Equal(byHand, throughTheProvider);
            Assert.Equal(byHand, inMemory);
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }

    // ---------- what is refused ----------

    /// <summary>
    /// The same type rule a single comparison follows, since the operands go into the same comparison
    /// </summary>
    [Fact]
    public void AnOperandOfAnotherTypeIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Matching("Pay IsBetween ([Floor], [HireDate])"));
    }

    [Fact]
    public void AnUnboundPropertyAmongTheOperandsIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Matching("Pay IsIn (8000, [NotAField])"));
    }

    /// <summary>
    /// A value operand is still read against the field's type, so one that is not a value of it is refused by
    /// name rather than at the point the expression is built
    /// </summary>
    [Fact]
    public void AValueOperandIsReadAgainstTheFieldsType()
    {
        Assert.Throws<WeequeryException>(() => Matching("Pay IsBetween ('not a number', [Ceiling])"));
    }

    [Fact]
    public void TheCountOfOperandsIsStillChecked()
    {
        Assert.Throws<WeequeryException>(() => Matching("Pay IsBetween ([Floor])"));
        Assert.Throws<WeequeryException>(() => Matching("Pay IsBetween ([Floor], [Ceiling], 100)"));
    }

    // ---------- which operand is which ----------

    /// <summary>
    /// Each operand keeps what it is wherever it sits in the list, which is worth pinning: getting it wrong would
    /// compare against the name of a property, or look up a value as one.
    /// </summary>
    [Theory]
    [InlineData("Pay > [Ceiling]", "B")]
    [InlineData("Pay IsBetween (8000, [Ceiling])", "RB")]
    [InlineData("Pay IsBetween ([Floor], 12000)", "BR")]
    [InlineData("Pay IsIn ([Floor], [Ceiling])", "BB")]
    [InlineData("Pay IsIn (8000, [Ceiling], 19000)", "RBR")]
    [InlineData("Pay IsIn ([Floor], 12000, [Ceiling], 19000)", "BRBR")]
    [InlineData("Pay BETWEEN [Floor] AND 12000", "BR")]
    public void EachOperandKeepsWhatItIsWhereverItSits(string query, string expected)
    {
        var condition = Assert.IsAssignableFrom<IBoundCondition>(ConditionFunctions.ParseQuery(query));

        var operands = condition.StringifyOperands();
        var sources = string.Concat(from operand in operands select operand.NamesProperty ? "B" : "R");

        Assert.Equal(expected, sources);

        // and the text is still the text, in the order it was written
        Assert.Equal(condition.StringifyValues(), [.. operands.Select(operand => operand.Value)]);
    }

    /// <summary>
    /// A query naming no property is an ordinary comparison: the shape comes from the operator either way, and
    /// every operand says it is text, so nothing is routed to the field comparison that did not name one
    /// </summary>
    [Theory]
    [InlineData("Pay > 8000")]
    [InlineData("Pay IsIn (8000, 12000)")]
    [InlineData("Pay IsBetween (8000, 12000)")]
    [InlineData("Alias IsNull")]
    public void AQueryNamingNoPropertyIsAnOrdinaryCondition(string query)
    {
        var condition = Assert.IsAssignableFrom<IBoundCondition>(ConditionFunctions.ParseQuery(query));

        Assert.DoesNotContain(condition.StringifyOperands(), operand => operand.NamesProperty);
    }

    // ---------- round trip ----------

    [Theory]
    [InlineData("([Pay] IsBetween ([Floor], [Ceiling]))")]
    [InlineData("([Pay] IsBetween ([Floor], '12000'))")]
    [InlineData("([Pay] IsIn ('8000', [Ceiling]))")]
    [InlineData("([Pay] IsNotIn ('8000', '19000', [Ceiling]))")]
    public void ItWritesAsItReadsAndReadsBackTheSame(string query)
    {
        var condition = ConditionFunctions.ParseQuery(query);

        Assert.NotNull(condition);
        Assert.Equal(query, ConditionFunctions.ToQuery(condition));

        // and means the same thing after the trip
        Assert.Equal(Matching(query), Matching(ConditionFunctions.ToQuery(condition)));
    }

    /// <summary>
    /// The wire shape carries which operand is which, so a condition serialized and sent still compares against
    /// the property rather than against its name
    /// </summary>
    [Fact]
    public void ItSurvivesBeingPackedAndUnpacked()
    {
        var condition = ConditionFunctions.ParseQuery("Pay IsIn (8000, [Ceiling])");

        Assert.NotNull(condition);

        var unpacked = condition.Pack().Unpack();

        Assert.Contains(Assert.IsAssignableFrom<IBoundCondition>(unpacked).StringifyOperands(), operand => operand.NamesProperty);
        Assert.Equal(["Alice", "David"], Query().ApplyCondition(unpacked).Build().ToList().Select(minion => minion.Name.Split(' ')[0]).Order());
    }

    // ---------- against a provider ----------

    /// <summary>
    /// The values in a mixed list keep the translation an ordinary list gets, rather than becoming an OR of one
    /// test each: PostgreSQL takes them as the single parameter it always did, so the statement still does not
    /// change with how many of them there are. The property is the part that cannot go in that list, since it is a
    /// column rather than something to send, so it is one comparison ORed on.
    /// </summary>
    [Fact]
    public void TheValuesInAMixedListAreStillTranslatedAsAList()
    {
        using var context = TestDatabase.Create(TestProvider.PostgreSql);

        string Query(string query)
        {
            return context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .ApplyCondition(query)
                .Build()
                .ToQueryString();
        }

        var two = Query("Name IsIn ('Bob Samuelson', [Alias])");
        var four = Query("Name IsIn ('Bob Samuelson', 'Alice Fox', 'David Edgars', [Alias])");

        Assert.Contains("= ANY (", TestDatabase.StatementOnly(two));
        Assert.Contains("\"Alias\"", TestDatabase.StatementOnly(two));

        Assert.Equal(TestDatabase.StatementOnly(two), TestDatabase.StatementOnly(four));
        Assert.Equal(1, TestDatabase.ParameterCount(four));
    }

    /// <summary>
    /// A range against a property is the provider's own comparison against the column, with any value end still a
    /// parameter
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void ARangeAgainstAPropertyReadsTheColumn(TestProvider provider)
    {
        using var context = TestDatabase.Create(provider);

        var sql = context.Minions
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition("BirthDate IsBetween (2000-01-01, [HireDate])")
            .Build()
            .ToQueryString();

        Assert.Contains("HireDate", TestDatabase.StatementOnly(sql));
        Assert.Equal(1, TestDatabase.ParameterCount(sql));
        Assert.DoesNotContain("2000", TestDatabase.StatementOnly(sql));
    }

    [Fact]
    public void TheDatabaseAnswersWithTheSameRows()
    {
        var context = TestDatabase.CreateSeeded(TestProvider.Sqlite);
        try
        {
            var names = context.Minions
                .WithWeequery()
                .BindProperties(Minion.Bindings)
                .BindConstant("Ceiling", 12000m)
                .ApplyCondition("Pay IsIn (8000, [Ceiling]) || Pay IsBetween (18000, [Ceiling])")
                .Build()
                .Select(minion => minion.Name)
                .ToList();

            Assert.Equal(["Alice Fox", "David Edgars"], names.Order());
        }
        finally
        {
            TestDatabase.Drop(context);
        }
    }
}
