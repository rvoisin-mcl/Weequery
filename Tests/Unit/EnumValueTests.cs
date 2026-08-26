using Tests.Common;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// Enum members were the one thing in the query language a caller had to spell exactly, and the one place a value
/// that named nothing was accepted anyway. Both are the same complaint from opposite ends: a name that is wrong
/// should be refused, and a name that is merely cased differently should not be.
/// </summary>
public class EnumValueTests
{
    [Flags]
    public enum Access
    {
        None = 0,
        Read = 1,
        Write = 2,
    }

    private static int Count(string query)
    {
        return MinionTestData.Minions()
            .WithWeequery()
            .BindProperties(Minion.Bindings)
            .ApplyCondition(query)
            .Build()
            .Count();
    }

    /// <summary>
    /// Only David is Irreplacable, whichever way the caller writes it
    /// </summary>
    [Theory]
    [InlineData("Irreplacable")]
    [InlineData("irreplacable")]
    [InlineData("IRREPLACABLE")]
    [InlineData("iRrEpLaCaBlE")]
    public void AMemberIsFoundWhateverTheCase(string member)
    {
        Assert.Equal(1, Count($"Classification == {member}"));
    }

    [Fact]
    public void AQuotedMemberReadsTheSameWay()
    {
        Assert.Equal(1, Count("Classification == 'irreplacable'"));
    }

    [Fact]
    public void AMemberThatDoesNotExistIsStillRefused()
    {
        Assert.Throws<WeequeryException>(() => Count("Classification == Priceless"));
    }

    /// <summary>
    /// The other half: a number in range of the underlying type used to be accepted whether or not it named a
    /// member, so a filter for something that does not exist quietly matched nothing instead of saying so
    /// </summary>
    [Fact]
    public void ANumberThatNamesNoMemberIsRefused()
    {
        Assert.Throws<WeequeryException>(() => Count("Classification == 99"));
    }

    /// <summary>
    /// A number that does name one still reads, since it means exactly what the name means
    /// </summary>
    [Fact]
    public void ANumberThatNamesAMemberStillReads()
    {
        Assert.Equal(Count($"Classification == {Classification.Irreplacable}"), Count($"Classification == {(int)Classification.Irreplacable}"));
    }

    // ---------- the parser underneath ----------

    [Theory]
    [InlineData("Read", Access.Read)]
    [InlineData("read", Access.Read)]
    [InlineData("WRITE", Access.Write)]
    [InlineData("None", Access.None)]
    [InlineData("0", Access.None)]
    [InlineData("1", Access.Read)]
    public void ValueFormatParsesAMember(string text, Access expected)
    {
        Assert.Equal(expected, ValueFormat.Parse(typeof(Access), text));
    }

    /// <summary>
    /// Only numbers are held to naming a member, so the combined form of a [Flags] enum, which is written as
    /// names, still reads even though no single member is called "Read, Write"
    /// </summary>
    [Theory]
    [InlineData("Read, Write")]
    [InlineData("read, write")]
    public void ValueFormatParsesACombinationOfFlags(string text)
    {
        Assert.Equal(Access.Read | Access.Write, ValueFormat.Parse(typeof(Access), text));
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("+7")]
    public void ValueFormatRefusesANumberThatNamesNoMember(string text)
    {
        Assert.Throws<WeequeryException>(() => ValueFormat.Parse(typeof(Access), text));
    }

    [Fact]
    public void ValueFormatRefusesANameThatIsNotAMember()
    {
        Assert.Throws<WeequeryException>(() => ValueFormat.Parse(typeof(Access), "Execute"));
    }

    /// <summary>
    /// A round trip still lands on the same value, which is what the packed form depends on
    /// </summary>
    [Fact]
    public void AMemberRoundTripsThroughItsInvariantString()
    {
        var text = ValueFormat.ToInvariantString(Classification.Irreplacable);

        Assert.Equal(Classification.Irreplacable, ValueFormat.Parse(typeof(Classification), text));
    }

    // ---------- an enum may be based on any of the eight integer types ----------

