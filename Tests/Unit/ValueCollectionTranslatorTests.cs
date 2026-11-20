using Weequery;
using Weequery.Elasticsearch;
using Weequery.OData;

namespace Tests.Unit;

/// <summary>
/// A quantifier over a collection of values, written for OData and for Elasticsearch, where the element is named
/// Value because it has no field to declare
/// </summary>
public class ValueCollectionTranslatorTests
{
    // ---------- OData ----------

    private static ODataFieldSet ODataFields()
    {
        return new ODataFieldSet
        {
            new("Name", "Name"),
            ODataField.ForValues("Tags", "Tags", ODataFieldKind.String),
            ODataField.ForValues("Scores", "Scores", ODataFieldKind.Number),
            ODataField.ForValues("Ranks", "Ranks", ODataFieldKind.Enum, "Lair.Model.Rank"),
            new("Assignments", "Assignments", ODataFieldKind.Collection),
            new("LairID", "LairID", ODataFieldKind.Number, Collection: "Assignments"),
        };
    }

    private static string Filter(string query)
    {
        return ODataFilter.Write(ConditionFunctions.ParseQuery(query), ODataFields());
    }

    [Fact]
    public void ODataWritesTheElementAsTheLambdasVariable()
    {
        Assert.Equal("Tags/any(d1: d1 eq 'red')", Filter("Tags Any (Value = 'red')"));
        Assert.Equal("Scores/all(d1: d1 ge 50)", Filter("Scores All (Value >= 50)"));
        Assert.Equal("not Tags/any(d1: startswith(d1,'tmp'))", Filter("Tags None (Value StartsWith 'tmp')"));
    }

    /// <summary>A lambda ties every test inside it to one element, so OData answers the AND exactly</summary>
    [Fact]
    public void ODataTiesSeveralTestsToOneElement()
    {
        Assert.Equal("Scores/any(d1: (d1 gt 5 and d1 lt 10))", Filter("Scores Any (Value > 5 AND Value < 10)"));
    }

    [Fact]
    public void ODataWritesAValueAsTheElementsKind()
    {
        Assert.Equal("Scores/any(d1: d1 eq 7)", Filter("Scores Any (Value = 7)"));
        Assert.Equal("Ranks/any(d1: d1 eq Lair.Model.Rank'High')", Filter("Ranks Any (Value = 'High')"));
    }

    [Fact]
    public void ODataGuardsANegativeTestOnTheElement()
    {
        Assert.Equal("Tags/any(d1: (d1 ne null and d1 ne 'red'))", Filter("Tags Any (Value <> 'red')"));
    }

    [Fact]
    public void ODataHoldsTheElementToItsKind()
    {
        var thrown = Assert.Throws<WeequeryException>(() => Filter("Scores Any (Value StartsWith '1')"));

        Assert.Equal(WeequeryError.OperatorUnsupported, thrown.Error);
    }

