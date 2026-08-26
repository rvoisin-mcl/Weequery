using System.Globalization;
using System.Reflection;

namespace Weequery;

/// <summary>
/// Converts condition values to and from their string form.
/// <para>
/// Conditions are packed and transported as strings, so both directions must be independent of the culture of
/// whichever machine happens to be running. Everything here goes through <see cref="CultureInfo.InvariantCulture"/>
/// and uses round-trip formats, so a condition packed on one host produces the same values when unpacked on another.
/// </para>
/// </summary>
public static class ValueFormat
{
    /// <summary>
    /// Render a value as a string that <see cref="Parse"/> can turn back into the same value, whatever the
    /// current culture. Types with a round-trip format use it, so no precision or offset is lost.
    /// </summary>
    /// <param name="value"></param>
    /// <returns>the empty string if value is null</returns>
    public static string ToInvariantString(object? value)
    {
        return value switch
        {
            null => string.Empty,

            // Already a string, nothing to format
            string text => text,

            // "O" is the round-trip format: it keeps sub-second precision and, for DateTime, the Kind
            DateTime dateTime => dateTime.ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToString("O", CultureInfo.InvariantCulture),
            DateOnly dateOnly => dateOnly.ToString("O", CultureInfo.InvariantCulture),
            TimeOnly timeOnly => timeOnly.ToString("O", CultureInfo.InvariantCulture),

            // "c" is TimeSpan's culture independent format
            TimeSpan timeSpan => timeSpan.ToString("c", CultureInfo.InvariantCulture),

            // Covers the numerics (shortest round-trippable since .NET Core 3.0), bool, Guid and the enums
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),

            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Parsers for the types a Binding can actually be built for.
    /// <para>
    /// The number styles matter as much as the culture does. The default for the numeric Parse overloads is
    /// <see cref="NumberStyles.Number"/>, which allows group separators, and under the invariant culture the group
    /// separator is ','. That means "1234,56" (what a de-DE machine used to pack) parses happily as 123456 rather
    /// than being rejected. Round-trip formatting never emits group separators, so disallowing them here costs
    /// nothing and turns a silently wrong value into a clear error.
    /// </para>
    /// </summary>
    private static readonly Dictionary<Type, Func<string, object>> Parsers = new()
    {
        { typeof(bool), text => bool.Parse(text) },
        { typeof(char), text => char.Parse(text) },
        { typeof(Guid), text => Guid.Parse(text) },

        { typeof(byte), text => byte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) },
        { typeof(sbyte), text => sbyte.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) },
        { typeof(short), text => short.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) },
        { typeof(ushort), text => ushort.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) },
        { typeof(int), text => int.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) },
        { typeof(uint), text => uint.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) },
        { typeof(long), text => long.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) },
        { typeof(ulong), text => ulong.Parse(text, NumberStyles.Integer, CultureInfo.InvariantCulture) },

        { typeof(float), text => float.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) },
        { typeof(double), text => double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) },
        { typeof(decimal), text => decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture) },

        { typeof(DateTime), text => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) },
        { typeof(DateTimeOffset), text => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) },

        { typeof(TimeSpan), text => TimeSpan.Parse(text, CultureInfo.InvariantCulture) },
        { typeof(DateOnly), text => DateOnly.Parse(text, CultureInfo.InvariantCulture) },
        { typeof(TimeOnly), text => TimeOnly.Parse(text, CultureInfo.InvariantCulture) },
    };

    /// <summary>
    /// Ensure that the value given is representable by the enum: a member of it, or a combination of members where
    /// the enum is a set of flags.
    /// </summary>
    /// <param name="type"></param>
    /// <param name="value">as <see cref="Enum.TryParse(Type, string, bool, out object)"/> produced it</param>
    /// <returns></returns>
    private static bool IsValidEnumValue(Type type, object value)
    {
        if (!type.IsEnum) { return false; }
        if (Enum.IsDefined(type, value)) { return true; } // exact match, whatever the underlying type
        if (type.GetCustomAttribute<FlagsAttribute>() is null) { return false; } // not a set of flags, so nothing to combine

        ulong remainingBits = ThunkEnumValue(value);
        if (remainingBits == 0) { return false; } // zero is only valid as a member, which it was not

        foreach (var member in Enum.GetValues(type))
        {
            ulong flag = ThunkEnumValue(member);

            if ((flag != 0) && ((remainingBits & flag) == flag)) // If the flag's bits are fully present inside the value, clear them out
            {
                remainingBits &= ~flag;
            }
        }

        return remainingBits == 0; // if there is nothing left over, the value is valid
    }

    /// <summary>
    /// The bit pattern of an enum value, whatever integer type the enum is based on. The signed types are
    /// reinterpreted rather than converted, so a negative member keeps its bits instead of overflowing.
    /// </summary>
    /// <param name="value"></param>
    /// <returns></returns>
    private static ulong ThunkEnumValue(object value)
    {
        // For an enum, this is the type code of what it is based on
        return Convert.GetTypeCode(value) switch
        {
            TypeCode.SByte or TypeCode.Int16 or TypeCode.Int32 or TypeCode.Int64 => unchecked((ulong)Convert.ToInt64(value, CultureInfo.InvariantCulture)),

            _ => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// The one method name looked up by reflection here, and only on a type that has promised to have it
    /// </summary>
    private const string ParseMethod = "Parse";

    /// <summary>
    /// Whether the type implements <see cref="IParsable{TSelf}"/> for itself.
    /// <para>
    /// Asked of the interface rather than of a method called "Parse", which is not the same promise. Any type might
    /// happen to have a static Parse: it may be culture sensitive, where everything here is deliberately invariant,
    /// and it need not even return the type it was found on, which would hand the caller back something it cannot
    /// use. Implementing the interface is a type saying it can be read from a string, with a format provider, and
    /// that what comes back is one of itself.
    /// </para>
    /// </summary>
    /// <param name="type"></param>
    /// <returns></returns>
    private static bool IsParsable(Type type)
    {
        return type.GetInterfaces().Any(candidate =>
            candidate.IsGenericType
            && (candidate.GetGenericTypeDefinition() == typeof(IParsable<>))
            && (candidate.GetGenericArguments()[0] == type));
    }

    /// <summary>
    /// Turn a string produced by <see cref="ToInvariantString"/> back into the requested type, using invariant culture.
    /// </summary>
    /// <remarks>
    /// The types a binding can be built for are read by the parsers above, and a string and an enum directly. Any
    /// other type has to say how one of it is read: by implementing <see cref="IParsable{TSelf}"/>, which is
    /// preferred since it takes the format provider that keeps the answer the same on every machine, or failing
    /// that by a public static Parse(string) that returns one of itself. A Parse that returns something else is
    /// not used, whatever it is called: it would hand back a value of a type nobody asked for.
    /// <para>
    /// Only the second route can surprise you, and only for a type Weequery cannot bind anyway: a Parse(string)
    /// decides for itself whether it reads the current culture, where everything else here is invariant.
    /// </para>
    /// </remarks>
    /// <param name="type">type to parse into</param>
    /// <param name="text"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException">
    /// the text is not a valid value for the type, or the type does not say how it can be read
    /// </exception>
    public static object Parse(Type type, string text)
    {
        WeequeryException.ThrowIfNull(type);
        WeequeryException.ThrowIfNull(text);

        if (type == typeof(string)) { return text; }

        if (type.IsEnum)
        {
            // Ok .TryParse will handle Enum.ToString() or (int)Enum, *BUT* if given an int, it does not validate that it is a valid value
            // Using Enum.IsDefined does not account for flag combinations, so make use of a helper func
            if ((Enum.TryParse(type, text, true, out var parsed)) && (parsed is not null) && (IsValidEnumValue(type, parsed)))
            {
                return parsed;
            }

            throw new WeequeryException($"'{text}' is not a member of enum {type.Name}");
        }

        if (Parsers.TryGetValue(type, out var parser))
        {
            return Invoke(type, text, () => parser(text));
        }

        // A type outside the supported set can still say how to read one of itself. IParsable<T> is the framework's
        // way of saying it and the one to prefer, since it takes a format provider and the invariant rule above
        // keeps holding.
        if (IsParsable(type))
        {
            var parsable = type.GetMethod(ParseMethod, BindingFlags.Public | BindingFlags.Static, [typeof(string), typeof(IFormatProvider)]);

            if (parsable is not null) { return InvokeReflected(parsable, type, text, [text, CultureInfo.InvariantCulture]); }
        }

        // Failing that, a plain Parse that hands back one of the type it was found on. Version is the reason this
        // is still here: it reads a string and does not implement the interface. What is checked is the return
        // type, which used to be taken on trust a static Parse returning something else entirely was invoked
        // happily and handed the caller a value of a type it never asked for.
        var named = type.GetMethod(ParseMethod, BindingFlags.Public | BindingFlags.Static, [typeof(string)]);

        if ((named is not null) && (type.IsAssignableFrom(named.ReturnType)))
        {
            return InvokeReflected(named, type, text, [text]);
        }

        throw new WeequeryException($"No conversion available from string to {type.Name}: a type outside the supported set has to implement IParsable<{type.Name}>, or have a public static {ParseMethod}(string) that returns one");
    }

    /// <summary>
    /// Quote and escape a string so it survives a trip back through the query parser
    /// </summary>
    /// <remarks>
    /// Escapes both the quote and the backslash, although the parser only needs the first: a backslash it does
    /// not recognise as an escape is left in the value, so <c>\w</c> would read back as itself either way. The
    /// one it cannot read back is a backslash immediately before the closing quote, where <c>'a\'</c> would look
    /// like an escaped quote and swallow it, so the backslash is escaped here rather than the case being special.
    /// A caller writing a query by hand has no such problem and can write the backslash plainly.
    /// </remarks>
    /// <param name="value"></param>
    /// <returns></returns>
    public static string Quote(string? value)
    {
        return $"'{(value ?? string.Empty).Replace("\\", "\\\\").Replace("'", "\\'")}'";
    }

    private static object Invoke(Type type, string text, Func<object> parse)
    {
        try
        {
            return parse();
        }
        catch (Exception ex) when ((ex is FormatException) || (ex is OverflowException) || (ex is ArgumentException))
        {
            throw new WeequeryException($"'{text}' is not a valid {type.Name}");
        }
    }

    private static object InvokeReflected(MethodInfo method, Type type, string text, object?[] arguments)
    {
        try
        {
            return method.Invoke(null, arguments) ?? throw new WeequeryException($"Parsing '{text}' as {type.Name} produced null");
        }
        catch (TargetInvocationException ex)
        {
            throw new WeequeryException($"'{text}' is not a valid {type.Name}: {ex.InnerException?.Message}");
        }
    }
}
