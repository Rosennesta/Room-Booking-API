using System.Data;
using Microsoft.EntityFrameworkCore;
using RoomBooking.Api.Contracts;
using RoomBooking.Api.Services.Impl;
using Xunit;

namespace RoomBooking.Api.Tests.Integration;

public sealed class BookingServiceTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Now = Start.AddDays(-1).AddTicks(1234567);

    [Fact]
    public async Task Rooms_are_projected_in_id_order_without_tracking()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new BookingService(context, TimeProvider.System);

        var rooms = await service.GetRoomsAsync(CancellationToken.None);

        Assert.Equal(new[]
        {
            new RoomResponse(1, "Small Room", 4),
            new RoomResponse(2, "Conference Room", 10),
            new RoomResponse(3, "Auditorium", 50)
        }, rooms);
        Assert.Empty(context.ChangeTracker.Entries());

        await context.Rooms.ExecuteDeleteAsync();
        Assert.Empty(await service.GetRoomsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Create_normalizes_offsets_trims_title_and_sets_creation_time_once()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var clock = new FixedTimeProvider(Now);
        var service = new BookingService(context, clock);
        var start = Start.AddTicks(7654321).ToOffset(TimeSpan.FromHours(8));
        var end = start.AddHours(1).ToOffset(TimeSpan.FromHours(-5));

        var result = await service.CreateReservationAsync(
            1, new CreateReservationRequest(start, end, " \t Planning \r\n"), CancellationToken.None);

        var response = Assert.IsType<CreateReservationResult.Created>(result).Reservation;
        Assert.True(response.Id > 0);
        Assert.Equal(1, response.RoomId);
        Assert.Equal(start, response.Start);
        Assert.Equal(end, response.End);
        Assert.Equal(TimeSpan.Zero, response.Start.Offset);
        Assert.Equal(TimeSpan.Zero, response.End.Offset);
        Assert.Equal(TimeSpan.Zero, response.CreatedAt.Offset);
        Assert.Equal("Planning", response.Title);
        Assert.Equal(Now, response.CreatedAt);
        Assert.Equal(1, clock.ReadCount);
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

        await using var reopened = database.CreateContext();
        var saved = await reopened.Reservations.SingleAsync();
        Assert.Equal(start.UtcDateTime, saved.StartDateTime);
        Assert.Equal(end.UtcDateTime, saved.EndDateTime);
        Assert.Equal(Now.UtcDateTime, saved.CreateDateTime);

        var queryService = new BookingService(reopened, clock);
        Assert.Equal(response, Assert.Single(
            (await queryService.GetReservationsAsync(1, null, null, CancellationToken.None))!));
    }

    [Theory]
    [InlineData(-30, 30)]
    [InlineData(30, 90)]
    [InlineData(-60, 120)]
    [InlineData(15, 45)]
    [InlineData(0, 60)]
    [InlineData(0, 30)]
    [InlineData(30, 60)]
    public async Task Overlapping_reservations_are_rejected(int startMinutes, int endMinutes)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new BookingService(context, new FixedTimeProvider(Now));
        await CreateAsync(service, 1, Start, Start.AddHours(1));

        var result = await service.CreateReservationAsync(1, new CreateReservationRequest(
            Start.AddMinutes(startMinutes).ToOffset(TimeSpan.FromHours(8)),
            Start.AddMinutes(endMinutes).ToOffset(TimeSpan.FromHours(-5)), "Conflict"),
            CancellationToken.None);

        Assert.IsType<CreateReservationResult.Conflict>(result);
        Assert.Equal(1, await context.Reservations.CountAsync());
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

        await CreateAsync(service, 1, Start.AddHours(1), Start.AddHours(2));
        Assert.Equal(2, await context.Reservations.CountAsync());
    }

    [Theory]
    [InlineData(1, -60, 0)]
    [InlineData(1, 60, 120)]
    [InlineData(1, 120, 180)]
    [InlineData(2, 0, 60)]
    public async Task Adjacent_disjoint_and_other_room_reservations_are_allowed(
        int roomId, int startMinutes, int endMinutes)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new BookingService(context, new FixedTimeProvider(Now));
        await CreateAsync(service, 1, Start, Start.AddHours(1));
        await CreateAsync(service, roomId, Start.AddMinutes(startMinutes), Start.AddMinutes(endMinutes));

        Assert.Equal(2, await context.Reservations.CountAsync());
    }

    [Fact]
    public async Task Missing_room_and_empty_results_are_distinct_and_leave_no_transaction()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new BookingService(context, new FixedTimeProvider(Now));

        Assert.Null(await service.GetReservationsAsync(999, null, null, CancellationToken.None));
        Assert.Empty((await service.GetReservationsAsync(1, null, null, CancellationToken.None))!);
        Assert.IsType<CreateReservationResult.RoomNotFound>(await service.CreateReservationAsync(
            999, Request(), CancellationToken.None));
        Assert.Empty(await context.Reservations.ToListAsync());
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);

        await CreateAsync(service, 1, Start, Start.AddHours(1));
    }

    [Theory]
    [InlineData(null, null, new[] { -120, -30, 60, 120 })]
    [InlineData(0, null, new[] { -30, 60, 120 })]
    [InlineData(null, 120, new[] { -120, -30, 60 })]
    [InlineData(0, 120, new[] { -30, 60 })]
    [InlineData(-30, 120, new[] { -30, 60 })]
    [InlineData(0, 60, new[] { -30 })]
    [InlineData(180, 240, new int[0])]
    public async Task Queries_apply_optional_overlap_bounds_in_start_order(
        int? fromMinutes, int? toMinutes, int[] expectedMinutes)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new BookingService(context, new FixedTimeProvider(Now));
        // Deliberately insert out of chronological order, including a different room.
        await CreateAsync(service, 1, Start.AddHours(2), Start.AddHours(3));
        await CreateAsync(service, 1, Start.AddMinutes(-30), Start.AddMinutes(30));
        await CreateAsync(service, 1, Start.AddHours(-2), Start.AddMinutes(-30));
        await CreateAsync(service, 1, Start.AddHours(1), Start.AddHours(2));
        await CreateAsync(service, 2, Start, Start.AddHours(1));
        context.ChangeTracker.Clear();

        DateTimeOffset? from = fromMinutes.HasValue
            ? Start.AddMinutes(fromMinutes.Value).ToOffset(TimeSpan.FromHours(8)) : null;
        DateTimeOffset? to = toMinutes.HasValue
            ? Start.AddMinutes(toMinutes.Value).ToOffset(TimeSpan.FromHours(-5)) : null;
        var result = await service.GetReservationsAsync(1, from, to, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(expectedMinutes.Select(minutes => Start.AddMinutes(minutes)), result.Select(item => item.Start));
        Assert.All(result, item => Assert.Equal(1, item.RoomId));
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_room_ids_are_rejected_before_database_access(int roomId)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        var service = new BookingService(context, TimeProvider.System);

        var queryError = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.GetReservationsAsync(roomId, null, null, CancellationToken.None));
        var createError = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            service.CreateReservationAsync(roomId, Request(), CancellationToken.None));

        Assert.Equal("roomId", queryError.ParamName);
        Assert.Equal("roomId", createError.ParamName);
        Assert.False(File.Exists(database.FilePath));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-60)]
    public async Task Invalid_ranges_are_rejected_before_database_access(int endMinutes)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        var service = new BookingService(context, TimeProvider.System);
        var end = Start.AddMinutes(endMinutes).ToOffset(TimeSpan.FromHours(8));

        var queryError = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.GetReservationsAsync(1, Start, end, CancellationToken.None));
        var createError = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateReservationAsync(1, new CreateReservationRequest(Start, end, "Meeting"),
                CancellationToken.None));

        Assert.Equal("to", queryError.ParamName);
        Assert.Equal("request", createError.ParamName);
        Assert.False(File.Exists(database.FilePath));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n\u3000")]
    public async Task Invalid_titles_are_rejected_before_database_access(string? title)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        var service = new BookingService(context, TimeProvider.System);
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateReservationAsync(1, Request() with { Title = title! }, CancellationToken.None));

        Assert.Equal("request", exception.ParamName);
        Assert.False(File.Exists(database.FilePath));
    }

    [Fact]
    public async Task Null_request_is_rejected_before_database_access()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        var service = new BookingService(context, TimeProvider.System);
        var exception = await Assert.ThrowsAsync<ArgumentNullException>(() =>
            service.CreateReservationAsync(1, null!, CancellationToken.None));
        Assert.Equal("request", exception.ParamName);
        Assert.False(File.Exists(database.FilePath));
    }

    [Fact]
    public async Task Title_limit_is_applied_after_trimming_in_utf16_code_units()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new BookingService(context, new FixedTimeProvider(Now));
        var title = string.Concat(Enumerable.Repeat("\U0001F600", 100));
        var result = await service.CreateReservationAsync(1,
            Request() with { Title = $" \t{title}\r\n" }, CancellationToken.None);
        Assert.Equal(title, Assert.IsType<CreateReservationResult.Created>(result).Reservation.Title);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateReservationAsync(1, Request() with { Title = title + "x" }, CancellationToken.None));
        Assert.Equal("request", exception.ParamName);
        Assert.Equal(1, await context.Reservations.CountAsync());
    }

    [Fact]
    public async Task Long_reservations_are_allowed()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new BookingService(context, new FixedTimeProvider(Now));

        await CreateAsync(service, 1, Start, Start.AddYears(1));
    }

    [Theory]
    [InlineData(-5)]
    [InlineData(0)]
    [InlineData(8)]
    public async Task Past_starts_are_rejected_before_database_access(int offsetHours)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        var clock = new FixedTimeProvider(Now);
        var service = new BookingService(context, clock);
        var start = Now.AddTicks(-1).ToOffset(TimeSpan.FromHours(offsetHours));

        var result = await service.CreateReservationAsync(
            1, new CreateReservationRequest(start, Now.AddHours(1), "Meeting"), CancellationToken.None);

        Assert.IsType<CreateReservationResult.StartInPast>(result);
        Assert.Equal(1, clock.ReadCount);
        Assert.False(File.Exists(database.FilePath));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Starts_at_or_after_now_are_allowed_and_remain_queryable_in_the_past(int ticks)
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        await context.Database.MigrateAsync();
        var service = new BookingService(context, new FixedTimeProvider(Now));
        var start = Now.AddTicks(ticks).ToOffset(TimeSpan.FromHours(8));
        var end = start.AddHours(1).ToOffset(TimeSpan.FromHours(-5));

        await CreateAsync(service, 1, start, end);

        var laterService = new BookingService(context, new FixedTimeProvider(end.AddDays(1)));
        var reservation = Assert.Single(
            (await laterService.GetReservationsAsync(1, null, null, CancellationToken.None))!);
        Assert.Equal(start, reservation.Start);
    }

    [Fact]
    public async Task Pre_cancelled_calls_propagate_cancellation_without_touching_database()
    {
        using var database = new TestDatabase();
        await using var context = database.CreateContext();
        var service = new BookingService(context, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetRoomsAsync(cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.GetReservationsAsync(1, null, null, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.CreateReservationAsync(1, Request(), cancellation.Token));
        Assert.False(File.Exists(database.FilePath));
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Empty(context.ChangeTracker.Entries());
    }

    private static CreateReservationRequest Request() => new(Start, Start.AddHours(1), "Meeting");

    private static async Task CreateAsync(
        BookingService service, int roomId, DateTimeOffset start, DateTimeOffset end)
    {
        Assert.IsType<CreateReservationResult.Created>(await service.CreateReservationAsync(
            roomId, new CreateReservationRequest(start, end, "Meeting"), CancellationToken.None));
    }

}
