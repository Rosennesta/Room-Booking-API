using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RoomBooking.Api.Data;
using Xunit;

namespace RoomBooking.Api.Tests.Integration;

public sealed class BookingDbContextTests
{
    private static readonly DateTime Start = new(2026, 11, 10, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Migration_seeds_rooms_and_reapplying_preserves_existing_data()
    {
        using var database = new TestDatabase();
        int reservationId;
        await using (var context = database.CreateContext())
        {
            await context.Database.MigrateAsync();
            var rooms = await context.Rooms.OrderBy(room => room.Id).ToListAsync();
            Assert.Collection(rooms,
                room => Assert.Equal((1, "Small Room", 4), (room.Id, room.Name, room.Capacity)),
                room => Assert.Equal((2, "Conference Room", 10), (room.Id, room.Name, room.Capacity)),
                room => Assert.Equal((3, "Auditorium", 50), (room.Id, room.Name, room.Capacity)));

            var reservation = NewReservation(Start);
            context.Reservations.Add(reservation);
            rooms[0].Name = "Renamed room";
            await context.SaveChangesAsync();
            reservationId = reservation.Id;
        }

        await using var reopened = database.CreateContext();
        await reopened.Database.MigrateAsync();
        Assert.Equal(3, await reopened.Rooms.CountAsync());
        Assert.Equal("Renamed room", (await reopened.Rooms.SingleAsync(room => room.Id == 1)).Name);
        Assert.Equal(reservationId, (await reopened.Reservations.SingleAsync()).Id);
        Assert.Equal(reopened.Database.GetMigrations(), await reopened.Database.GetAppliedMigrationsAsync());
    }

    [Fact]
    public async Task Migration_can_be_rolled_back_and_reapplied()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        await context.GetService<IMigrator>().MigrateAsync(Migration.InitialDatabase);
        Assert.Empty(await context.Database.GetAppliedMigrationsAsync());

        await context.Database.MigrateAsync();
        Assert.Equal(3, await context.Rooms.CountAsync());
        Assert.Empty(await context.Reservations.ToListAsync());
    }

    [Fact]
    public async Task Dates_round_trip_as_utc_with_tick_precision_and_integer_storage()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var reservation = NewReservation(Start.AddTicks(1234567));
        context.Reservations.Add(reservation);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var saved = await context.Reservations.SingleAsync();
        Assert.Equal(reservation.StartDateTime, saved.StartDateTime);
        Assert.Equal(reservation.EndDateTime, saved.EndDateTime);
        Assert.Equal(reservation.CreateDateTime, saved.CreateDateTime);
        Assert.All(new[]
        {
            saved.StartDateTime, saved.EndDateTime, saved.CreateDateTime
        }, date => Assert.Equal(DateTimeKind.Utc, date.Kind));

