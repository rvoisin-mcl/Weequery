using Weequery;
using Weequery.Elasticsearch;

namespace Tests.Unit;

/// <summary>
/// Turning a Weequery condition into Elasticsearch Query DSL.
/// <para>
/// The interesting part is not the shapes, it is the semantics: Weequery's operators carry a null guard and the
/// Query DSL's do not, so most of what these check is that a condition means the same thing on both sides.
/// </para>
/// </summary>
public class ElasticQueryTests
{
    private static ElasticFieldSet Fields()
    {
        return new ElasticFieldSet
        {
            new("Name", "name", ElasticFieldKind.Text),
            new("Alias", "alias.keyword"),
            new("Pay", "salary", ElasticFieldKind.Number),
            new("IsActive", "active", ElasticFieldKind.Boolean),
            new("Hired", "hired_on", ElasticFieldKind.Date),
            new("Assignments", "assignments", ElasticFieldKind.Keyword, Nested: "assignments"),
            new("LairName", "assignments.lair", ElasticFieldKind.Keyword, Nested: "assignments"),
        };
    }

    private static string Json(string query)
    {
        return ElasticQuery.ToJson(ConditionFunctions.ParseQuery(query, QueryStyle.Native), Fields());
    }

    // ---------- the shapes ----------

    [Fact]
    public void EqualityOnAKeywordIsATerm()
    {
        Assert.Equal("""{"term":{"alias.keyword":"Ghost"}}""", Json("Alias = 'Ghost'"));
    }

    /// <summary>
    /// term against an analysed field looks for the whole string among its tokens and finds nothing, which is
    /// the classic mistake this avoids
    /// </summary>
    [Fact]
    public void EqualityOnTextIsAMatchPhrase()
    {
        Assert.Equal("""{"match_phrase":{"name":"Alice Fox"}}""", Json("Name = 'Alice Fox'"));
    }

    [Fact]
    public void AValueIsWrittenAsItsDeclaredKind()
    {
        // A number is a JSON number and a boolean a JSON boolean, not the text they arrived as
        Assert.Equal("""{"term":{"salary":10000}}""", Json("Pay = 10000"));
        Assert.Equal("""{"term":{"salary":100.5}}""", Json("Pay = 100.5"));
        Assert.Equal("""{"term":{"active":true}}""", Json("IsActive = true"));

        // And a date stays text, for Elasticsearch to read against the mapping's own format
        Assert.Equal("""{"term":{"hired_on":"2024-01-15"}}""", Json("Hired = '2024-01-15'"));
    }

    [Fact]
    public void TheRangesAreARange()
    {
        Assert.Equal("""{"range":{"salary":{"gt":100}}}""", Json("Pay > 100"));
        Assert.Equal("""{"range":{"salary":{"lte":100}}}""", Json("Pay <= 100"));
        Assert.Equal("""{"range":{"salary":{"gte":1,"lte":9}}}""", Json("Pay IsBetween (1, 9)"));
    }

    [Fact]
    public void TheNullTestsAreExists()
    {
        Assert.Equal("""{"exists":{"field":"alias.keyword"}}""", Json("Alias IsNotNull"));
        Assert.Equal("""{"bool":{"must_not":[{"exists":{"field":"alias.keyword"}}]}}""", Json("Alias IsNull"));
    }

    [Fact]
    public void TheListOperatorsAreTerms()
    {
        Assert.Equal("""{"terms":{"alias.keyword":["Ghost","Snake"]}}""", Json("Alias IsIn ('Ghost', 'Snake')"));

        // An empty list matches nothing, in the Query DSL as in Weequery
        Assert.Equal("""{"terms":{"alias.keyword":[]}}""", Json("Alias IsIn ()"));
    }

    [Fact]
    public void TheSubstringOperatorsArePrefixAndWildcard()
    {
        Assert.Equal("""{"prefix":{"alias.keyword":"Gho"}}""", Json("Alias StartsWith 'Gho'"));
        Assert.Equal("""{"wildcard":{"alias.keyword":"*ost"}}""", Json("Alias EndsWith 'ost'"));
        Assert.Equal("""{"wildcard":{"alias.keyword":"*hos*"}}""", Json("Alias Contains 'hos'"));
        Assert.Equal("""{"regexp":{"alias.keyword":"Gh.st"}}""", Json("Alias IsMatch 'Gh.st'"));
    }

    /// <summary>A caller writing Contains '*' means the character, not everything</summary>
    [Fact]
    public void AWildcardInAValueIsEscaped()
    {
        Assert.Equal("""{"wildcard":{"alias.keyword":"*\\*a\\?b*"}}""", Json(@"Alias Contains '*a?b'"));
    }

