using System.Globalization;

namespace RoomBooking.Api.Infrastructure;

public static class ExplicitOffsetTimestamp
{
    public const string ErrorMessage =
        "Use an ISO 8601 timestamp with Z or an explicit offset, e.g. 2026-11-10T10:00Z.";

    private static readonly string[] Formats =
    [
        "yyyy-MM-dd'T'HH:mmzzz",
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"
    ];

    public static bool TryParse(string? input, out DateTimeOffset value)
    {
        if (input is not null && input.EndsWith('Z'))
        {
            input = input[..^1] + "+00:00";
        }

        return DateTimeOffset.TryParseExact(
            input, Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out value);
    }
}
