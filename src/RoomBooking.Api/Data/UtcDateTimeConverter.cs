using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace RoomBooking.Api.Data;

public sealed class UtcDateTimeConverter() : ValueConverter<DateTime, long>(
    value => ToTicks(value),
    ticks => new DateTime(ticks, DateTimeKind.Utc))
{
    private static long ToTicks(DateTime value)
    {
        if (value.Kind != DateTimeKind.Utc)
        {
            throw new ArgumentException("Persisted date/time values must have DateTimeKind.Utc.", nameof(value));
        }

        return value.Ticks;
    }
}
