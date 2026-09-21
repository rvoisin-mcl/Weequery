using Weequery;

namespace Tests.Unit;

public class Heist
{
    public string Target { get; set; } = "";
    public int Take { get; set; }
    public bool Botched { get; set; }
    public Fence? Fence { get; set; }
}

public class Fence
{
    public string Name { get; set; } = "";
}

public class Crew
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public List<Heist>? Heists { get; set; }
}

/// <summary>
/// Quantified predicates: asking whether Any, All or None of a collection's elements satisfy a condition.
/// <para>
/// The other half of <see cref="CollectionIndexTests"/> and <see cref="ElementBindingTests"/>, which both pick an
/// element and then compare it. This asks about the elements as a set, so the answer does not depend on which one
/// is where, and the condition inside is scoped to one element at a time.
/// </para>
/// </summary>
public class QuantifierTests
{
    private static IQueryable<Crew> Crews()
    {
        return new List<Crew>
        {
            // A big clean job and a small botched one, which is what separates "any is big AND any is botched"
            // from "any is both"
            new()
            {
                Id = 1,
                Name = "Alpha",
                Heists =
                [
                    new() { Target = "Bank", Take = 500, Botched = false, Fence = new() { Name = "Vic" } },
                    new() { Target = "Museum", Take = 50, Botched = true },
                ],
            },
            new()
            {
                Id = 2,
                Name = "Beta",
                Heists = [new() { Target = "Bank", Take = 900, Botched = true, Fence = new() { Name = "Wanda" } }],
            },
            new() { Id = 3, Name = "Gamma", Heists = [] },      // empty
            new() { Id = 4, Name = "Delta" },                   // missing altogether
        }.AsQueryable();
    }

    private static Inquiry<Crew> Bound()
    {
        return Crews()
            .WithWeequery()
            .BindProperty(crew => crew.Name)
            .BindCollection(crew => crew.Heists, "Heists", inner => inner
                .BindProperty(heist => heist.Target)
                .BindProperty(heist => heist.Take)
                .BindProperty(heist => heist.Botched)
                .BindProperty(heist => heist.Fence!.Name, "FenceName"));
    }

    private static int[] Ids(string query)
    {
        return [.. Bound().ApplyCondition(query).Build().ToList().Select(crew => crew.Id)];
    }

    // ---------- what each quantifier means ----------

    [Fact]
    public void AnyIsTrueWhereOneElementMatches()
    {
        Assert.Equal([1, 2], Ids("Heists Any (Target = 'Bank')"));
        Assert.Equal([2], Ids("Heists Any (Take > 500)"));
    }

    [Fact]
    public void AllIsTrueWhereEveryElementMatches()
    {
        // Only Beta's single heist clears 100, and the two with nothing in them clear it vacuously
        Assert.Equal([2, 3, 4], Ids("Heists All (Take > 100)"));
    }

    [Fact]
    public void NoneIsTheNegationOfAny()
    {
        Assert.Equal([3, 4], Ids("Heists None (Target = 'Bank')"));
        Assert.Equal([1, 3, 4], Ids("Heists None (Take > 500)"));
    }

    /// <summary>
    /// Empty and missing are the same answer, which is the whole point of the guard: no elements means no element
    /// matches, so Any is false and the two that are true of nothing are true.
    /// </summary>
    [Fact]
    public void EmptyAndMissingAnswerAlike()
    {
        // Neither 3 nor 4 appears for Any, and both appear for All and None, whatever is asked
        Assert.Equal([1, 2], Ids("Heists Any (Take > 0)"));
        Assert.Equal([3, 4], Ids("Heists None (Take > 0)"));
        Assert.Equal([3, 4], Ids("Heists All (Take > 100000)"));
    }

    /// <summary>
    /// A quantifier never comes back unknown, which every other bound condition can. So a query and its negation
    /// partition the set rather than leaving rows out of both.
    /// </summary>
    [Fact]
    public void AQuantifierAndItsNegationCoverEverything()
    {
        var matched = Ids("Heists Any (Botched = true)");
        var rest = Ids("NOT (Heists Any (Botched = true))");

        Assert.Equal([1, 2], matched);
        Assert.Equal([3, 4], rest);
        Assert.Equal([1, 2, 3, 4], matched.Concat(rest).Order());
    }

    // ---------- the inner condition is scoped to one element ----------

    /// <summary>
    /// The reason a quantifier holds a condition rather than a test. "One heist that was both big and botched" is
    /// Beta only; "some heist was big, and some heist was botched" is Alpha as well, and they are asked differently.
    /// </summary>
    [Fact]
    public void OneElementSatisfiesTheWholeInnerCondition()
    {
        Assert.Equal([2], Ids("Heists Any (Take > 100 AND Botched = true)"));
        Assert.Equal([1, 2], Ids("Heists Any (Take > 100) AND Heists Any (Botched = true)"));
    }

