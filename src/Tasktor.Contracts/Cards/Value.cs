using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tasktor.Contracts.Cards;

/// <summary>
/// A card value: either a literal JSON value or a binding <c>{"bind": "/state/..."}</c> (a JSON Pointer
/// into the snapshot's <c>state</c>, U1 D-H2). Literals are kept as raw JSON text so records compare by value.
/// </summary>
[JsonConverter(typeof(ValueJsonConverter))]
public sealed record Value
{
    private Value(string? bind, string? raw)
    {
        Bind = bind;
        Raw = raw;
    }

    /// <summary>The JSON Pointer, when this value is a binding.</summary>
    public string? Bind { get; }

    /// <summary>The raw JSON text, when this value is a literal.</summary>
    public string? Raw { get; }

    public bool IsBinding => Bind is not null;

    public static Value Binding(string pointer)
    {
        ArgumentNullException.ThrowIfNull(pointer);
        if (pointer.Length > 0 && pointer[0] != '/')
        {
            throw new ArgumentException("A binding must be a JSON Pointer starting with '/'.", nameof(pointer));
        }

        return new Value(pointer, null);
    }

    public static Value Literal(string text) => new(null, JsonSerializer.Serialize(text, ContractsJsonContext.Default.String));

    public static Value Literal(double number) => new(null, number.ToString("R", CultureInfo.InvariantCulture));

    public static Value Literal(long number) => new(null, number.ToString(CultureInfo.InvariantCulture));

    public static Value Literal(bool flag) => new(null, flag ? "true" : "false");

    /// <summary>A literal from raw JSON text (validated).</summary>
    public static Value FromRawJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return FromElement(doc.RootElement);
    }

    internal static Value FromElement(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object
            && element.TryGetProperty("bind", out var bind)
            && bind.ValueKind == JsonValueKind.String)
        {
            var count = 0;
            foreach (var _ in element.EnumerateObject())
            {
                count++;
            }

            if (count == 1)
            {
                return Binding(bind.GetString()!);
            }
        }

        return new Value(null, element.GetRawText());
    }

    /// <summary>The literal as a JSON element (null for a binding).</summary>
    public JsonElement? AsElement()
    {
        if (Raw is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(Raw);
        return doc.RootElement.Clone();
    }

    public override string ToString() => Bind is not null ? "{\"bind\":\"" + Bind + "\"}" : Raw ?? "null";
}

public sealed class ValueJsonConverter : JsonConverter<Value>
{
    public override Value Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        return Value.FromElement(doc.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, Value value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        if (value.Bind is not null)
        {
            writer.WriteStartObject();
            writer.WriteString("bind", value.Bind);
            writer.WriteEndObject();
        }
        else
        {
            writer.WriteRawValue(value.Raw ?? "null", skipInputValidation: false);
        }
    }
}
