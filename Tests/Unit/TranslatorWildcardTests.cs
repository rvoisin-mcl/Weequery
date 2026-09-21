using Weequery;
using Weequery.Elasticsearch;
using Weequery.OData;

namespace Tests.Unit;

/// <summary>
/// <c>*</c> and <c>Prefix.*</c> on the translating side.
/// <para>
/// The same two wildcards <see cref="Inquiry{T}.ApplyProjection(string?)"/> takes, so a projection written once
/// means the same thing whether the rows come from the entity or the query goes to a service. What differs is
/// what they expand against: a declared field set rather than a binding set, and a declared field is a readable
/// one, there being no <see cref="BindingUse"/> out here.
/// </para>
/// </summary>
public class TranslatorWildcardTests
{
    private static ODataFieldSet ODataFields()
    {
        return new ODataFieldSet
        {
            new("Name", "Name"),
            new("Lair.Name", "Lair/Name"),
            new("Lair.City", "Lair/Address/City"),
            new("Lairyard", "Lairyard"),
        };
    }

    private static ElasticFieldSet ElasticFields()
    {
        return new ElasticFieldSet
        {
            new("Name", "name"),
            new("Lair.Name", "lair.name"),
            new("Lair.City", "lair.city"),
            new("Lairyard", "lairyard"),
        };
    }

    // ---------- OData ----------

    [Fact]
    public void ODataReadsEveryDeclaredFieldForAStar()
    {
        var query = ODataQuery.Build(ODataFields(), projection: Projection.Parse("*"));

        Assert.Equal("Name,Lair/Name,Lair/Address/City,Lairyard", query["$select"]);
    }

    [Fact]
    public void ODataReadsEveryFieldUnderAPrefix()
    {
        var query = ODataQuery.Build(ODataFields(), projection: Projection.Parse("Lair.*"));

        Assert.Equal("Lair/Name,Lair/Address/City", query["$select"]);
    }

    /// <summary>The dot is part of the prefix, so Lairyard is not one of Lair's</summary>
    [Fact]
    public void ODataStopsThePrefixAtTheDot()
    {
        var query = ODataQuery.Build(ODataFields(), projection: Projection.Parse("Lair.*"));

        Assert.DoesNotContain("Lairyard", query["$select"]);
    }

    [Fact]
    public void ODataComposesNamesAndPrefixesWithoutRepeating()
    {
        var query = ODataQuery.Build(ODataFields(), projection: Projection.Parse("Lair.Name, Lair.*, Name"));

        Assert.Equal("Lair/Name,Lair/Address/City,Name", query["$select"]);
    }

    [Fact]
    public void ODataRefusesAPrefixThatMatchesNothing()
    {
        var error = Assert.Throws<WeequeryException>(() => ODataQuery.Build(ODataFields(), projection: Projection.Parse("Gizmo.*")));

        Assert.Equal(WeequeryError.UnboundField, error.Error);
        Assert.Contains("Gizmo.", error.Message);
    }

    // ---------- Elasticsearch ----------

    [Fact]
    public void ElasticReadsEveryDeclaredFieldForAStar()
    {
        var body = ElasticSearchBody.ToJson(ElasticFields(), projection: Projection.Parse("*"));

        Assert.Contains("\"includes\":[\"name\",\"lair.name\",\"lair.city\",\"lairyard\"]", body);
    }

    [Fact]
    public void ElasticReadsEveryFieldUnderAPrefix()
    {
        var body = ElasticSearchBody.ToJson(ElasticFields(), projection: Projection.Parse("Lair.*"));

        Assert.Contains("\"includes\":[\"lair.name\",\"lair.city\"]", body);
    }

    [Fact]
    public void ElasticStopsThePrefixAtTheDot()
    {
        var body = ElasticSearchBody.ToJson(ElasticFields(), projection: Projection.Parse("Lair.*"));

        Assert.DoesNotContain("lairyard", body);
    }

    [Fact]
    public void ElasticRefusesAPrefixThatMatchesNothing()
    {
        Assert.Throws<WeequeryException>(() => ElasticSearchBody.ToJson(ElasticFields(), projection: Projection.Parse("Gizmo.*")));
    }

    // ---------- and an unknown plain name still reads as it did ----------

    [Fact]
    public void AnUndeclaredNameIsStillRefusedByBoth()
    {
        Assert.Throws<WeequeryException>(() => ODataQuery.Build(ODataFields(), projection: Projection.Parse("Gizmo")));
        Assert.Throws<WeequeryException>(() => ElasticSearchBody.ToJson(ElasticFields(), projection: Projection.Parse("Gizmo")));
    }
}
