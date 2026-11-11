using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RoomBooking.Api.Contracts;
using RoomBooking.Api.Data;
using RoomBooking.Api.Services.Impl;
using Xunit;

namespace RoomBooking.Api.Tests.Integration;

public sealed class BookingServiceTransactionTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);
    private readonly TimeProvider _clock = new FixedTimeProvider(Start.AddDays(-1));
    private static CreateReservationRequest Request() => new(Start, Start.AddHours(1), "Meeting");

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(1, 60, false)]
    [InlineData(2, 0, false)]
    public async Task Competing_creates_observe_committed_state(
        int secondRoomId, int startMinutes, bool expectConflict)
    {
        using var database = new TestDatabase();
        await using (var setup = database.CreateContext())
        {
            await setup.Database.MigrateAsync();
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var readyCount = 0;

        async Task<CreateReservationResult> RunAsync(int roomId, CreateReservationRequest request)
        {
            await using var context = database.CreateContext();
            if (Interlocked.Increment(ref readyCount) == 2)
            {
                ready.TrySetResult();
            }
            await release.Task.WaitAsync(TimeSpan.FromSeconds(15));
            return await new BookingService(context, _clock)
                .CreateReservationAsync(roomId, request, CancellationToken.None);
        }

        var first = Task.Run(() => RunAsync(1, Request()));
        var second = Task.Run(() => RunAsync(secondRoomId, new CreateReservationRequest(
            Start.AddMinutes(startMinutes), Start.AddMinutes(startMinutes + 60), "Second")));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        finally
        {
            release.TrySetResult();
        }

        var results = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(expectConflict ? 1 : 2, results.OfType<CreateReservationResult.Created>().Count());
        Assert.Equal(expectConflict ? 1 : 0, results.OfType<CreateReservationResult.Conflict>().Count());
        await using var verification = database.CreateContext();
        Assert.Equal(expectConflict ? 1 : 2, await verification.Reservations.CountAsync());
    }

    [Fact]
    public async Task Write_lock_is_held_before_first_room_query()
    {
        using var database = new TestDatabase();
        await using (var setup = database.CreateContext())
        {
            await setup.Database.MigrateAsync();
        }

        var gate = new FirstReadGate();
        await using var context = database.CreateContext(gate);
        var service = new BookingService(context, _clock);
        var creating = Task.Run(() => service.CreateReservationAsync(1, Request(), CancellationToken.None));

        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(IsolationLevel.Serializable, gate.IsolationLevel);
            await using var contender = new SqliteConnection(database.ConnectionString) { DefaultTimeout = 1 };
            await contender.OpenAsync();
            var error = Assert.Throws<SqliteException>(() =>
            {
                using var transaction = contender.BeginTransaction(deferred: false);
            });
            Assert.Equal(5, error.SqliteErrorCode);
        }
        finally
        {
            gate.Release.TrySetResult();
            await creating.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.IsType<CreateReservationResult.Created>(await creating);
    }

    [Fact]
    public async Task Waiting_create_detects_reservation_committed_by_another_connection()
    {
        using var database = new TestDatabase();
        await using var first = database.CreateContext();
        await first.Database.MigrateAsync();
        await first.Database.OpenConnectionAsync();
        var connection = (SqliteConnection)first.Database.GetDbConnection();
        await using var transaction = connection.BeginTransaction(deferred: false);
        await using var enlisted = await first.Database.UseTransactionAsync(transaction);
        first.Reservations.Add(new Reservation
        {
            RoomId = 1,
            StartDateTime = Start.UtcDateTime,
            EndDateTime = Start.AddHours(1).UtcDateTime,
            Title = "First",
            CreateDateTime = Start.UtcDateTime
        });
        await first.SaveChangesAsync();

        var opening = new ConnectionOpenedSignal();
        await using var second = database.CreateContext(opening);
        var waiting = Task.Run(() => new BookingService(second, _clock)
            .CreateReservationAsync(1, Request(), CancellationToken.None));
        try
        {
            await opening.Opened.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await transaction.CommitAsync();
        }
        finally
        {
            await transaction.DisposeAsync();
            await waiting.WaitAsync(TimeSpan.FromSeconds(15));
        }

        Assert.IsType<CreateReservationResult.Conflict>(await waiting);
        Assert.Equal(1, await second.Reservations.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Busy_timeout_releases_its_connection_reference_and_a_new_scope_can_book(bool alreadyOpen)
    {
        using var database = new TestDatabase();
        await using (var setup = database.CreateContext())
        {
            await setup.Database.MigrateAsync();
        }

        await using var holder = new SqliteConnection(database.ConnectionString);
        await holder.OpenAsync();
        await using var transaction = holder.BeginTransaction(deferred: false);

        await using (var context = database.CreateContext())
        {
            ((SqliteConnection)context.Database.GetDbConnection()).DefaultTimeout = 1;
            context.Database.SetCommandTimeout(1);
            if (alreadyOpen)
            {
                await context.Database.OpenConnectionAsync();
            }
            var expectedState = alreadyOpen ? ConnectionState.Open : ConnectionState.Closed;
            var service = new BookingService(context, _clock);
            var error = await Assert.ThrowsAsync<SqliteException>(() =>
                service.CreateReservationAsync(1, Request(), CancellationToken.None));

            Assert.Equal(5, error.SqliteErrorCode);
            Assert.Equal(expectedState, context.Database.GetDbConnection().State);
            Assert.Null(context.Database.CurrentTransaction);
            if (alreadyOpen)
            {
                await context.Database.CloseConnectionAsync();
                Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
            }
        }
        await transaction.RollbackAsync();

        await using var next = database.CreateContext();
        Assert.Empty(await next.Reservations.ToListAsync());
        Assert.IsType<CreateReservationResult.Created>(
            await new BookingService(next, _clock).CreateReservationAsync(1, Request(), CancellationToken.None));
        Assert.Equal(1, await next.Reservations.CountAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_or_cancellation_after_insert_rolls_back_and_releases_resources(bool cancel)
    {
        using var database = new TestDatabase();
        using var cancellation = new CancellationTokenSource();
        var failure = new FailAfterSave(cancel ? cancellation : null);
        await using (var context = database.CreateContext(failure))
        {
            await context.Database.MigrateAsync();
            var service = new BookingService(context, _clock);

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    service.CreateReservationAsync(1, Request(), cancellation.Token));
            }
            else
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    service.CreateReservationAsync(1, Request(), CancellationToken.None));
                Assert.Same(failure.Error, error);
            }

            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }

        await using var verification = database.CreateContext();
        Assert.Empty(await verification.Reservations.ToListAsync());

        Assert.IsType<CreateReservationResult.Created>(
            await new BookingService(verification, _clock).CreateReservationAsync(
                1, Request(), CancellationToken.None));
        Assert.Equal(1, await verification.Reservations.CountAsync());
    }

    [Fact]
    public async Task Unexpected_database_errors_are_not_reported_as_business_conflicts()
    {
        using var database = new TestDatabase();
        await using (var context = database.CreateContext())
        {
            var service = new BookingService(context, _clock);
            var error = await Assert.ThrowsAsync<SqliteException>(() =>
                service.CreateReservationAsync(1, Request(), CancellationToken.None));

            Assert.Equal(1, error.SqliteErrorCode);
            Assert.Null(context.Database.CurrentTransaction);
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }

        await using var next = database.CreateContext();
        await next.Database.MigrateAsync();
        Assert.IsType<CreateReservationResult.Created>(
            await new BookingService(next, _clock).CreateReservationAsync(1, Request(), CancellationToken.None));
    }

    private sealed class FirstReadGate : DbCommandInterceptor
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IsolationLevel? IsolationLevel { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (!Entered.Task.IsCompleted)
            {
                IsolationLevel = command.Transaction?.IsolationLevel;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private sealed class ConnectionOpenedSignal : DbConnectionInterceptor
    {
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            Opened.TrySetResult();
            return Task.CompletedTask;
        }
    }

    private sealed class FailAfterSave(CancellationTokenSource? cancellation) : SaveChangesInterceptor
    {
        public InvalidOperationException Error { get; } = new("Injected failure after insertion.");

        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            if (cancellation is null)
            {
                throw Error;
            }
            cancellation.Cancel();
            return ValueTask.FromResult(result);
        }
    }
}
