using Microsoft.EntityFrameworkCore;
using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Normalising a binding's values, so a comparison agrees about things the stored data and the caller spell
/// differently.
/// <para>
/// Which side it runs against is the whole point, see <see cref="ConversionTarget"/>: the caller's value, the
/// row's value, or both. Most of what these check is that each of the three does exactly what it says and
/// nothing more.
/// </para>
/// </summary>
public class ValueConverterTests
{
    private static Inquiry<Minion> Bound(ConversionTarget applies)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name)
            .BindProperty(minion => minion.Alias, "Alias", convert: ValueConverter.For<string>(alias => alias!.ToUpper(), applies));
    }

    private static string[] Names(ConversionTarget applies, string query)
    {
        return [.. Bound(applies).ApplyCondition(query).Build().ToList().Select(minion => minion.Name).Order()];
    }

    // ---------- each side does what it says ----------

    /// <summary>Both sides folded, so the comparison agrees however either was written. Alice is stored "Ghost".</summary>
    [Fact]
    public void BothFoldsEverySideOfTheComparison()
    {
        Assert.Equal(["Alice Fox"], Names(ConversionTarget.Both, "Alias = 'ghost'"));
        Assert.Equal(["Alice Fox"], Names(ConversionTarget.Both, "Alias = 'GHOST'"));
        Assert.Equal(["Alice Fox"], Names(ConversionTarget.Both, "Alias = 'Ghost'"));
    }

    /// <summary>
    /// The caller's value only, which is the setting for a column already stored normalised. Every minion's
    /// currency is stored "US$", so folding what the caller wrote is enough to meet it.
    /// </summary>
    [Fact]
    public void ClientFoldsOnlyWhatTheCallerWrote()
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.PreferredCurrency, "Currency",
                convert: ValueConverter.For<string>(text => text!.ToUpper(), ConversionTarget.Value));

        Assert.Equal(4, inquiry.ApplyCondition("Currency = 'us$'").Build().Count());
    }

    /// <summary>
    /// And it does not touch the column, so a fold the stored value does not already agree with matches nothing.
    /// Alias is stored "Ghost", and upper casing what the caller wrote can never produce that.
    /// </summary>
    [Fact]
    public void ClientAloneCannotReachAnUnnormalisedColumn()
    {
        Assert.Empty(Names(ConversionTarget.Value, "Alias = 'ghost'"));
        Assert.Empty(Names(ConversionTarget.Value, "Alias = 'GHOST'"));
        Assert.Equal(["Alice Fox"], Names(ConversionTarget.Both, "Alias = 'ghost'"));
    }

    /// <summary>The row's value only, which is the mirror image of it</summary>
    [Fact]
    public void SourceFoldsOnlyWhatTheRowHolds()
    {
        Assert.Equal(["Alice Fox"], Names(ConversionTarget.Binding, "Alias = 'GHOST'"));
        Assert.Empty(Names(ConversionTarget.Binding, "Alias = 'ghost'"));
    }

    [Fact]
    public void NoneLeavesBothAlone()
    {
        Assert.Equal(["Alice Fox"], Names(ConversionTarget.None, "Alias = 'Ghost'"));
        Assert.Empty(Names(ConversionTarget.None, "Alias = 'ghost'"));
    }

    [Fact]
    public void BothIsTheDefaultAndIsTheTwoFlags()
    {
        Assert.Equal(ConversionTarget.Both, ConversionTarget.Value | ConversionTarget.Binding);
        Assert.Equal(ConversionTarget.Both, ValueConverter.For<string>(text => text).Applies);
    }

    // ---------- every operator, and every value an operator holds ----------

    [Fact]
    public void EveryOperatorGetsIt()
    {
        Assert.Equal(["Alice Fox"], Names(ConversionTarget.Both, "Alias StartsWith 'gho'"));
        Assert.Equal(["Alice Fox"], Names(ConversionTarget.Both, "Alias Contains 'hos'"));
        Assert.Equal(["Alice Fox"], Names(ConversionTarget.Both, "Alias EndsWith 'ost'"));
        Assert.Equal(["Alice Fox", "Charlie Smith", "David Edgars"], Names(ConversionTarget.Both, "Alias <> 'nobody'"));
    }

    /// <summary>Both ends of a range and every entry of a list, not just the first</summary>
    [Fact]
    public void EveryValueAnOperatorHoldsGetsIt()
    {
        Assert.Equal(["Alice Fox", "Charlie Smith"], Names(ConversionTarget.Both, "Alias IsIn ('ghost', 'snake')"));
        Assert.Equal(["David Edgars"], Names(ConversionTarget.Both, "Alias IsIn ('babyface')"));
        Assert.Equal(["Alice Fox", "David Edgars"], Names(ConversionTarget.Both, "Alias IsBetween ('babyface', 'ghost')"));
    }

    /// <summary>A condition built in code rather than parsed takes the same route through the builders</summary>
    [Fact]
    public void ATypedConditionIsConvertedToo()
    {
        var condition = new OneValueCondition<string>(Operator.Equals, "Alias", "ghost");

        var names = Bound(ConversionTarget.Both).ApplyCondition(condition).Build().ToList().Select(minion => minion.Name);

        Assert.Equal(["Alice Fox"], names);
    }

    // ---------- what it deliberately leaves alone ----------

    /// <summary>
    /// A null test asks whether there is a value at all, which no normalisation changes. Bob has no alias.
    /// </summary>
    [Fact]
    public void ANullTestIsUntouched()
    {
        Assert.Equal(["Bob Samuelson"], Names(ConversionTarget.Both, "Alias IsNull"));
        Assert.Equal(["Alice Fox", "Charlie Smith", "David Edgars"], Names(ConversionTarget.Both, "Alias IsNotNull"));
    }

    /// <summary>An order should be the order of the real data, so a sort reads the stored value</summary>
    [Fact]
    public void ASortReadsTheStoredValue()
    {
        var ordered = MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name, "Name", convert: ValueConverter.For<string>(name => name.ToUpper()))
            .ApplySorts([new Sort("Name", SortDirection.Ascending)])
            .Build()
            .ToList()
            .Select(minion => minion.Name);

        Assert.Equal(["Alice Fox", "Bob Samuelson", "Charlie Smith", "David Edgars"], ordered);
    }

    /// <summary>And a projected row should hand back what is really in it</summary>
    [Fact]
    public void AProjectionReadsTheStoredValue()
    {
        var row = Bound(ConversionTarget.Both).ApplyProjection("Alias").ApplyCondition("Alias = 'ghost'").BuildProjected().Single();

        Assert.Equal("Ghost", row["Alias"]);
    }

    // ---------- other shapes of binding ----------

    /// <summary>
    /// A conversion is declared for the unwrapped type, so a DateTime? property takes a converter of DateTime
    /// and never has to think about the null: the guard has already decided there is a value to fold.
    /// </summary>
    [Fact]
    public void ANullablePropertyTakesAConverterOfItsUnwrappedType()
    {
        // An Inquiry is mutable and conditions accumulate, so each of these gets its own
        static Inquiry<Minion> Folded()
        {
            return MinionTestData.Minions()
                .WithWeequery()
                .BindProperty(minion => minion.Name)
                .BindProperty(minion => minion.FireDate, "Fired",
                    convert: ValueConverter.For<DateTime>(fired => new DateTime(fired.Year, 1, 1)));
        }

        // Both sides fold to the start of the year, so any date in 2024 finds Bob, who was fired that December
        Assert.Equal(["Bob Samuelson"], Folded().ApplyCondition("Fired = '2024-06-15'").Build().ToList().Select(minion => minion.Name));

        // And the three with no fire date at all are still null rather than folded to anything
        Assert.Equal(3, Folded().ApplyCondition("Fired IsNull").Build().Count());
    }

    /// <summary>
    /// A converter is caller code and can throw. On the client side it happens once, while the query is being
    /// built, and is reported as what it is rather than as a parse failure.
    /// </summary>
    [Fact]
    public void AConverterThatThrowsOnTheClientSideSaysSo()
    {
        var inquiry = MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Morale, "Morale",
                convert: ValueConverter.For<sbyte>(morale => checked((sbyte)-morale), ConversionTarget.Value));

        var error = Assert.Throws<WeequeryException>(() => inquiry.ApplyCondition("Morale = -128").Build().ToList());

        Assert.Contains("SByte", error.Message);
        Assert.Contains("-128", error.Message);
    }

    [Fact]
    public void AConverterForTheWrongTypeIsRefusedWhereItIsDeclared()
    {
        var error = Assert.Throws<WeequeryException>(() => MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Pay, "Pay", convert: ValueConverter.For<string>(text => text.ToUpper())));

        Assert.Contains("Pay", error.Message);
        Assert.Contains("String", error.Message);
        Assert.Contains("Decimal", error.Message);
    }

    [Fact]
    public void AConversionIsRequired()
    {
        Assert.Throws<WeequeryException>(() => ValueConverter.For<string>(null!));
    }
    /// <summary>
    /// Both sides of a comparison between two properties are source data, so each gets its own. They have to be
    /// the one converter, shared: see <see cref="ComparingTwoPropertiesNormalisedDifferentlyIsRefused"/>.
    /// </summary>
    [Fact]
    public void ComparingTwoPropertiesUsesEachOnesOwnConverter()
    {
        var upper = ValueConverter.For<string>(text => text!.ToUpper());

        var names = MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name)
            .BindProperty(minion => minion.PreferredCurrency, "Currency", convert: upper)
            .BindConstant("Wanted", "us$", convert: upper)
            .ApplyCondition("Currency = [Wanted]")
            .Build()
            .ToList()
            .Select(minion => minion.Name);

        Assert.Equal(4, names.Count());
    }

    /// <summary>
    /// Two sides normalised differently compare one normalisation against the other, which is an answer about
    /// neither, so it is refused rather than answered. Folding one conversion through the other instead would
    /// make the comparison depend on which side was written first, and would change what two matching bindings
    /// mean wherever the conversion is not idempotent.
    /// </summary>
    [Fact]
    public void ComparingTwoPropertiesNormalisedDifferentlyIsRefused()
    {
        static Inquiry<Minion> Bound(ValueConverter? left, ValueConverter? right) => MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name, convert: left)
            .BindProperty(minion => minion.Alias, convert: right);

        var upper = ValueConverter.For<string>(text => text!.ToUpper());
        var lower = ValueConverter.For<string>(text => text!.ToLower());

        // two different conversions
        Assert.Throws<WeequeryException>(() => Bound(upper, lower).ApplyCondition("Name == [Alias]").Build().ToList());

        // one side converted and the other not, either way round
        Assert.Throws<WeequeryException>(() => Bound(upper, null).ApplyCondition("Name == [Alias]").Build().ToList());
        Assert.Throws<WeequeryException>(() => Bound(null, upper).ApplyCondition("Name == [Alias]").Build().ToList());

        // and two conversions that read alike but are two objects, since nothing here can tell that they agree
        Assert.Throws<WeequeryException>(() => Bound(upper, ValueConverter.For<string>(text => text!.ToUpper()))
            .ApplyCondition("Name == [Alias]").Build().ToList());

        // the one converter, shared, is the way to say they agree
        Assert.Empty(Bound(upper, upper).ApplyCondition("Name == [Alias]").Build().ToList());
    }

    /// <summary>
    /// A converter that runs only against a caller's value never reached the accessor, so it is not a difference
    /// between two sides of a comparison that has no caller's value in it at all
    /// </summary>
    [Fact]
    public void AValueOnlyConverterIsNotADifferenceBetweenTwoProperties()
    {
        var names = MinionTestData.Minions()
            .WithWeequery()
            .BindProperty(minion => minion.Name, convert: ValueConverter.For<string>(text => text.ToUpper(), ConversionTarget.Value))
            .BindProperty(minion => minion.Alias)
            .ApplyCondition("Name == [Alias]")
            .Build()
            .ToList();

        Assert.Empty(names);
    }

    /// <summary>An index makes a binding of the element's type, which a converter for the collection is not for</summary>
    [Fact]
    public void AnIndexedBindingDoesNotInheritOne()
    {
        var boxes = new List<Box>
        {
            new() { Id = 1, Labels = ["first"] },
        }.AsQueryable();

        var inquiry = boxes
            .WithWeequery()
            .BindProperty(box => box.Id)
            .BindProperty(box => box.Labels!, "Labels");

        // The collection binding carries no converter of its own, and indexing it produces a plain string binding
        Assert.Single(inquiry.ApplyCondition("Labels[0] = 'first'").Build().ToList());
        Assert.Empty(inquiry.ApplyCondition("Labels[0] = 'FIRST'").Build().ToList());
    }

    // ---------- against a provider ----------

    /// <summary>
    /// The source half becomes part of the SQL, which is the reason it is an expression and not a delegate. A
    /// converter a provider cannot translate fails when the query runs, and that is on whoever wrote it.
    /// </summary>
    [Theory]
    [MemberData(nameof(TestDatabase.AllProviders), MemberType = typeof(TestDatabase))]
    public void TheSourceHalfTranslates(TestProvider provider)
    {
        using var context = CollectionIndexProviderTests.Context(provider);

        var sql = context.Shipments
            .WithWeequery()
            .BindProperty(shipment => shipment.Id)
            .BindProperty(shipment => shipment.Tags, "Tags")
            .BindProperty("Id", "Folded", convert: ValueConverter.For<int>(id => id * 2))
            .ApplyCondition("Folded = 4")
            .Build()
            .ToQueryString()
            .Replace("\r", " ")
            .Replace("\n", " ");

        // The fold is in the WHERE rather than done after the rows arrive, and the value still parameterizes
        Assert.Contains("WHERE", sql);
        Assert.Contains("@Value", sql);
        Assert.DoesNotContain(" 4", sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..]);
    }
}
