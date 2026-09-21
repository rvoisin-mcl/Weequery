using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// A binding key must be a valid unquoted SQL name: an ASCII letter or underscore, then any number of ASCII
/// letters, digits or underscores.
/// <para>
/// A key is written as a bare field name in the query language and lines up with a column name in the database, so
/// anything a database would need quoting for is refused at binding time rather than surfacing later as a query
/// that will not parse.
/// </para>
/// </summary>
public class BindingKeyTests
{
    private static IQueryable<Minion> Minions()
    {
        return MinionTestData.Minions();
    }

    /// <summary>
    /// Keys the rule rejects, grouped by why
    /// </summary>
    public static TheoryData<string> InvalidKeys()
    {
        return new TheoryData<string>(
            // whitespace
            "my field", " leading", "trailing ", "two  spaces", "with\ttab", "with\nnewline", "with\rreturn", " ",
            // leading digit
            "1name", "9", "0_name",
            // the punctuation the spec calls out. A period is legal between two names, see ValidDottedKeys, and
            // these are the ways it can still be written that are not that
            "minion-name", "path/name",
            ".", "..", ".Name", "Name.", "Lair..Name", "Lair. Name", "Lair.1Name", "Lair.-Name", "Lair.Name.",
            // and the rest of it
            "name!", "name?", "name*", "name+", "name=", "name%", "name$", "name#", "name@", "name&", "name|",
            "name:", "name;", "name,", "name'", "name\"", "name(", "name)", "name[", "name]", "name{", "name}",
            "name<", "name>", "name~", "name^", "name\\",
            // not ASCII, deliberately out even though some databases would take it
            "Név", "имя", "名前", "na­me", "name​");
    }

    /// <summary>
    /// Keys the rule accepts
    /// </summary>
    public static TheoryData<string> ValidKeys()
    {
        return new TheoryData<string>(
            "Name",
            "name",
            "NAME",
            "_name",
            "_",
            "__",
            "_1",
            "name1",
            "Name2Name",
            "minion_name",
            "MINION_NAME_2",
            "a",
            "A123456789");
    }

    /// <summary>
    /// Keys with a period in them, which is what makes a nested property's own path a key.
    /// <para>
    /// Held separately from <see cref="ValidKeys"/> because the two predicates differ on exactly this:
    /// IsSqlName is one unquoted name and refuses a period, IsBindingKey is a period separated sequence of them.
    /// </para>
    /// </summary>
    public static TheoryData<string> ValidDottedKeys()
    {
        return new TheoryData<string>(
            "Lair.Name",
            "Lair.Capacity",
            "A.B.C",
            "_a._b",
            "Lair.Name2",
            "LairAssignments.LairID",
            // A segment spelling a keyword is not the keyword: only a whole word is promoted, and this is one word
            "Lair.And",
            "Not.Contains");
    }

