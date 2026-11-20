using Weequery;

namespace Tests.Unit;

/// <summary>
/// "Assignments[]" is how ListBindings and IgnorePaths spell the inside of a collection, so it is what gets written
/// as a quantifier's field. It is refused by name, with the key to write instead, rather than as an empty index.
/// </summary>
public class QuantifierElementMarkerTests
{
    private static OneValueCondition<int> Inner()
    {
        return new OneValueCondition<int>(Operator.GreaterThan, "Take", 100);
    }

    [Fact]
    public void TheMarkerIsRefusedWithTheKeyToWrite()
    {
        var thrown = Assert.Throws<WeequeryException>(() => new QuantifiedCondition(Operator.Any, "Heists[]", Inner()));

        Assert.Equal(WeequeryError.KeyInvalid, thrown.Error);
        Assert.Contains("'Heists Any (...)'", thrown.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("empty index", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Operator.Any)]
    [InlineData(Operator.All)]
    [InlineData(Operator.None)]
    public void EveryQuantifierRefusesIt(Operator quantifier)
    {
        var thrown = Assert.Throws<WeequeryException>(() => new QuantifiedCondition(quantifier, "Heists[]", Inner()));

        Assert.Contains($"'Heists {quantifier} (...)'", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANestedCollectionIsNamedByItsWholeKey()
    {
        var thrown = Assert.Throws<WeequeryException>(() => new QuantifiedCondition(Operator.Any, "Bay.Cartons[]", Inner()));

        Assert.Contains("'Bay.Cartons Any (...)'", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheKeyItselfIsFine()
    {
        var quantified = new QuantifiedCondition(Operator.Any, "Heists", Inner());

        Assert.Equal("Heists", quantified.Field);
    }

    /// <summary>
    /// A packed condition is unpacked through the constructor, so one off the wire says the same
    /// </summary>
    [Fact]
    public void APackedConditionFromTheWireIsRefusedTheSameWay()
    {
        var packed = new PackedCondition(Operator.Any, "Heists[]", [], [new PackedCondition(Inner())]);

        var thrown = Assert.Throws<WeequeryException>(() => packed.Unpack());

        Assert.Equal(WeequeryError.KeyInvalid, thrown.Error);
        Assert.Contains("'Heists Any (...)'", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AQueryStringIsRefusedEitherWay()
    {
        var thrown = Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery("Heists[] Any (Take > 100)"));

        Assert.Contains("'Heists Any (...)'", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AQueryStringIsRefusedByNameWhateverTheQuantifierIsCalled()
    {
        var thrown = Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery("Heists[] none (Take > 100)"));

        Assert.Equal(WeequeryError.KeyInvalid, thrown.Error);
        Assert.Contains("'Heists None (...)'", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Only a quantifier gets the hint: an empty index anywhere else is still a missing index
    /// </summary>
    [Fact]
    public void AnEmptyIndexBeforeAComparisonIsStillAMissingIndex()
    {
        var thrown = Assert.Throws<WeequeryException>(() => ConditionFunctions.ParseQuery("Tallies[] > 5"));

        Assert.Equal(WeequeryError.QuerySyntax, thrown.Error);
        Assert.Contains("Expected an index for field 'Tallies'", thrown.Message, StringComparison.Ordinal);
    }
}
