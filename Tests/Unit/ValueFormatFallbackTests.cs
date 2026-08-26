using System.Globalization;
using System.Net;
using System.Numerics;
using Weequery;

namespace Tests.Unit;

/// <summary>
/// ValueFormat.Parse reads the types a binding can be built for directly. For anything else it used to look for a
/// static method called "Parse" and invoke whatever it found, taking on trust that the method returned the type it
/// was found on a Parse returning something else was called happily, and handed the caller a value of a type
/// nobody had asked for.
/// <para>
/// It now wants the type to say how one of it is read: IParsable&lt;T&gt; for preference, since that takes the
/// format provider everything here is invariant by, or a Parse(string) that returns one of itself.
/// </para>
/// </summary>
public class ValueFormatFallbackTests
{
    /// <summary>A type that says how it is read, the way the framework asks for</summary>
    public class Postcode : IParsable<Postcode>
    {
        public string Text { get; init; } = "";

        public static Postcode Parse(string s, IFormatProvider? provider)
        {
            return (s.StartsWith("AB")) ? new Postcode { Text = s } : throw new FormatException($"'{s}' is not a postcode");
        }

        public static bool TryParse(string? s, IFormatProvider? provider, out Postcode result)
        {
            result = new Postcode();
            return false;
        }
    }

    /// <summary>A type that reads a string the older way, as Version does</summary>
    public class Ticket
    {
        public string Text { get; init; } = "";

        public static Ticket Parse(string s) => new() { Text = s };
    }

    /// <summary>The shape that used to be invoked and should not be: its Parse is not about this type at all</summary>
    public class Mislabelled
    {
        public static int Parse(string s) => int.Parse(s, CultureInfo.InvariantCulture);
    }

    public class Opaque
    {
    }

    [Fact]
    public void ATypeThatImplementsIParsableIsRead()
    {
        var parsed = Assert.IsType<Postcode>(ValueFormat.Parse(typeof(Postcode), "AB12"));

        Assert.Equal("AB12", parsed.Text);
    }

    /// <summary>
    /// And a failure inside it comes back as a WeequeryException naming the text, not as the TargetInvocationException
    /// the reflection call produced
    /// </summary>
    [Fact]
    public void AFailureInsideItIsReported()
    {
        Assert.Throws<WeequeryException>(() => ValueFormat.Parse(typeof(Postcode), "nope"));
    }

    /// <summary>
    /// The older shape still reads, which is why the check is on what Parse returns rather than on the interface
    /// alone: Version reads a string and does not implement IParsable
    /// </summary>
    [Fact]
    public void ATypeWithOnlyAPlainParseIsStillRead()
    {
        Assert.Equal(new Version(1, 2, 3), ValueFormat.Parse(typeof(Version), "1.2.3"));

        var ticket = Assert.IsType<Ticket>(ValueFormat.Parse(typeof(Ticket), "T-1"));
        Assert.Equal("T-1", ticket.Text);
    }

    /// <summary>
    /// The defect this closes: a static Parse that returns something else is not the type saying how it is read
    /// </summary>
    [Fact]
    public void AParseThatReturnsAnotherTypeIsNotUsed()
    {
        Assert.Throws<WeequeryException>(() => ValueFormat.Parse(typeof(Mislabelled), "5"));
    }

    [Fact]
    public void ATypeThatSaysNothingIsRefused()
    {
        Assert.Throws<WeequeryException>(() => ValueFormat.Parse(typeof(Opaque), "x"));
    }

    /// <summary>
    /// A few types from the framework that are outside Weequery's own set, to show which route each takes
    /// </summary>
    [Fact]
    public void FrameworkTypesOutsideTheSupportedSetStillRead()
    {
        Assert.Equal(IPAddress.Parse("10.0.0.1"), ValueFormat.Parse(typeof(IPAddress), "10.0.0.1"));
        Assert.Equal(BigInteger.Parse("123456789012345678901234567890", CultureInfo.InvariantCulture), ValueFormat.Parse(typeof(BigInteger), "123456789012345678901234567890"));
    }

    /// <summary>
    /// And the types Weequery does bind are read by the parsers, not by any of this
    /// </summary>
    [Theory]
    [InlineData(typeof(decimal), "12.5")]
    [InlineData(typeof(int), "-7")]
    [InlineData(typeof(bool), "TRUE")]
    [InlineData(typeof(string), "as is")]
    [InlineData(typeof(Guid), "00000000-0000-0000-0000-000000000000")]
    [InlineData(typeof(DateTime), "2024-12-25T13:45:30.1230000")]
    [InlineData(typeof(TimeSpan), "01:02:03")]
    public void TheSupportedTypesAreUnaffected(Type type, string text)
    {
        Assert.NotNull(ValueFormat.Parse(type, text));
    }
}