    [Fact]
    public void ODataKnowsNothingButValueInsideACollectionOfValues()
    {
        var thrown = Assert.Throws<WeequeryException>(() => Filter("Tags Any (Name = 'red')"));

        Assert.Equal(WeequeryError.UnboundField, thrown.Error);
        Assert.Contains("'Value'", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>Value is the element of a collection of values and nothing anywhere else</summary>
    [Fact]
    public void ODataRefusesValueOutsideOne()
    {
        Assert.Throws<WeequeryException>(() => Filter("Value = 'red'"));
        Assert.Throws<WeequeryException>(() => Filter("Assignments Any (Value = 5)"));
    }

    [Fact]
    public void ODataStillRefusesComparingTheCollectionItself()
    {
        Assert.Throws<WeequeryException>(() => Filter("Tags = 'red'"));
    }

    [Fact]
    public void ODataRefusesACollectionOfCollections()
    {
        Assert.Throws<WeequeryException>(() => ODataField.ForValues("Grid", "Grid", ODataFieldKind.Collection));
    }

    // ---------- Elasticsearch ----------

    private static ElasticFieldSet ElasticFields()
    {
        return new ElasticFieldSet
        {
            new("Name", "name", ElasticFieldKind.Text),
            ElasticField.ForValues("Tags", "tags"),
            ElasticField.ForValues("Words", "words", ElasticFieldKind.Text),
            ElasticField.ForValues("Scores", "scores", ElasticFieldKind.Number),
            ElasticField.ForValues("Flags", "flags", ElasticFieldKind.Boolean),
            new("Assignments", "assignments", ElasticFieldKind.Keyword, Nested: "assignments"),
            new("LairName", "assignments.lair", ElasticFieldKind.Keyword, Nested: "assignments"),
        };
    }

    private static string Json(string query)
    {
        return ElasticQuery.ToJson(ConditionFunctions.ParseQuery(query), ElasticFields());
    }

    private static WeequeryException Refused(string query)
    {
        return Assert.Throws<WeequeryException>(() => Json(query));
    }

    /// <summary>A query against a multi-valued field asks whether any value matches, which is Any</summary>
    [Fact]
    public void ElasticAnyIsAQueryAgainstTheFieldItself()
    {
        Assert.Equal("""{"term":{"tags":"red"}}""", Json("Tags Any (Value = 'red')"));
        Assert.Equal("""{"range":{"scores":{"gt":5}}}""", Json("Scores Any (Value > 5)"));
        Assert.Equal("""{"terms":{"tags":["red","blue"]}}""", Json("Tags Any (Value IsIn ('red', 'blue'))"));
        Assert.Equal("""{"prefix":{"tags":"tmp"}}""", Json("Tags Any (Value StartsWith 'tmp')"));
        Assert.Equal("""{"exists":{"field":"tags"}}""", Json("Tags Any (Value IsNotNull)"));
    }

    /// <summary>Between is one range, which one value has to fall inside, so it ties both ends to one value</summary>
    [Fact]
    public void ElasticAnyBetweenIsOneRange()
    {
        Assert.Equal("""{"range":{"scores":{"gte":6,"lte":9}}}""", Json("Scores Any (Value IsBetween (6, 9))"));
    }

    [Fact]
    public void ElasticAnyOfAnOrIsAShould()
    {
        Assert.Equal("""{"bool":{"should":[{"term":{"tags":"red"}},{"prefix":{"tags":"b"}}],"minimum_should_match":1}}""",
            Json("Tags Any (Value = 'red' OR Value StartsWith 'b')"));
    }

    [Fact]
    public void ElasticNoneIsMustNotAny()
    {
        Assert.Equal("""{"bool":{"must_not":[{"term":{"tags":"red"}}]}}""", Json("Tags None (Value = 'red')"));
    }

    /// <summary>All is "no value fails", which needs the failing test written as a query</summary>
    [Fact]
    public void ElasticAllIsMustNotTheOpposite()
    {
        Assert.Equal("""{"bool":{"must_not":[{"range":{"scores":{"lt":50}}}]}}""", Json("Scores All (Value >= 50)"));
        Assert.Equal("""{"bool":{"must_not":[{"range":{"scores":{"gte":10}}}]}}""", Json("Scores All (Value < 10)"));
        Assert.Equal("""{"bool":{"must_not":[{"term":{"flags":false}}]}}""", Json("Flags All (Value = true)"));
    }

    /// <summary>Every value passing both is every value passing each, so an AND inside All comes apart</summary>
    [Fact]
    public void ElasticAllOfAnAndIsEachAll()
    {
        Assert.Equal("""{"bool":{"filter":[{"bool":{"must_not":[{"range":{"scores":{"lte":1}}}]}},{"bool":{"must_not":[{"range":{"scores":{"gte":5}}}]}}]}}""",
            Json("Scores All (Value > 1 AND Value < 5)"));
    }

    [Fact]
    public void ElasticAllBetweenFailsOnEitherSide()
    {
        Assert.Equal("""{"bool":{"must_not":[{"bool":{"should":[{"range":{"scores":{"lt":1}}},{"range":{"scores":{"gt":5}}}],"minimum_should_match":1}}]}}""",
            Json("Scores All (Value IsBetween (1, 5))"));
    }

    /// <summary>A keyword is ordered, so a value other than this one is below it or above it</summary>
    [Fact]
    public void ElasticAllEqualOnAKeywordIsNothingBelowOrAbove()
    {
        Assert.Equal("""{"bool":{"must_not":[{"bool":{"should":[{"range":{"tags":{"lt":"red"}}},{"range":{"tags":{"gt":"red"}}}],"minimum_should_match":1}}]}}""",
            Json("Tags All (Value = 'red')"));
    }

    /// <summary>Two clauses against an array would each be answered by whichever value suits them</summary>
    [Fact]
    public void ElasticRefusesAnAndWithTheBetweenThatWouldSayIt()
    {
        var thrown = Refused("Scores Any (Value > 5 AND Value < 10)");

        Assert.Equal(WeequeryError.NotTranslatable, thrown.Error);
        Assert.Contains("IsBetween", thrown.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Tags Any (Value <> 'red')")]
    [InlineData("Tags Any (NOT (Value = 'red'))")]
    [InlineData("Tags Any (Value DoesNotStartWith 'r')")]
    [InlineData("Tags Any (Value IsNull)")]
    public void ElasticRefusesANegativeTestInsideAny(string query)
    {
        Assert.Equal(WeequeryError.NotTranslatable, Refused(query).Error);
    }

    [Theory]
    [InlineData("Tags All (Value StartsWith 'r')")]
    [InlineData("Tags All (Value IsIn ('red', 'blue'))")]
    [InlineData("Words All (Value = 'red')")]
    [InlineData("Scores All (Value < 1 OR Value > 5)")]
    public void ElasticRefusesAnAllWhoseFailureIsNotAQuery(string query)
    {
        var thrown = Refused(query);

        Assert.Equal(WeequeryError.NotTranslatable, thrown.Error);
        Assert.Contains("None", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ElasticKnowsNothingButValueInsideAnArrayOfValues()
    {
        var thrown = Refused("Tags Any (Name = 'red')");

        Assert.Equal(WeequeryError.UnboundField, thrown.Error);
    }

    /// <summary>A direct comparison against an array would quietly be an Any, so it is refused instead</summary>
    [Fact]
    public void ElasticRefusesComparingTheArrayDirectly()
    {
        var thrown = Refused("Tags = 'red'");

        Assert.Equal(WeequeryError.OperatorUnsupported, thrown.Error);
        Assert.Contains("Any", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ElasticHoldsTheElementToItsKind()
    {
        Assert.Throws<WeequeryException>(() => Json("Scores Any (Value StartsWith '1')"));
        Assert.Throws<WeequeryException>(() => Json("Scores Any (Value = 'seven')"));
    }

    /// <summary>A collection of objects is unchanged: nested, and the AND inside is tied to one element by it</summary>
    [Fact]
    public void ElasticLeavesNestedCollectionsAsTheyWere()
    {
        Assert.Equal("""{"nested":{"path":"assignments","query":{"term":{"assignments.lair":"Volcano"}}}}""", Json("Assignments Any (LairName = 'Volcano')"));
    }
}
