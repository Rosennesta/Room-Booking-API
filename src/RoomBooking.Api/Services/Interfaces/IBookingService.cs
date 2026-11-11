using RoomBooking.Api.Contracts;

namespace RoomBooking.Api.Services.Interfaces;

public interface IBookingService
{
    // Database failures and cancellation end the unit of work; dispose its scope instead of reusing the context.
    Task<IReadOnlyList<RoomResponse>> GetRoomsAsync(
        CancellationToken cancellationToken);

    // Null means the room does not exist; an empty list means it has no matching reservations.
    Task<IReadOnlyList<ReservationResponse>?> GetReservationsAsync(
        int roomId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken);

    Task<CreateReservationResult> CreateReservationAsync(
        int roomId,
        CreateReservationRequest request,
        CancellationToken cancellationToken);
}
