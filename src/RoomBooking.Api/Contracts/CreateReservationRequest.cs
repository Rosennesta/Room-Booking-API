using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace RoomBooking.Api.Contracts;

public sealed record CreateReservationRequest(
    [property: JsonRequired] DateTimeOffset Start,
    [property: JsonRequired] DateTimeOffset End,
    [Required] [property: JsonRequired] string Title) : IValidatableObject
{
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Start >= End)
        {
            yield return new ValidationResult("The end must be later than the start.", ["end"]);
        }

        if (!string.IsNullOrWhiteSpace(Title) && Title.Trim().Length > 200)
        {
            yield return new ValidationResult(
                "The trimmed title must not exceed 200 UTF-16 code units.", ["title"]);
        }
    }
}
