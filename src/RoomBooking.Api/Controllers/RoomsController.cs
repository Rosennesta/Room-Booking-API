using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using RoomBooking.Api.Contracts;
using RoomBooking.Api.Infrastructure;
using RoomBooking.Api.Services.Interfaces;

namespace RoomBooking.Api.Controllers;

[ApiController]
[Route("api/rooms")]
[ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable, "application/problem+json")]
[ProducesResponseType<ProblemDetails>(StatusCodes.Status500InternalServerError, "application/problem+json")]
public sealed class RoomsController(IBookingService bookingService) : ControllerBase
{
    [HttpGet]
    [EndpointSummary("List all bookable rooms.")]
    [ProducesResponseType<IReadOnlyList<RoomResponse>>(StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<RoomResponse>>> GetRooms(CancellationToken cancellationToken)
        => Ok(await bookingService.GetRoomsAsync(cancellationToken));

    [HttpGet("{roomId}/reservations")]
    [EndpointSummary("List a room's reservations, optionally overlapping a time window.")]
    [EndpointDescription("Bounds are exclusive for overlap checks: end > from and start < to. ISO 8601 timestamps require Z or an explicit offset; seconds are optional. Empty bounds are invalid.")]
    [ProducesResponseType<IReadOnlyList<ReservationResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    public async Task<ActionResult<IReadOnlyList<ReservationResponse>>> GetReservations(
        [FromRoute, Range(1, int.MaxValue)] int roomId,
        [FromQuery, ModelBinder(BinderType = typeof(ExplicitOffsetTimestampModelBinder))]
        [Description("Optional lower bound, e.g. 2026-11-10T10:00:00Z.")] DateTimeOffset? from,
        [FromQuery, ModelBinder(BinderType = typeof(ExplicitOffsetTimestampModelBinder))]
        [Description("Optional upper bound, e.g. 2026-11-10T12:00:00Z.")] DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        if (from.HasValue && to.HasValue && from.Value >= to.Value)
        {
            ModelState.AddModelError("to", "The upper bound must be later than the lower bound.");
            return ValidationProblem(ModelState);
        }

        var reservations = await bookingService.GetReservationsAsync(roomId, from, to, cancellationToken);
        return reservations is null ? MissingRoom() : Ok(reservations);
    }

    [HttpPost("{roomId}/reservations")]
    [Consumes("application/json")]
    [EndpointSummary("Create a non-overlapping reservation.")]
    [EndpointDescription("Start and end use ISO 8601 with Z or an explicit offset; seconds are optional. Start must precede end and must not be earlier than the server's current UTC time at validation; equality is allowed. Adjacent reservations are allowed. Title is trimmed and limited to 200 UTF-16 code units. Response timestamps are UTC.")]
    [ProducesResponseType<ReservationResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict, "application/problem+json")]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status415UnsupportedMediaType, "application/problem+json")]
    public async Task<ActionResult<ReservationResponse>> CreateReservation(
        [FromRoute, Range(1, int.MaxValue)] int roomId,
        [FromBody] CreateReservationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await bookingService.CreateReservationAsync(roomId, request, cancellationToken);
        if (result is CreateReservationResult.StartInPast)
        {
            ModelState.AddModelError("start", "The start must not be earlier than the current time.");
            return ValidationProblem(ModelState);
        }

        return result switch
        {
            CreateReservationResult.Created created => StatusCode(StatusCodes.Status201Created, created.Reservation),
            CreateReservationResult.RoomNotFound => MissingRoom(),
            CreateReservationResult.Conflict => ApiProblems.Result(ApiProblems.Create(HttpContext,
                StatusCodes.Status409Conflict, "reservation_conflict",
                "The room already has a reservation overlapping the requested time range.")),
            _ => throw new InvalidOperationException("Unknown reservation creation result.")
        };
    }

    private ObjectResult MissingRoom() => ApiProblems.Result(ApiProblems.Create(HttpContext,
        StatusCodes.Status404NotFound, "room_not_found", "The requested room does not exist."));
}
