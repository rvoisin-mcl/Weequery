using System.Text.Json;
using System.Text.Json.Serialization;

namespace Weequery;

/// <summary>
/// Reads and writes a <see cref="ConditionValue{T}"/> in its compact form: an operand that is a value travels as
/// the value alone, and only one naming a bound property carries a source with it.
/// </summary>
/// <remarks>
/// <para>
/// Nearly every operand ever sent is a value, so paying two JSON properties to say
/// <c>{"Source":0,"Value":"8000"}</c> where <c>"8000"</c> says the same thing costs something on every condition
/// to describe the case nobody wrote. <see cref="ValueSource.Raw"/> is both the default and the common case, so
/// it is what the absence of a source means:
/// </para>
/// <code>
/// "Values": [ "8000", { "Source": 1, "Value": "Ceiling" } ]
/// </code>
/// <para>
/// Nothing is guessed from the text. A value and a key are told apart by the shape they arrive in, a string
/// against an object, which no value can be mistaken for whatever it spells. That is the property the feature
/// rests on, see <see cref="PackedCondition.Values"/>, and it holds here as it did when every operand carried a
/// source.
/// </para>
/// <para>
/// It also means a payload whose values are plain text reads as values, which is the shape they travelled in
/// before an operand could name a property at all.
/// </para>
/// </remarks>
internal sealed class ConditionValueConverter : JsonConverterFactory
{
    /// <summary>
    /// Any <see cref="ConditionValue{T}"/>, whatever it holds. Only the string one is ever serialized by
    /// <see cref="PackedCondition"/>, but the compact form is written the same way for any operand whose value is
    /// not itself a JSON object.
    /// </summary>
    /// <param name="typeToConvert"></param>
    /// <returns></returns>
    public override bool CanConvert(Type typeToConvert)
    {
        return typeToConvert.IsGenericType && (typeToConvert.GetGenericTypeDefinition() == typeof(ConditionValue<>));
    }

    /// <summary>
    /// One converter per closed type, which System.Text.Json caches
    /// </summary>
    /// <param name="typeToConvert"></param>
    /// <param name="options"></param>
    /// <returns></returns>
    /// <exception cref="WeequeryException"></exception>
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converter = typeof(Converter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]);

        return (JsonConverter?)Activator.CreateInstance(converter)
            ?? throw new WeequeryException($"(Should be impossible) Could not create a converter for {typeToConvert.Name}");
    }

    private sealed class Converter<T> : JsonConverter<ConditionValue<T>>
    {
        /// <summary>
        /// The property names, honouring whatever naming policy the caller serializes with
        /// </summary>
        private static string Name(JsonSerializerOptions options, string name)
        {
            return options.PropertyNamingPolicy?.ConvertName(name) ?? name;
        }

        public override ConditionValue<T>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            // The compact form: the value on its own, which is what a value operand is written as
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                return new ConditionValue<T>(ValueSource.Raw, JsonSerializer.Deserialize<T>(ref reader, options)!);
            }

            var source = ValueSource.Raw;
            T? value = default;
            var read = false;

            while (reader.Read() && (reader.TokenType != JsonTokenType.EndObject))
            {
                if (reader.TokenType != JsonTokenType.PropertyName) { continue; }

                var property = reader.GetString();
                reader.Read();

                // Matched without regard to case, so a payload written under any naming policy reads back
                if (string.Equals(property, nameof(ConditionValue<T>.Source), StringComparison.OrdinalIgnoreCase))
                {
                    source = JsonSerializer.Deserialize<ValueSource>(ref reader, options);
                }
                else if (string.Equals(property, nameof(ConditionValue<T>.Value), StringComparison.OrdinalIgnoreCase))
                {
                    value = JsonSerializer.Deserialize<T>(ref reader, options);
                    read = true;
                }
                else
                {
                    // Something a later version added, which this one has no use for
                    reader.Skip();
                }
            }

            if (!read)
            {
                throw new JsonException($"An operand written as an object needs a {nameof(ConditionValue<T>.Value)}");
            }

            return new ConditionValue<T>(source, value!);
        }

        public override void Write(Utf8JsonWriter writer, ConditionValue<T> value, JsonSerializerOptions options)
        {
            WeequeryException.ThrowIfNull(value);

            // A value is the default and the common case, so it says nothing about where it came from
            if (!value.NamesProperty)
            {
                JsonSerializer.Serialize(writer, value.Value, options);

                return;
            }

            writer.WriteStartObject();

            writer.WritePropertyName(Name(options, nameof(ConditionValue<T>.Source)));
            JsonSerializer.Serialize(writer, value.Source, options);

            writer.WritePropertyName(Name(options, nameof(ConditionValue<T>.Value)));
            JsonSerializer.Serialize(writer, value.Value, options);

            writer.WriteEndObject();
        }
    }
}