        Assert.Equal(reservation.StartDateTime.Ticks,
            await context.Database.SqlQueryRaw<long>(
                "SELECT StartDateTime AS Value FROM Reservations").SingleAsync());
        Assert.Equal("integer",
            await context.Database.SqlQueryRaw<string>(
                "SELECT typeof(StartDateTime) AS Value FROM Reservations").SingleAsync());
    }

    [Fact]
    public async Task Overlap_filter_and_ordering_execute_in_sql_with_converted_parameters()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        context.Reservations.AddRange(
            NewReservation(Start.AddHours(2)),
            NewReservation(Start),
            NewReservation(Start.AddHours(1)),
            NewReservation(Start, roomId: 2));
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        var from = Start;
        var to = Start.AddHours(2);
        var query = context.Reservations.AsNoTracking()
            .Where(item => item.RoomId == 1 && item.StartDateTime < to && item.EndDateTime > from)
            .OrderBy(item => item.StartDateTime).ThenBy(item => item.Id);

        Assert.Contains("WHERE", query.ToQueryString());
        Assert.Contains("ORDER BY", query.ToQueryString());
        Assert.Equal(new[] { Start, Start.AddHours(1) },
            await query.Select(item => item.StartDateTime).ToArrayAsync());
        Assert.True(await query.AnyAsync());

        var adjacentFrom = Start.AddHours(3);
        var adjacentTo = adjacentFrom.AddHours(1);
        Assert.False(await context.Reservations.AnyAsync(item =>
            item.RoomId == 1 && item.StartDateTime < adjacentTo && item.EndDateTime > adjacentFrom));

        var indexSql = await context.Database.SqlQueryRaw<string>(
            """
            SELECT sql AS Value FROM sqlite_master
            WHERE type = 'index' AND name = 'IX_Reservations_RoomId_StartDateTime'
            """).SingleAsync();
        Assert.Contains("(\"RoomId\", \"StartDateTime\")", indexSql);
    }

    [Theory]
    [InlineData("StartDateTime", DateTimeKind.Unspecified)]
    [InlineData("StartDateTime", DateTimeKind.Local)]
    [InlineData("EndDateTime", DateTimeKind.Unspecified)]
    [InlineData("EndDateTime", DateTimeKind.Local)]
    [InlineData("CreateDateTime", DateTimeKind.Unspecified)]
    [InlineData("CreateDateTime", DateTimeKind.Local)]
    public async Task Non_utc_entity_values_are_rejected(string property, DateTimeKind kind)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var reservation = NewReservation(Start);
        switch (property)
        {
            case nameof(Reservation.StartDateTime):
                reservation.StartDateTime = DateTime.SpecifyKind(reservation.StartDateTime, kind);
                break;
            case nameof(Reservation.EndDateTime):
                reservation.EndDateTime = DateTime.SpecifyKind(reservation.EndDateTime, kind);
                break;
            case nameof(Reservation.CreateDateTime):
                reservation.CreateDateTime = DateTime.SpecifyKind(reservation.CreateDateTime, kind);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(property));
        }
        context.Reservations.Add(reservation);

        var exception = await Record.ExceptionAsync(() => context.SaveChangesAsync());
        Assert.NotNull(exception);
        Assert.IsType<ArgumentException>(exception.GetBaseException());
        Assert.Contains("DateTimeKind.Utc", exception.GetBaseException().Message);
        Assert.Equal(0, await context.Reservations.CountAsync());
    }

    [Theory]
    [InlineData("", 4)]
    [InlineData(null, 4)]
    [InlineData("Valid room", 0)]
    [InlineData("Valid room", -1)]
    public async Task Database_rejects_invalid_rooms(string? name, int capacity)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Rooms (Name, Capacity) VALUES ({name}, {capacity})"));

        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Database_rejects_empty_or_null_titles(string? title)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO Reservations (RoomId, StartDateTime, EndDateTime, Title, CreateDateTime)
                VALUES (1, 0, 1, {title}, 0)
                """));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task Database_enforces_text_length_limits()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var name = new string('n', 100);
        var title = new string('t', 200);
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO Rooms (Name, Capacity) VALUES ({name}, 1)");
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO Reservations (RoomId, StartDateTime, EndDateTime, Title, CreateDateTime)
            VALUES (1, 0, 1, {title}, 0)
            """);

        var invalidName = name + "n";
        var invalidTitle = title + "t";
        var roomError = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO Rooms (Name, Capacity) VALUES ({invalidName}, 1)"));
        var reservationError = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE Reservations SET Title = {invalidTitle}"));
        Assert.Equal(19, roomError.SqliteErrorCode);
        Assert.Equal(19, reservationError.SqliteErrorCode);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public async Task Database_rejects_invalid_time_ranges(long start, long end)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();

        var exception = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO Reservations (RoomId, StartDateTime, EndDateTime, Title, CreateDateTime)
                VALUES (1, {start}, {end}, 'Meeting', 0)
                """));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task Foreign_key_rejects_missing_rooms_and_prevents_deleting_reserved_rooms()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var reservation = NewReservation(Start, roomId: 999);
        context.Reservations.Add(reservation);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        Assert.Equal(787, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);

        context.ChangeTracker.Clear();
        context.Reservations.Add(NewReservation(Start));
        await context.SaveChangesAsync();
        var deleteError = await Assert.ThrowsAsync<SqliteException>(() =>
            context.Database.ExecuteSqlRawAsync("DELETE FROM Rooms WHERE Id = 1"));
        Assert.Equal(19, deleteError.SqliteErrorCode);
        Assert.Equal(1, await context.Reservations.CountAsync());
    }

    private static Reservation NewReservation(DateTime start, int roomId = 1) => new()
    {
        RoomId = roomId,
        StartDateTime = start,
        EndDateTime = start.AddHours(1),
        Title = "Meeting",
        CreateDateTime = Start.AddDays(-1).AddTicks(123)
    };
}