    [Fact]
    public void TheInnerConditionCanBeAnyShape()
    {
        Assert.Equal([1, 2], Ids("Heists Any (Target = 'Bank' OR Target = 'Museum')"));
        Assert.Equal([1], Ids("Heists Any (NOT (Botched = true) AND Take < 600)"));
        Assert.Equal([1, 2], Ids("Heists Any (Take IsBetween (400, 1000))"));
        Assert.Equal([1], Ids("Heists Any (Target IsIn ('Museum', 'Vault'))"));
    }

    /// <summary>An element's own navigations are reachable, when the inner set bound them</summary>
    [Fact]
    public void TheInnerSetCanBindAPath()
    {
        Assert.Equal([1], Ids("Heists Any (FenceName = 'Vic')"));

        // Alpha's second heist has no fence, so the null rules inside are the ordinary ones
        Assert.Equal([1], Ids("Heists Any (FenceName IsNull)"));
    }

    [Fact]
    public void AQuantifierCombinesWithTheRest()
    {
        Assert.Equal([1], Ids("Name = 'Alpha' AND Heists Any (Botched = true)"));
        Assert.Equal([2, 3], Ids("Name = 'Gamma' OR Heists Any (Take > 500)"));
    }

    /// <summary>Two questions about one collection, which is two quantifiers rather than one</summary>
    [Fact]
    public void QuantifiersCompose()
    {
        Assert.Equal([1], Ids("Heists Any (Botched = false) AND Heists Any (Botched = true)"));
        Assert.Equal([1, 2, 3, 4], Ids("NOT (Heists Any (Take > 100 AND Botched = false AND Target = 'Museum'))"));
    }

    // ---------- the allow-list ----------

