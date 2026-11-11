namespace RoomBooking.Api.Contracts;

public sealed record ReservationResponse(
    int Id,
    int RoomId,
    DateTimeOffset Start,
    DateTimeOffset End,
    string Title,
    DateTimeOffset CreatedAt);
