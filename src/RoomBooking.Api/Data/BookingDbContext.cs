using Microsoft.EntityFrameworkCore;

namespace RoomBooking.Api.Data;

public sealed class BookingDbContext(DbContextOptions<BookingDbContext> options)
    : DbContext(options)
{
    private const string RoomsTableName = "Rooms";
    private const string ReservationsTableName = "Reservations";

    public DbSet<Room> Rooms => Set<Room>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var room = modelBuilder.Entity<Room>();
        room.ToTable(RoomsTableName, table =>
        {
            table.HasCheckConstraint("CK_Rooms_Name",
                "length(Name) BETWEEN 1 AND 100");
            table.HasCheckConstraint("CK_Rooms_Capacity", "Capacity > 0");
        });
        room.HasKey(value => value.Id);
        room.Property(value => value.Name).IsRequired().HasMaxLength(100);
        room.Property(value => value.Capacity).IsRequired();
        room.HasData(
            new Room { Id = 1, Name = "Small Room", Capacity = 4 },
            new Room { Id = 2, Name = "Conference Room", Capacity = 10 },
            new Room { Id = 3, Name = "Auditorium", Capacity = 50 });

        var reservation = modelBuilder.Entity<Reservation>();
        reservation.ToTable(ReservationsTableName, table =>
        {
            table.HasCheckConstraint("CK_Reservations_Title",
                "length(Title) BETWEEN 1 AND 200");
            table.HasCheckConstraint("CK_Reservations_TimeRange", "StartDateTime < EndDateTime");
        });
        reservation.HasKey(value => value.Id);
        reservation.Property(value => value.Title).IsRequired().HasMaxLength(200);
        reservation.HasOne<Room>()
            .WithMany()
            .HasForeignKey(value => value.RoomId)
            .OnDelete(DeleteBehavior.Restrict);
        reservation.HasIndex(value => new { value.RoomId, value.StartDateTime });

        var converter = new UtcDateTimeConverter();
        reservation.Property(value => value.StartDateTime).HasConversion(converter);
        reservation.Property(value => value.EndDateTime).HasConversion(converter);
        reservation.Property(value => value.CreateDateTime).HasConversion(converter);
    }
}