    public enum OfSByte : sbyte { First = 1, Second = 2 }
    public enum OfByte : byte { First = 1, Second = 2 }
    public enum OfShort : short { First = 1, Second = 2 }
    public enum OfUShort : ushort { First = 1, Second = 2 }
    public enum OfInt : int { First = 1, Second = 2 }
    public enum OfUInt : uint { First = 1, Second = 2 }
    public enum OfLong : long { First = 1, Second = 2 }
    public enum OfULong : ulong { First = 1, Second = 2 }

    public enum Signed : sbyte { Below = -1, Above = 1 }
    public enum PastLong : ulong { Big = ulong.MaxValue }

    [Flags]
    public enum ByteFlags : byte { Read = 1, Write = 2, Execute = 4 }

    public static TheoryData<Type> EveryUnderlyingType()
    {
        return new TheoryData<Type>(
            typeof(OfSByte), typeof(OfByte), typeof(OfShort), typeof(OfUShort),
            typeof(OfInt), typeof(OfUInt), typeof(OfLong), typeof(OfULong));
    }

    /// <summary>
    /// The trap this covers: a boxed enum only unboxes to the exact type it is based on, so reading members as
    /// int worked for a default enum and threw InvalidCastException for every other kind. Every fixture enum here
    /// was int based, so nothing noticed.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryUnderlyingType))]
    public void AMemberReadsWhateverTheEnumIsBasedOn(Type type)
    {
        Assert.Equal("Second", ValueFormat.Parse(type, "Second").ToString());
        Assert.Equal("Second", ValueFormat.Parse(type, "second").ToString());
        Assert.Equal("Second", ValueFormat.Parse(type, "2").ToString());
    }

    [Theory]
    [MemberData(nameof(EveryUnderlyingType))]
    public void ANumberThatNamesNoMemberIsRefusedWhateverTheEnumIsBasedOn(Type type)
    {
        Assert.Throws<WeequeryException>(() => ValueFormat.Parse(type, "99"));
    }

    /// <summary>
    /// The two ends of the range, which are where converting rather than reinterpreting the bits would go wrong:
    /// a negative member of a signed enum, and a member past long.MaxValue in an unsigned one
    /// </summary>
    [Fact]
    public void TheEndsOfTheRangeRead()
    {
        Assert.Equal(Signed.Below, ValueFormat.Parse(typeof(Signed), "-1"));
        Assert.Equal(Signed.Below, ValueFormat.Parse(typeof(Signed), "Below"));

        Assert.Equal(PastLong.Big, ValueFormat.Parse(typeof(PastLong), ulong.MaxValue.ToString()));
        Assert.Equal(PastLong.Big, ValueFormat.Parse(typeof(PastLong), "Big"));
    }

    [Fact]
    public void FlagsCombineWhateverTheEnumIsBasedOn()
    {
        Assert.Equal(ByteFlags.Read | ByteFlags.Write, ValueFormat.Parse(typeof(ByteFlags), "Read, Write"));
        Assert.Equal(ByteFlags.Read | ByteFlags.Write, ValueFormat.Parse(typeof(ByteFlags), "3"));
    }

    [Theory]
    [InlineData("8")]   // a bit no member claims
    [InlineData("0")]   // no member is zero
    public void ANumberThatIsNoCombinationIsRefused(string text)
    {
        Assert.Throws<WeequeryException>(() => ValueFormat.Parse(typeof(ByteFlags), text));
    }

    /// <summary>
    /// Only a set of flags combines. A plain enum whose members happen to be bit disjoint does not.
    /// </summary>
    [Fact]
    public void APlainEnumDoesNotCombineItsMembers()
    {
        Assert.Throws<WeequeryException>(() => ValueFormat.Parse(typeof(OfByte), "3"));
    }

    private class Minion2
    {
        public OfByte Size { get; set; }
    }

    /// <summary>
    /// And the whole way through, since this is how it reached a caller: as "Failed to parse OfByte"
    /// </summary>
    [Fact]
    public void AQueryFiltersOnAnEnumThatIsNotIntBased()
    {
        var rows = new List<Minion2> { new() { Size = OfByte.First }, new() { Size = OfByte.Second } }.AsQueryable();

        Assert.Equal(1, rows.WithWeequery().BindProperty(row => row.Size, "Size").ApplyCondition("Size == second").Build().Count());
    }
}
