using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RoomBooking.Api.Infrastructure;

public sealed class ExplicitOffsetTimestampJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String ||
            !ExplicitOffsetTimestamp.TryParse(reader.GetString(), out var value))
        {
            throw new JsonException(ExplicitOffsetTimestamp.ErrorMessage);
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
}