    // ---------- the null semantics, which is the point ----------

    /// <summary>
    /// A bare must_not matches documents with no such field, and Weequery's negative operators do not catch a
    /// null. So the exists goes beside it, and this is the difference from a naive translation.
    /// </summary>
    [Fact]
    public void ANegativeOperatorKeepsItsGuard()
    {
        Assert.Equal(
            """{"bool":{"filter":[{"exists":{"field":"alias.keyword"}}],"must_not":[{"term":{"alias.keyword":"Ghost"}}]}}""",
            Json("Alias <> 'Ghost'"));
    }

    [Fact]
    public void EveryNegativeOperatorKeepsIt()
    {
        foreach (var query in new[]
        {
            "Alias <> 'Ghost'",
            "Alias DoesNotStartWith 'G'",
            "Alias DoesNotEndWith 'G'",
            "Alias DoesNotContain 'G'",
            "Alias DoesNotMatch 'G'",
            "Alias IsNotIn ('Ghost')",
            "Pay IsNotBetween (1, 9)",
        })
        {
            Assert.Contains("\"exists\"", Json(query));
            Assert.Contains("\"must_not\"", Json(query));
        }
    }

    /// <summary>
    /// And NOT is deliberately not guarded, for the same reason it is not in Weequery: negating a condition
    /// negates its guard, so the documents that have no alias come back.
    /// </summary>
    [Fact]
    public void ANegatedConditionDoesNotKeepTheGuard()
    {
        var json = Json("NOT (Alias = 'Ghost')");

        Assert.Equal("""{"bool":{"must_not":[{"term":{"alias.keyword":"Ghost"}}]}}""", json);
        Assert.DoesNotContain("exists", json);
    }

    /// <summary>A positive operator needs no guard: a document missing the field matches no term and no range</summary>
    [Fact]
    public void APositiveOperatorNeedsNoGuard()
    {
        Assert.DoesNotContain("exists", Json("Alias = 'Ghost'"));
        Assert.DoesNotContain("exists", Json("Pay > 1"));
        Assert.DoesNotContain("exists", Json("Alias StartsWith 'G'"));
    }

    // ---------- containers ----------

    [Fact]
    public void AndIsAFilter()
    {
        Assert.Equal(
            """{"bool":{"filter":[{"term":{"active":true}},{"range":{"salary":{"gt":100}}}]}}""",
            Json("IsActive = true AND Pay > 100"));
    }

    /// <summary>Without minimum_should_match a should beside no other clause is optional, which matches everything</summary>
    [Fact]
    public void OrIsAShouldThatHasToMatch()
    {
        var json = Json("IsActive = true OR Pay > 100");

        Assert.Contains("\"should\"", json);
        Assert.Contains("\"minimum_should_match\":1", json);
    }

    [Fact]
    public void TheIdentitiesAreWhatTheyMean()
    {
        // No condition at all, and an AND over nothing, both match everything
        Assert.Equal("""{"match_all":{}}""", ElasticQuery.ToJson(null, Fields()));
        Assert.Equal("""{"match_all":{}}""", ElasticQuery.ToJson(new ConjunctionCondition(Operator.And, []), Fields()));

        // And an OR over nothing matches nothing
        Assert.Equal("""{"bool":{"must_not":[{"match_all":{}}]}}""", ElasticQuery.ToJson(new ConjunctionCondition(Operator.Or, []), Fields()));
    }

    [Fact]
    public void NestingSurvives()
    {
        var json = Json("IsActive = true AND (Pay > 100 OR NOT (Alias IsNull))");

        Assert.Contains("\"filter\"", json);
        Assert.Contains("\"should\"", json);
        Assert.Contains("\"must_not\"", json);
    }

    [Fact]
    public void APackedConditionTranslatesToo()
    {
        var packed = ConditionFunctions.ParseQuery("Alias = 'Ghost'", QueryStyle.Native)!.Pack();

        Assert.Equal("""{"term":{"alias.keyword":"Ghost"}}""", ElasticQuery.ToJson(packed, Fields()));
    }

    // ---------- quantifiers ----------

    [Fact]
    public void AnyIsANestedQuery()
    {
        Assert.Equal(
            """{"nested":{"path":"assignments","query":{"term":{"assignments.lair":"Volcano"}}}}""",
            Json("Assignments Any (LairName = 'Volcano')"));
    }

    [Fact]
    public void NoneIsANegatedNestedQuery()
    {
        Assert.Equal(
            """{"bool":{"must_not":[{"nested":{"path":"assignments","query":{"term":{"assignments.lair":"Volcano"}}}}]}}""",
            Json("Assignments None (LairName = 'Volcano')"));
    }