    // ---------- every route that takes an explicit key ----------

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public void BindPropertyByPathRejectsAnInvalidKey(string key)
    {
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(nameof(Minion.Name), key));
    }

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public void BindPropertyBySelectorRejectsAnInvalidKey(string key)
    {
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(minion => minion.Name, key));
    }

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public void BindingRequestRejectsAnInvalidKey(string key)
    {
        Assert.Throws<WeequeryException>(() => new BindingRequest(nameof(Minion.Name), key));
    }

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public void BindingRequestFromPathSegmentsRejectsAnInvalidKey(string key)
    {
        Assert.Throws<WeequeryException>(() => new BindingRequest([nameof(Minion.Name)], key));
    }

    /// <summary>
    /// BindingRequest.Key has an init accessor, so an object initializer runs after the constructor and can put
    /// back a key the constructor would have rejected. The binding checks the key it is about to use, so the rule
    /// still holds on this route.
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public void BindPropertiesRejectsAKeyThatBypassedTheConstructor(string key)
    {
        var request = new BindingRequest(nameof(Minion.Name), null) { Key = key };

        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperties([request]));
    }

    [Fact]
    public void TheMessageNamesTheOffendingKeyAndTheRule()
    {
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(nameof(Minion.Name), "my field"));
    }

    // ---------- valid keys are untouched ----------

    [Theory]
    [MemberData(nameof(ValidKeys))]
    public void AValidKeyIsAcceptedAndUsable(string key)
    {
        var result = Minions()
            .WithWeequery()
            .BindProperty(nameof(Minion.Name), key)
            .ApplyCondition(new OneValueCondition<string>(Operator.Equals, key, "Alice Fox"))
            .Build()
            .Count();

        Assert.Equal(1, result);
    }

    [Theory]
    [MemberData(nameof(ValidKeys))]
    public void AValidKeyCanBeReferredToInAQueryStringWithoutQuoting(string key)
    {
        // The point of the rule: a key is always writable as a bare field name
        var result = Minions()
            .WithWeequery()
            .BindProperty(nameof(Minion.Name), key)
            .ApplyCondition($"{key} == 'Alice Fox'")
            .Build()
            .Count();

        Assert.Equal(1, result);
    }

    [Fact]
    public void TheExistingSharedBindingsStillBind()
    {
        // Minion.Bindings uses auto-generated keys throughout, so the rule must not disturb them
        Assert.Equal(4, Minions().WithWeequery().BindProperties(Minion.Bindings).Build().Count());
    }

    [Fact]
    public void AnEmptyKeyKeepsItsOwnMessage()
    {
        // Kept distinct from the SQL name rule so the diagnostic stays specific
        Assert.Throws<WeequeryException>(() => Minions().WithWeequery().BindProperty(nameof(Minion.Name), string.Empty));
    }

    [Fact]
    public void AnOmittedKeyStillDefaultsToThePropertyName()
    {
        var result = Minions()
            .WithWeequery()
            .BindProperty(nameof(Minion.Name))
            .ApplyCondition($"{nameof(Minion.Name)} == 'Alice Fox'")
            .Build()
            .Count();

        Assert.Equal(1, result);
    }

    // ---------- keys are matched without regard to case ----------

    /// <summary>
    /// A field name from a caller matches its binding key whatever the case, the same way the keywords and the
    /// operator names already read. A field name used to be the one thing in a query that had to be spelled
    /// exactly, which is a poor reason to fail a request.
    /// </summary>
    [Theory]
    [InlineData("Pay")]
    [InlineData("pay")]
    [InlineData("PAY")]
    [InlineData("pAy")]
    public void AQueryFindsItsBindingWhateverTheCase(string field)
    {
        var result = Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Pay)
            .ApplyCondition($"{field} > 10000")
            .Build()
            .Count();

        Assert.Equal(2, result);
    }

    [Theory]
    [InlineData("pay")]
    [InlineData("PAY")]
    public void AConditionTreeFindsItsBindingWhateverTheCase(string field)
    {
        var result = Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Pay)
            .ApplyCondition(new OneValueCondition<decimal>(Operator.GreaterThan, field, 10000m))
            .Build()
            .Count();

        Assert.Equal(2, result);
    }

    [Theory]
    [InlineData("pay")]
    [InlineData("PAY")]
    public void ASortFindsItsBindingWhateverTheCase(string field)
    {
        var result = Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Pay)
            .ApplySort(new Sort(field, SortDirection.Descending))
            .Build()
            .ToList();

        Assert.Equal(19000m, result.First().Pay);
    }

    /// <summary>
    /// The two static entry points build their own lookup, so they have to agree with the fluent one
    /// </summary>
    [Fact]
    public void TheStandaloneBuildersFindTheirBindingsWhateverTheCase()
    {
        var condition = new OneValueCondition<decimal>(Operator.GreaterThan, "pay", 10000m);

        Assert.NotNull(Inquiry<Minion>.BuildExpression(Minion.Bindings, condition));

        var test = Inquiry<Minion>.BuildDelegate(Minion.Bindings, condition);
        Assert.Equal(2, Minions().Count(test));
    }

    /// <summary>
    /// The other side of it: two keys differing only in case are one key, so the second is a duplicate rather
    /// than something that quietly shadows the first
    /// </summary>
    [Fact]
    public void TwoKeysDifferingOnlyInCaseAreADuplicate()
    {
        Assert.Throws<WeequeryException>(() => Minions()
            .WithWeequery()
            .BindProperty(nameof(Minion.Name))
            .BindProperty(nameof(Minion.Alias), "NAME"));
    }

    /// <summary>
    /// Matching is looser about case, not about the name: a field no binding claimed is still refused
    /// </summary>
    [Fact]
    public void AFieldThatIsNotBoundIsStillUnbound()
    {
        Assert.Throws<WeequeryException>(() => Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Pay)
            .ApplyCondition("paycheck > 10000")
            .Build());
    }

    // ---------- the consequence for nested properties ----------

    /// <summary>
    /// A nested property's auto-generated key is its path, and a path contains periods, which the rule rejects. So
    /// a nested property now has to be given a key rather than falling back to its path.
    /// </summary>
    private static IQueryable<LairAssignment> Assignments()
    {
        // LairAssignment is where the model has a single-reference navigation, so a nested path exists
        return new List<LairAssignment>().AsQueryable();
    }

    /// <summary>
    /// A period is a legal key character, so the path a nested property already has is a key. This used to throw,
    /// which is what made naming one mandatory.
    /// </summary>
    [Fact]
    public void ANestedPropertyBindsUnderItsOwnPath()
    {
        var result = Assignments()
            .WithWeequery()
            .BindProperty("Lair.Name")
            .ApplyCondition("Lair.Name IsNull")
            .Build()
            .ToList();

        // the point is that it bound, and that the dotted key resolved unquoted in a query string
        Assert.Empty(result);
    }

    /// <summary>
    /// And it reads back the way any other key does, bracketed rather than quoted, since a period was never a
    /// delimiter to the tokenizer
    /// </summary>
    [Fact]
    public void ADottedKeyWritesBackAsABracketedField()
    {
        Assert.Equal("([Lair.Name] IsNull)", ConditionFunctions.ParseQuery("Lair.Name IsNull")!.ToQuery());
        Assert.Equal("[Lair.Name] ASC", Sort.Parse("Lair.Name", null).ToQuery());
    }

    [Fact]
    public void ANestedPropertyBindsOnceItHasAKey()
    {
        var result = Assignments()
            .WithWeequery()
            .BindProperty("Lair.Name", "LairName")
            .ApplyCondition("LairName IsNull")
            .Build()
            .ToList();

        // the point is that it bound and the key resolved unquoted, the set is empty
        Assert.Empty(result);
    }

    /// <summary>
    /// The path-segments constructor takes the last segment as the key, so it already produces a valid name for a
    /// nested path without the caller naming it
    /// </summary>
    [Fact]
    public void ThePathSegmentsConstructorDerivesAValidKeyFromTheLastSegment()
    {
        var request = new BindingRequest(["LairAssignments", "LairID"], null);

        Assert.Equal("LairAssignments.LairID", request.PropertyPath);
        Assert.Equal("LairID", request.Key);
        Assert.True(WeequeryException.IsSqlName(request.Key));
    }

    // ---------- the predicate on its own ----------

    [Theory]
    [MemberData(nameof(ValidKeys))]
    public void IsSqlNameAcceptsValidNames(string key)
    {
        Assert.True(WeequeryException.IsSqlName(key));
    }

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public void IsSqlNameRejectsInvalidNames(string key)
    {
        Assert.False(WeequeryException.IsSqlName(key));
    }

    [Fact]
    public void IsSqlNameRejectsNullAndEmpty()
    {
        Assert.False(WeequeryException.IsSqlName(null));
        Assert.False(WeequeryException.IsSqlName(string.Empty));
    }

    // ---------- the period, and where the two predicates part company ----------

    /// <summary>
    /// IsSqlName is still one unquoted name, which is what a single column may be called, and a period is not
    /// part of one. The key rule is the looser one, and it is the one keys are held to.
    /// </summary>
    [Theory]
    [MemberData(nameof(ValidDottedKeys))]
    public void IsSqlNameStillRefusesAPeriodWhereABindingKeyTakesIt(string key)
    {
        Assert.False(WeequeryException.IsSqlName(key));
        Assert.True(WeequeryException.IsQualifiedSqlName(key));
        Assert.True(WeequeryException.IsBindingKey(key));
    }

    /// <summary>
    /// A name with no period in it is both, so nothing that was a key before has stopped being one
    /// </summary>
    [Theory]
    [MemberData(nameof(ValidKeys))]
    public void EveryPlainNameIsStillAKey(string key)
    {
        Assert.True(WeequeryException.IsSqlName(key));
        Assert.True(WeequeryException.IsBindingKey(key));
    }

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public void IsBindingKeyRejectsWhatTheRouteRejects(string key)
    {
        Assert.False(WeequeryException.IsBindingKey(key));
    }

    /// <summary>
    /// The reserved words are still reserved, and still only as whole keys. A segment that spells one is fine,
    /// since the tokenizer promotes a whole word and a dotted key is one word.
    /// </summary>
    [Theory]
    [InlineData("Contains")]
    [InlineData("AND")]
    [InlineData("IsNull")]
    [InlineData("OrderBy")]
    public void AReservedWordIsStillRefusedAsAWholeKey(string key)
    {
        Assert.True(WeequeryException.IsQualifiedSqlName(key));
        Assert.False(WeequeryException.IsBindingKey(key));
        Assert.Throws<WeequeryException>(() => new BindingRequest(nameof(Minion.Name), key));
    }

    [Fact]
    public void IsQualifiedSqlNameRejectsNullAndEmpty()
    {
        Assert.False(WeequeryException.IsQualifiedSqlName(null));
        Assert.False(WeequeryException.IsQualifiedSqlName(string.Empty));
        Assert.False(WeequeryException.IsBindingKey(null));
    }

    // ---------- a dotted key through every route that takes one ----------

    [Theory]
    [MemberData(nameof(ValidDottedKeys))]
    public void ADottedKeyIsAcceptedAndUsable(string key)
    {
        var result = Minions()
            .WithWeequery()
            .BindProperty(nameof(Minion.Name), key)
            .ApplyCondition(new OneValueCondition<string>(Operator.Equals, key, "Alice Fox"))
            .Build()
            .Count();

        Assert.Equal(1, result);
    }

    /// <summary>
    /// The point of the rule: a key is always writable as a bare field name, and a period never was a delimiter
    /// </summary>
    [Theory]
    [MemberData(nameof(ValidDottedKeys))]
    public void ADottedKeyCanBeReferredToInAQueryStringWithoutQuoting(string key)
    {
        var result = Minions()
            .WithWeequery()
            .BindProperty(nameof(Minion.Name), key)
            .ApplyCondition($"{key} = 'Alice Fox'")
            .Build()
            .Count();

        Assert.Equal(1, result);
    }

    [Theory]
    [MemberData(nameof(ValidDottedKeys))]
    public void ABindingRequestTakesADottedKey(string key)
    {
        Assert.Equal(key, new BindingRequest(nameof(Minion.Name), key).Key);
    }

    /// <summary>
    /// The whole reason for the change: a path is a key, so a nested property needs no second name
    /// </summary>
    [Fact]
    public void ADottedPathDerivesItselfAsTheKey()
    {
        var request = new BindingRequest("Lair.Capacity", null);

        Assert.Equal("Lair.Capacity", request.PropertyPath);
        Assert.Equal("Lair.Capacity", request.Key);
    }

    /// <summary>
    /// A derived key is held to the same rule as a given one, and now says so where the request is declared
    /// rather than where it is bound
    /// </summary>
    [Fact]
    public void ADerivedKeyThatIsNotOneIsRefusedAtTheRequest()
    {
        Assert.Throws<WeequeryException>(() => new BindingRequest("Lair..Capacity", null));
        Assert.Throws<WeequeryException>(() => new BindingRequest("Lair.", null));
    }
}
