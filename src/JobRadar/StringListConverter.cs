using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace JobRadar;

/// <summary>
/// Lenient list parsing for LLM output: accepts a JSON array of strings, a single bare
/// string (wrapped into a one-item list), scalars (coerced to text), or null. Small local
/// models often emit a bare string where the schema asks for an array — without this the
/// whole object fails to deserialize and the UI falls back to showing raw JSON.
/// </summary>
public sealed class StringListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        var list = new List<string>();
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                break;
            case JsonTokenType.StartArray:
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    Add(ref reader, list);
                break;
            default:
                Add(ref reader, list);
                break;
        }
        return list;
    }

    private static void Add(ref Utf8JsonReader reader, List<string> list)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var s = reader.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s!.Trim());
                break;
            case JsonTokenType.Number:
                list.Add(reader.GetDouble().ToString(CultureInfo.InvariantCulture));
                break;
            case JsonTokenType.True: list.Add("true"); break;
            case JsonTokenType.False: list.Add("false"); break;
            case JsonTokenType.StartObject:
            case JsonTokenType.StartArray:
                reader.Skip(); // ignore unexpected nested structures
                break;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, options);
}