    /// <summary>"No element fails it", which is the only way to say "every element passes" over nested documents</summary>
    [Fact]
    public void AllIsNoElementFailing()
    {
        Assert.Equal(
            """{"bool":{"must_not":[{"nested":{"path":"assignments","query":{"bool":{"must_not":[{"term":{"assignments.lair":"Volcano"}}]}}}}]}}""",
            Json("Assignments All (LairName = 'Volcano')"));
    }

    [Fact]
    public void AQuantifierNeedsANestedPath()
    {
        var error = Assert.Throws<WeequeryException>(() => Json("Name Any (LairName = 'Volcano')"));

        Assert.Contains("nested", error.Message);
    }

    /// <summary>A nested query addresses one path, so a field from outside it cannot be asked inside</summary>
    [Fact]
    public void AFieldOutsideThePathCannotBeAskedInside()
    {
        Assert.Throws<WeequeryException>(() => Json("Assignments Any (Alias = 'Ghost')"));
    }

    // ---------- what it refuses ----------

    [Fact]
    public void AnUndeclaredKeyIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Json("Gizmo = 3"));

        Assert.Contains("Gizmo", error.Message);
        Assert.Equal(WeequeryError.UnboundField, error.Error);
    }

    /// <summary>The same rules Weequery holds a property to, held to a declared kind instead</summary>
    [Fact]
    public void AnOperatorThatDoesNotFitTheKindIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Json("Pay StartsWith '1'"));
        Assert.Throws<WeequeryException>(() => Json("IsActive > true"));
        Assert.Throws<WeequeryException>(() => Json("Pay = 'not a number'"));
        Assert.Throws<WeequeryException>(() => Json("IsActive = 'maybe'"));
    }

    [Fact]
    public void ComparingTwoFieldsIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Json("Pay > [Pay]"));

        Assert.Equal(WeequeryError.NotTranslatable, error.Error);
    }

    /// <summary>An array is flattened into the field, so there is no element zero to address</summary>
    [Fact]
    public void AnIndexIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Json("Alias[0] = 'Ghost'"));

        Assert.Contains("Alias[0]", error.Message);
    }

    [Fact]
    public void ADuplicateKeyIsRefusedWhereItIsDeclared()
    {
        Assert.Throws<WeequeryException>(() => new ElasticFieldSet { new("Name", "a"), new("name", "b") });
    }

    // ---------- the whole body ----------

    [Fact]
    public void ABodyCarriesTheQuerySortWindowAndSource()
    {
        var parsed = ParsedQuery.Parse("IsActive = true OrderBy Pay DESC", style: QueryStyle.Native);

        var json = ElasticSearchBody.ToJson(
            Fields(), parsed.Condition, parsed.Sorts, Projection.Parse("Name, Alias"), pageSize: 20, page: 2);

        Assert.Equal(
            """{"query":{"term":{"active":true}},"sort":[{"salary":{"order":"desc"}}],"from":40,"size":20,"_source":{"includes":["name","alias.keyword"]}}""",
            json);
    }

    [Fact]
    public void ABodyLeavesOffWhatWasNotAsked()
    {
        Assert.Equal("""{"query":{"match_all":{}}}""", ElasticSearchBody.ToJson(Fields()));
    }

    [Fact]
    public void ABodyHoldsSortsAndProjectionsToTheSameAllowList()
    {
        Assert.Throws<WeequeryException>(() => ElasticSearchBody.Build(Fields(), sorts: [new Sort("Gizmo", SortDirection.Ascending)]));
        Assert.Throws<WeequeryException>(() => ElasticSearchBody.Build(Fields(), projection: Projection.Parse("Gizmo")));
    }

    /// <summary>A nested sort has to say which of the many values to order by, and a Sort does not carry that</summary>
    [Fact]
    public void ANestedFieldCannotBeSortedOn()
    {
        var error = Assert.Throws<WeequeryException>(() => ElasticSearchBody.Build(Fields(), sorts: [new Sort("LairName", SortDirection.Ascending)]));

        Assert.Contains("nested", error.Message);
    }

    [Fact]
    public void TheWindowIsCheckedTheWayApplyPaginationIs()
    {
        Assert.Throws<WeequeryException>(() => ElasticSearchBody.Build(Fields(), pageSize: 20, page: -1));
        Assert.Throws<WeequeryException>(() => ElasticSearchBody.Build(Fields(), pageSize: int.MaxValue, page: 2));

        // No window asked for is no window written, rather than a size of nothing
        Assert.DoesNotContain("size", ElasticSearchBody.ToJson(Fields(), pageSize: 0));
    }
}
