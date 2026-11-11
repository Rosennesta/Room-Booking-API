namespace RoomBooking.Api.Data;

public sealed class Reservation
{
    public int Id { get; set; }

    public int RoomId { get; set; }

    // All DateTime values represent UTC instants.
    public DateTime StartDateTime { get; set; }

    public DateTime EndDateTime { get; set; }

    public required string Title { get; set; }

    public DateTime CreateDateTime { get; set; }
}
