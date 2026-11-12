using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arrow.Common.Json;

/// <summary>
/// JSON conventions of P00 §5: snake_case names, upper-case enums (unless an enum member declares
/// <see cref="JsonStringEnumMemberNameAttribute"/>), UTC timestamps ending in <c>Z</c>, LF line endings,
/// dictionary keys kept as written, non-ASCII text (Vietnamese) not escaped.
/// Writing the text to disk (UTF-8 without BOM, atomic replace) belongs to <c>Arrow.DataAccess</c>.
/// </summary>
public static class ArrowJson
{
    /// <summary>Indented output, used for every result and config file.</summary>
    public static JsonSerializerOptions Options { get; } = Create(indented: true);

    /// <summary>Single-line output, used for JSONL files such as <c>events.jsonl</c> and <c>history.jsonl</c>.</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    /// <summary>Serializes with the conventions above. The result always uses <c>\n</c> line endings.</summary>
    public static string Serialize<T>(T value, bool indented = true) =>
        JsonSerializer.Serialize(value, indented ? Options : Compact);

    /// <summary>Deserializes a JSON document; a document that is just <c>null</c> is an error.</summary>
    public static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new JsonException("JSON document is null");

    private static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            DictionaryKeyPolicy = null,
            WriteIndented = indented,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false));
        options.Converters.Add(new UtcTimestampConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    /// <summary>Writes <see cref="DateTimeOffset"/> as UTC ISO 8601 with second precision, e.g. <c>2026-11-10T07:25:30Z</c>.</summary>
    private sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(reader.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).ToUniversalTime();

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}
