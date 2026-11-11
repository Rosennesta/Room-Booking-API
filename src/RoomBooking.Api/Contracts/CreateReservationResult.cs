namespace RoomBooking.Api.Contracts;

public abstract record CreateReservationResult
{
    private CreateReservationResult()
    {
    }

    public sealed record Created(ReservationResponse Reservation) : CreateReservationResult;

    public sealed record RoomNotFound : CreateReservationResult;

    public sealed record Conflict : CreateReservationResult;

    public sealed record StartInPast : CreateReservationResult;
}
