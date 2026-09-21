using Weequery;
using Weequery.OData;

namespace Tests.Unit;

/// <summary>
/// Turning a Weequery condition into an OData $filter.
/// <para>
/// The interesting part is not the operator names, it is that OData disagrees with Weequery about a null on the
/// right of a negative comparison and says so in the specification. Most of what these check is that a condition
/// means the same thing on both sides.
/// </para>
/// </summary>
public class ODataFilterTests
{
    private static ODataFieldSet Fields()
    {
        return new ODataFieldSet
        {
            new("Name", "Name"),
            new("Alias", "Alias"),
            new("Pay", "Salary", ODataFieldKind.Number),
            new("IsActive", "Active", ODataFieldKind.Boolean),
            new("Hired", "HiredOn", ODataFieldKind.Date),
            new("Id", "MinionID", ODataFieldKind.Guid),
            new("Rank", "Classification", ODataFieldKind.Enum, EnumType: "Lair.Model.Rank"),
            new("Shift", "ShiftLength", ODataFieldKind.Duration),
            new("City", "Lair/Address/City"),
            new("Assignments", "Assignments", ODataFieldKind.Collection),
            new("LairName", "Lair/Name", Collection: "Assignments"),
            new("LairID", "LairID", ODataFieldKind.Number, Collection: "Assignments"),
        };
    }

    private static string Filter(string query, ODataVersion version = ODataVersion.V401)
    {
        return ODataFilter.Write(ConditionFunctions.ParseQuery(query, QueryStyle.Native), Fields(), version);
    }

    // ---------- the operators ----------

    [Fact]
    public void TheComparisonsAreTheirOperators()
    {
        Assert.Equal("Alias eq 'Ghost'", Filter("Alias = 'Ghost'"));
        Assert.Equal("Salary lt 100", Filter("Pay < 100"));
        Assert.Equal("Salary le 100", Filter("Pay <= 100"));
        Assert.Equal("Salary gt 100", Filter("Pay > 100"));
        Assert.Equal("Salary ge 100", Filter("Pay >= 100"));
    }

    [Fact]
    public void TheNullTestsCompareAgainstNull()
    {
        Assert.Equal("Alias eq null", Filter("Alias IsNull"));
        Assert.Equal("Alias ne null", Filter("Alias IsNotNull"));
    }

    [Fact]
    public void TheStringFunctionsAreFunctions()
    {
        Assert.Equal("startswith(Alias,'Gho')", Filter("Alias StartsWith 'Gho'"));
        Assert.Equal("endswith(Alias,'ost')", Filter("Alias EndsWith 'ost'"));
        Assert.Equal("contains(Alias,'hos')", Filter("Alias Contains 'hos'"));
        Assert.Equal("matchesPattern(Alias,'Gh.st')", Filter("Alias IsMatch 'Gh.st'"));
    }

    [Fact]
    public void ARangeIsBothEndsInclusive()
    {
        Assert.Equal("(Salary ge 1 and Salary le 9)", Filter("Pay IsBetween (1, 9)"));
    }

    [Fact]
    public void ANestedPropertyPathUsesSlashes()
    {
        Assert.Equal("Lair/Address/City eq 'Reykjavik'", Filter("City = 'Reykjavik'"));
    }

    /// <summary>OData writes almost every type differently, and gets it wrong loudly rather than quietly</summary>
    [Fact]
    public void EachKindIsWrittenItsOwnWay()
    {
        Assert.Equal("Name eq 'Alice'", Filter("Name = 'Alice'"));
        Assert.Equal("Salary eq 10000", Filter("Pay = 10000"));
        Assert.Equal("Active eq true", Filter("IsActive = true"));
        Assert.Equal("HiredOn eq 2024-01-15", Filter("Hired = '2024-01-15'"));
        Assert.Equal("MinionID eq 0f8fad5b-d9cb-469f-a165-70867728950e", Filter("Id = '0f8fad5b-d9cb-469f-a165-70867728950e'"));
        Assert.Equal("Classification eq Lair.Model.Rank'High'", Filter("Rank = 'High'"));
        Assert.Equal("ShiftLength eq duration'PT8H'", Filter("Shift = 'PT8H'"));
    }

    /// <summary>
    /// A quote inside a string literal is doubled, which is the whole of OData's escaping. Built rather than
    /// parsed, since Weequery's own language escapes a quote its own way and this is about what comes out.
    /// </summary>
    [Fact]
    public void AQuoteInAValueIsDoubled()
    {
        var condition = new OneValueCondition<string>(Operator.Equals, "Alias", "O'Hara");

        Assert.Equal("Alias eq 'O''Hara'", ODataFilter.Write(condition, Fields()));
    }

    // ---------- the null semantics, which is the point ----------

