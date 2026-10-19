using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TemplateName.Modules.Notifications.Application.Preferences.GetQuietHours;

/// <summary>
/// Reads and writes a time of day as the 24-hour <c>"HH:mm"</c> string (<c>"07:00"</c>, <c>"23:30"</c>) and nothing else: no seconds,
/// no single-digit hour, no <c>"24:00"</c>, no number. Any other token or text is a <see cref="JsonException"/>, which the host
/// answers with 400 <c>request.malformed</c>. It is applied to the quiet-hours properties, not registered host-wide, because the
/// default <see cref="TimeOnly"/> format is <c>"HH:mm:ss"</c> and other modules may rely on it.
/// </summary>
internal sealed class HourMinuteJsonConverter : JsonConverter<TimeOnly>
{
    private const string Format = "HH:mm";

    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String
            && TimeOnly.TryParseExact(reader.GetString(), Format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            return time;
        }

        throw new JsonException("A time of day must be a string like \"07:00\" or \"23:30\" (24-hour HH:mm).");
    }

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString(Format, CultureInfo.InvariantCulture));
}
