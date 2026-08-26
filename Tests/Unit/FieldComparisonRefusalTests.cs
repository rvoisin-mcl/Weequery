using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// What a comparison against another bound property does with a field it cannot compare, and with a name it could
/// not write back.
/// <para>
/// Both are about the two routes to a comparison agreeing. A condition naming a property is built somewhere other
/// than the per-type builders, so a type they refuse has to be refused there too, and a failure has to arrive as
/// the same kind of exception rather than as whatever the expression api threw.
/// </para>
/// </summary>
public class FieldComparisonRefusalTests
{
    private static IQueryable<LairAssignment> Assignments()
    {
        return new List<LairAssignment>
        {
            new() { Lair = new Lair { Name = "Volcano", Capacity = 10 } },
            new() { Lair = null },
        }.AsQueryable();
    }

    private static int Count(string query)
    {
        return Assignments()
            .WithWeequery()
            .BindProperty("Lair")
            .BindProperty("Lair.Name", "LairName")
            .BindProperty("Lair.Capacity", "LairCapacity")
            .ApplyCondition(query)
            .Build()
            .Count();
    }

    // ---------- a type that cannot be compared ----------

    /// <summary>
    /// A reference type Weequery does not support is bound as an object, which only the null tests apply to. It
    /// used to reach the expression api through this route and throw InvalidOperationException out of the build,
    /// where the same comparison against a value was refused by name.
    /// </summary>
    [Theory]
    [InlineData("Lair > [Lair]")]
    [InlineData("Lair == [Lair]")]
    [InlineData("Lair != [Lair]")]
    [InlineData("Lair IsIn ([Lair])")]
    [InlineData("Lair IsBetween ([Lair], [Lair])")]
    [InlineData("Lair StartsWith [Lair]")]
    public void ComparingAPropertyOfATypeThatCannotBeComparedIsRefused(string query)
    {
        Assert.Throws<WeequeryException>(() => Count(query));
    }

    /// <summary>
    /// Which is the answer the same comparison against a value gives, so the two routes agree
    /// </summary>
    [Fact]
    public void TheSameComparisonAgainstAValueIsRefusedToo()
    {
        Assert.Throws<WeequeryException>(() => Count("Lair == 'Volcano'"));
        Assert.Throws<WeequeryException>(() => Count("Lair > 'Volcano'"));
    }

    /// <summary>
    /// The null tests are what an object binding is for, and they still work either way round
    /// </summary>
    [Fact]
    public void TheNullTestsOnSuchAPropertyStillWork()
    {
        Assert.Equal(1, Count("Lair IsNull"));
        Assert.Equal(1, Count("Lair IsNotNull"));
    }

    /// <summary>
    /// And a property of a type that can be compared, reached through the same reference, is unaffected
    /// </summary>
    [Fact]
    public void APropertyReachedThroughItIsUnaffected()
    {
        Assert.Equal(1, Count("LairName == [LairName]"));
        Assert.Equal(1, Count("LairCapacity IsIn (10, [LairCapacity])"));
    }

    /// <summary>
    /// Whatever goes wrong while an expression is built, a caller sees the library's own exception naming the
    /// operator and the field, not the expression api's account of the types the tree is made of
    /// </summary>
    [Fact]
    public void AFailureToBuildArrivesAsAWeequeryException()
    {
        var ex = Record.Exception(() => Count("Lair > [Lair]"));

        Assert.IsType<WeequeryException>(ex);
    }

    // ---------- a name that could not be written back ----------

    /// <summary>
    /// A property is named between brackets when the condition is written out, so a name the brackets could not
    /// hold would produce a query string that will not parse back. No binding can have such a key, so this only
    /// arrives from a condition built in code, and it is refused there rather than at the far end of a round trip.
    /// </summary>
    [Theory]
    [InlineData("has space")]
    [InlineData("with]bracket")]
    [InlineData("with'quote")]
    [InlineData("with,comma")]
    public void ANameThatCouldNotBeWrittenBackIsRefused(string name)
    {
        Assert.Throws<WeequeryException>(() => new OneValueCondition<string>(Operator.Equals, nameof(Minion.Name), ConditionValue.Binding(name)));
    }

    /// <summary>
    /// A dotted path is a binding key like any other, since a property bound without one is keyed by its path
    /// </summary>
    [Fact]
    public void ADottedPathIsStillAName()
    {
        var condition = new OneValueCondition<string>(Operator.Equals, "LairName", ConditionValue.Binding("Lair.Name"));

        Assert.Equal("([LairName] == [Lair.Name])", ConditionFunctions.ToQuery(condition));
    }

    /// <summary>
    /// The property this exists for: what is written can be read
    /// </summary>
    [Theory]
    [InlineData(Operator.Equals)]
    [InlineData(Operator.GreaterThan)]
    [InlineData(Operator.IsIn)]
    public void WhatIsWrittenReadsBack(Operator op)
    {
        var condition = ConditionFunctions.BuildComparison(op, nameof(Minion.Name), [ConditionValue.Binding(nameof(Minion.Alias))]);

        var written = ConditionFunctions.ToQuery(condition);
        var read = ConditionFunctions.ParseQuery(written);

        Assert.NotNull(read);
        Assert.Equal(written, ConditionFunctions.ToQuery(read));
    }

    /// <summary>
    /// A value is quoted rather than named, so it has nothing to live up to
    /// </summary>
    [Fact]
    public void AValueIsUnaffectedByTheRule()
    {
        var condition = new MultipleValueCondition<string>(Operator.IsIn, nameof(Minion.Name), [ConditionValue.Raw("has space"), ConditionValue.Binding(nameof(Minion.Alias))]);

        Assert.Equal("([Name] IsIn ('has space', [Alias]))", ConditionFunctions.ToQuery(condition));
    }

    /// <summary>
    /// The route it would arrive by from a client, which builds the same condition and so applies the same rule
    /// </summary>
    [Fact]
    public void APackedConditionCarryingSuchANameIsRefusedWhenUnpacked()
    {
        var packed = new PackedCondition(Operator.Equals, nameof(Minion.Name), [ConditionValue.Binding("has space")], []);

        Assert.Throws<WeequeryException>(() => packed.Unpack());
    }
}