    [Fact]
    public void ACollectionNobodyBoundIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Ids("Jobs Any (Take > 1)"));

        Assert.Contains("Jobs", error.Message);
        Assert.Equal(WeequeryError.UnboundField, error.Error);
    }

    /// <summary>Binding the collection exposes nothing inside it, which is the same rule as everywhere else</summary>
    [Fact]
    public void AFieldTheInnerSetDidNotBindIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Ids("Heists Any (Fence.Name = 'Vic')"));

        Assert.Contains("Fence.Name", error.Message);
    }

    /// <summary>The outer bindings are not in scope inside, and the inner ones are not in scope outside</summary>
    [Fact]
    public void TheTwoAllowListsDoNotLeakIntoEachOther()
    {
        Assert.Throws<WeequeryException>(() => Ids("Heists Any (Name = 'Alpha')"));
        Assert.Throws<WeequeryException>(() => Ids("Take > 1"));
    }

    /// <summary>A collection is not something the comparison operators can be asked</summary>
    [Fact]
    public void ACollectionCannotBeCompared()
    {
        Assert.Throws<WeequeryException>(() => Ids("Heists = 'Bank'"));
        Assert.Throws<WeequeryException>(() => Ids("Heists IsNull"));
    }

    /// <summary>And a property is not something a quantifier can be asked</summary>
    [Fact]
    public void APropertyCannotBeQuantified()
    {
        var error = Assert.Throws<WeequeryException>(() => Ids("Name Any (Take > 1)"));

        Assert.Contains("Name", error.Message);
    }

    [Fact]
    public void ACollectionCannotShareAKeyWithAProperty()
    {
        Assert.Throws<WeequeryException>(() => Crews()
            .WithWeequery()
            .BindProperty(crew => crew.Name, "Heists")
            .BindCollection(crew => crew.Heists, "Heists", inner => inner.BindProperty(heist => heist.Take)));

        Assert.Throws<WeequeryException>(() => Crews()
            .WithWeequery()
            .BindCollection(crew => crew.Heists, "Heists", inner => inner.BindProperty(heist => heist.Take))
            .BindProperty(crew => crew.Name, "Heists"));
    }

    /// <summary>Nothing bound inside means no condition could ever be written, so it is refused at binding time</summary>
    [Fact]
    public void AnEmptyInnerSetIsRefused()
    {
        var error = Assert.Throws<WeequeryException>(() => Crews()
            .WithWeequery()
            .BindCollection(crew => crew.Heists, "Heists", inner => { }));

        Assert.Contains("Heists", error.Message);
    }

    // ---------- the text ----------

    [Fact]
    public void TheParenthesesAreRequired()
    {
        var error = Assert.Throws<WeequeryException>(() => Ids("Heists Any Take > 1"));

        Assert.Contains("'('", error.Message);
        Assert.Contains("Heists", error.Message);
    }

    /// <summary>An index names one element, and one element is not something to quantify over</summary>
    [Fact]
    public void AnIndexedFieldCannotBeQuantified()
    {
        var error = Assert.Throws<WeequeryException>(() => Ids("Heists[0] Any (Take > 1)"));

        Assert.Contains("Heists[0]", error.Message);
    }

    [Fact]
    public void AQuantifierWritesBackAsItReads()
    {
        Assert.Equal("([Heists] Any ([Take] > '500'))", Parsed("Heists Any (Take > 500)"));
        Assert.Equal("([Heists] None ([Target] = 'Bank'))", Parsed("Heists None (Target = 'Bank')"));
        Assert.Equal("([Heists] All (([Take] > '1') AND ([Botched] = 'false')))", Parsed("Heists All (Take > 1 AND Botched = false)"));

        // A negation leads with its operator rather than a parenthesis, so it gets a pair of its own
        Assert.Equal("([Heists] Any (NOT ([Take] > '1')))", Parsed("Heists Any (NOT (Take > 1))"));
    }

    private static string Parsed(string query)
    {
        var condition = ConditionFunctions.ParseQuery(query, QueryStyle.Native);

        return condition!.ToQuery(QueryStyle.Native);
    }

    /// <summary>What is written has to select the same rows when it is read back, which is what round trip means</summary>
    [Fact]
    public void TheRoundTripSelectsTheSameRows()
    {
        foreach (var query in new[]
        {
            "Heists Any (Take > 100 AND Botched = true)",
            "Heists All (Take > 100)",
            "Heists None (Target = 'Bank')",
            "Name = 'Alpha' OR Heists Any (Take IsBetween (1, 60))",
            "Heists Any (NOT (Botched = true))",
        })
        {
            Assert.Equal(Ids(query), Ids(Parsed(query)));
        }
    }

    /// <summary>
    /// The combined string splits where the condition stops, and the parser is what finds that. A quantifier
    /// ends at its closing parenthesis, so the sort clause after one is still read as a sort clause.
    /// </summary>
    [Fact]
    public void AQuantifierDoesNotSwallowTheSortClause()
    {
        var parsed = ParsedQuery.Parse("Heists Any (Target = 'Bank') OrderBy Name DESC", style: QueryStyle.Native);

        Assert.NotNull(parsed.Condition);
        Assert.Equal("([Heists] Any ([Target] = 'Bank'))", parsed.Condition!.ToQuery(QueryStyle.Native));
        Assert.Equal("Name", Assert.Single(parsed.Sorts).Field);

        var ordered = Bound().ApplyCondition(parsed.Condition).ApplySorts(parsed.Sorts).Build().ToList();

        Assert.Equal(["Beta", "Alpha"], ordered.Select(crew => crew.Name));
    }

    /// <summary>The wire format did not have to change: an operator, a field and one child was already its shape</summary>
    [Fact]
    public void AQuantifierPacksAndUnpacks()
    {
        const string query = "Heists Any (Take > 100 AND Botched = true)";

        var packed = ConditionFunctions.ParseQuery(query, QueryStyle.Native)!.Pack();
        var json = System.Text.Json.JsonSerializer.Serialize(packed);
        var read = System.Text.Json.JsonSerializer.Deserialize<PackedCondition>(json)!;

        Assert.Equal(Operator.Any, read.Operator);
        Assert.Equal("Heists", read.Field);

        Assert.Equal([2], [.. Bound().ApplyCondition(read.Unpack()).Build().ToList().Select(crew => crew.Id)]);
    }

    [Fact]
    public void AQuantifierBuiltByHandBehavesTheSame()
    {
        var condition = new QuantifiedCondition(Operator.Any, "Heists", new OneValueCondition<int>(Operator.GreaterThan, "Take", 500));

        Assert.Equal([2], [.. Bound().ApplyCondition(condition).Build().ToList().Select(crew => crew.Id)]);
        Assert.Equal("([Heists] Any ([Take] > 500))", condition.ToString());
    }

    /// <summary>
    /// An operator travels as its number, so the three went on the end. Renumbering anything before them would
    /// change what payloads already in flight mean, see <see cref="IsMatchTests"/>.
    /// </summary>
    [Fact]
    public void TheQuantifiersAreNumberedAfterEverythingThatCameBeforeThem()
    {
        Assert.Equal(23, (int)Operator.Any);
        Assert.Equal(24, (int)Operator.All);
        Assert.Equal(25, (int)Operator.None);
    }

    [Fact]
    public void OnlyTheThreeQuantifiersMakeOne()
    {
        Assert.Throws<WeequeryException>(() => new QuantifiedCondition(Operator.Equals, "Heists", new NoValueCondition(Operator.IsNull, "Take")));
    }
}