    /// <summary>
    /// OData says a null is "not equal to any other value", so a bare ne returns the records with no alias.
    /// Weequery's does not, so the guard goes back on.
    /// </summary>
    [Fact]
    public void ANegativeOperatorKeepsItsGuard()
    {
        Assert.Equal("(Alias ne null and Alias ne 'Ghost')", Filter("Alias <> 'Ghost'"));
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
            Assert.Contains("ne null and", Filter(query));
        }
    }

    /// <summary>And NOT is deliberately not guarded, for the same reason it is not in Weequery</summary>
    [Fact]
    public void ANegatedConditionDoesNotKeepTheGuard()
    {
        Assert.Equal("not Alias eq 'Ghost'", Filter("NOT (Alias = 'Ghost')"));
    }

    [Fact]
    public void APositiveOperatorNeedsNoGuard()
    {
        Assert.DoesNotContain("ne null", Filter("Alias = 'Ghost'"));
        Assert.DoesNotContain("ne null", Filter("Alias StartsWith 'G'"));
        Assert.DoesNotContain("ne null", Filter("Pay > 1"));
    }

    // ---------- versions ----------

    [Fact]
    public void AListIsTheInOperatorIn401()
    {
        Assert.Equal("Alias in ('Ghost','Snake')", Filter("Alias IsIn ('Ghost', 'Snake')"));
    }

    /// <summary>4.0 has no in operator, so it is expanded into what it means</summary>
    [Fact]
    public void AListIsExpandedIn40()
    {
        Assert.Equal("(Alias eq 'Ghost' or Alias eq 'Snake')", Filter("Alias IsIn ('Ghost', 'Snake')", ODataVersion.V4));
    }

    [Fact]
    public void AnEmptyListMatchesNothingEitherWay()
    {
        Assert.Equal("false", Filter("Alias IsIn ()"));
        Assert.Equal("false", Filter("Alias IsIn ()", ODataVersion.V4));
    }

    /// <summary>matchesPattern arrived with 4.01, so 4.0 is told rather than handed something it cannot read</summary>
    [Fact]
    public void APatternIsRefusedFor40()
    {
        var error = Assert.Throws<WeequeryException>(() => Filter("Alias IsMatch 'Gh.st'", ODataVersion.V4));

        Assert.Equal(WeequeryError.NotTranslatable, error.Error);
    }

    // ---------- containers ----------

    [Fact]
    public void TheConjunctionsAreAndAndOr()
    {
        Assert.Equal("(Active eq true and Salary gt 100)", Filter("IsActive = true AND Pay > 100"));
        Assert.Equal("(Active eq true or Salary gt 100)", Filter("IsActive = true OR Pay > 100"));
    }

    /// <summary>Parenthesised whatever the precedence would be, so a tree reads back as the tree it was</summary>
    [Fact]
    public void NestingSurvives()
    {
        Assert.Equal(
            "(Active eq true and (Salary gt 100 or Alias eq null))",
            Filter("IsActive = true AND (Pay > 100 OR Alias IsNull)"));
    }

    [Fact]
    public void TheIdentitiesAreWhatTheyMean()
    {
        Assert.Equal(string.Empty, ODataFilter.Write(null, Fields()));
        Assert.Equal("true", ODataFilter.Write(new ConjunctionCondition(Operator.And, []), Fields()));
        Assert.Equal("false", ODataFilter.Write(new ConjunctionCondition(Operator.Or, []), Fields()));
    }

    [Fact]
    public void APackedConditionWritesToo()
    {
        var packed = ConditionFunctions.ParseQuery("Alias = 'Ghost'", QueryStyle.Native)!.Pack();

        Assert.Equal("Alias eq 'Ghost'", ODataFilter.Write(packed, Fields()));
    }

    /// <summary>The one thing OData does that SQL and the Query DSL both struggle with</summary>
    [Fact]
    public void AFieldCanBeComparedAgainstAnotherField()
    {
        Assert.Equal("Name eq Alias", Filter("Name = [Alias]"));
    }

    // ---------- quantifiers ----------

    [Fact]
    public void TheQuantifiersAreLambdas()
    {
        Assert.Equal("Assignments/any(d1: d1/Lair/Name eq 'Volcano')", Filter("Assignments Any (LairName = 'Volcano')"));
        Assert.Equal("Assignments/all(d1: d1/Lair/Name eq 'Volcano')", Filter("Assignments All (LairName = 'Volcano')"));
        Assert.Equal("not Assignments/any(d1: d1/Lair/Name eq 'Volcano')", Filter("Assignments None (LairName = 'Volcano')"));
    }

    /// <summary>The condition inside is scoped to one element, which is what a lambda is</summary>
    [Fact]
    public void TheWholeInnerConditionIsInsideTheLambda()
    {
        Assert.Equal(
            "Assignments/any(d1: (d1/LairID eq 5 and d1/Lair/Name eq 'Volcano'))",
            Filter("Assignments Any (LairID = 5 AND LairName = 'Volcano')"));
    }

    [Fact]
    public void AQuantifierNeedsACollection()
    {
        var error = Assert.Throws<WeequeryException>(() => Filter("Name Any (LairID = 5)"));

        Assert.Contains(nameof(ODataFieldKind.Collection), error.Message);
    }

    [Fact]
    public void ACollectionCannotBeCompared()
    {
        var error = Assert.Throws<WeequeryException>(() => Filter("Assignments = 'x'"));

        Assert.Equal(WeequeryError.OperatorUnsupported, error.Error);
    }

    /// <summary>A lambda's variable does not reach outside it, and nothing outside reaches in</summary>
    [Fact]
    public void TheTwoScopesDoNotLeak()
    {
        Assert.Throws<WeequeryException>(() => Filter("Assignments Any (Alias = 'Ghost')"));
        Assert.Throws<WeequeryException>(() => Filter("LairID = 5"));
    }

    // ---------- what it refuses ----------

    [Fact]
    public void AnUndeclaredKeyIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Filter("Gizmo = 3"));

        Assert.Contains("Gizmo", error.Message);
        Assert.Equal(WeequeryError.UnboundField, error.Error);
    }

    [Fact]
    public void AnOperatorThatDoesNotFitTheKindIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Filter("Pay StartsWith '1'"));
        Assert.Throws<WeequeryException>(() => Filter("IsActive > true"));
        Assert.Throws<WeequeryException>(() => Filter("IsActive = 'maybe'"));
    }

    /// <summary>A bare literal holding a quote or a space would end the expression early</summary>
    [Fact]
    public void AValueThatWouldBreakABareLiteralIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Filter("Pay = '1, 2'"));

        Assert.Equal(WeequeryError.ValueInvalid, error.Error);
    }

    [Fact]
    public void AnIndexIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Filter("Alias[0] = 'Ghost'"));

        Assert.Contains("Alias[0]", error.Message);
    }

    // ---------- the whole set of options ----------

    [Fact]
    public void TheOptionsCarryEverything()
    {
        var parsed = ParsedQuery.Parse("IsActive = true OrderBy Pay DESC, Name", style: QueryStyle.Native);

        var query = ODataQuery.ToQueryString(
            Fields(), parsed.Condition, parsed.Sorts, Projection.Parse("Name, Alias"), pageSize: 20, page: 2);

        Assert.Equal(
            "$filter=Active eq true&$orderby=Salary desc,Name asc&$top=20&$skip=40&$select=Name,Alias",
            query);
    }

    [Fact]
    public void NothingAskedIsNothingWritten()
    {
        Assert.Equal(string.Empty, ODataQuery.ToQueryString(Fields()));
        Assert.Empty(ODataQuery.Build(Fields()));
    }

    /// <summary>Skipping nothing is what not saying so already means</summary>
    [Fact]
    public void TheFirstPageWritesNoSkip()
    {
        Assert.Equal("$top=20", ODataQuery.ToQueryString(Fields(), pageSize: 20, page: 0));
    }

    [Fact]
    public void TheOptionsHoldSortsAndSelectsToTheSameAllowList()
    {
        Assert.Throws<WeequeryException>(() => ODataQuery.Build(Fields(), sorts: [new Sort("Gizmo", SortDirection.Ascending)]));
        Assert.Throws<WeequeryException>(() => ODataQuery.Build(Fields(), projection: Projection.Parse("Gizmo")));
    }

    [Fact]
    public void ACollectionsFieldCannotBeSortedOnOrSelected()
    {
        Assert.Throws<WeequeryException>(() => ODataQuery.Build(Fields(), sorts: [new Sort("LairName", SortDirection.Ascending)]));
        Assert.Throws<WeequeryException>(() => ODataQuery.Build(Fields(), sorts: [new Sort("Assignments", SortDirection.Ascending)]));
        Assert.Throws<WeequeryException>(() => ODataQuery.Build(Fields(), projection: Projection.Parse("LairName")));
    }

    [Fact]
    public void TheWindowIsCheckedTheWayApplyPaginationIs()
    {
        Assert.Throws<WeequeryException>(() => ODataQuery.Build(Fields(), pageSize: 20, page: -1));
        Assert.Throws<WeequeryException>(() => ODataQuery.Build(Fields(), pageSize: int.MaxValue, page: 2));
    }

    /// <summary>The order is fixed, so the same query gives the same string and is worth comparing</summary>
    [Fact]
    public void TheOrderOfTheOptionsIsStable()
    {
        var once = ODataQuery.ToQueryString(Fields(), ConditionFunctions.ParseQuery("Pay > 1", QueryStyle.Native), pageSize: 5, page: 1);

        Assert.Equal("$filter=Salary gt 1&$top=5&$skip=5", once);
    }
}
