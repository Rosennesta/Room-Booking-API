using System.Data;
using Microsoft.EntityFrameworkCore;
using RoomBooking.Api.Contracts;
using RoomBooking.Api.Data;
using RoomBooking.Api.Services.Interfaces;

namespace RoomBooking.Api.Services.Impl;

public sealed class BookingService(BookingDbContext database, TimeProvider timeProvider) : IBookingService
{
    public async Task<IReadOnlyList<RoomResponse>> GetRoomsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await database.Rooms.AsNoTracking()
            .OrderBy(room => room.Id)
            .Select(room => new RoomResponse(room.Id, room.Name, room.Capacity))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ReservationResponse>?> GetReservationsAsync(
        int roomId,
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roomId);
        if (from.HasValue && to.HasValue && from.Value >= to.Value)
        {
            throw new ArgumentException("The upper bound must be later than the lower bound.", nameof(to));
        }

        if (!await database.Rooms.AnyAsync(room => room.Id == roomId, cancellationToken))
        {
            return null;
        }

        var query = database.Reservations.AsNoTracking()
            .Where(reservation => reservation.RoomId == roomId);

        if (from.HasValue)
        {
            var fromDateTime = from.Value.UtcDateTime;
            query = query.Where(reservation => reservation.EndDateTime > fromDateTime);
        }

        if (to.HasValue)
        {
            var toDateTime = to.Value.UtcDateTime;
            query = query.Where(reservation => reservation.StartDateTime < toDateTime);
        }

        return await query.OrderBy(reservation => reservation.StartDateTime)
            .ThenBy(reservation => reservation.Id)
            .Select(reservation => new ReservationResponse(
                reservation.Id,
                reservation.RoomId,
                new DateTimeOffset(reservation.StartDateTime),
                new DateTimeOffset(reservation.EndDateTime),
                reservation.Title,
                new DateTimeOffset(reservation.CreateDateTime)))
            .ToListAsync(cancellationToken);
    }

    public async Task<CreateReservationResult> CreateReservationAsync(
        int roomId,
        CreateReservationRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(roomId);
        ArgumentNullException.ThrowIfNull(request);

        var startDateTime = request.Start.UtcDateTime;
        var endDateTime = request.End.UtcDateTime;
        if (startDateTime >= endDateTime)
        {
            throw new ArgumentException("The end must be later than the start.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("A non-blank title is required.", nameof(request));
        }

        var title = request.Title.Trim();
        if (title.Length > 200)
        {
            throw new ArgumentException("The trimmed title must not exceed 200 UTF-16 code units.", nameof(request));
        }

        var now = timeProvider.GetUtcNow().UtcDateTime;
        if (startDateTime < now)
        {
            return new CreateReservationResult.StartInPast();
        }

        var transactionStarted = false;
        try
        {
            await using var transaction = await database.Database.BeginTransactionAsync(
                IsolationLevel.Serializable, cancellationToken);
            transactionStarted = true;
            cancellationToken.ThrowIfCancellationRequested();

            if (!await database.Rooms.AnyAsync(room => room.Id == roomId, cancellationToken))
            {
                return new CreateReservationResult.RoomNotFound();
            }

            if (await database.Reservations.AnyAsync(existing =>
                existing.RoomId == roomId &&
                existing.StartDateTime < endDateTime &&
                existing.EndDateTime > startDateTime, cancellationToken))
            {
                return new CreateReservationResult.Conflict();
            }

            var reservation = new Reservation
            {
                RoomId = roomId,
                StartDateTime = startDateTime,
                EndDateTime = endDateTime,
                Title = title,
                CreateDateTime = now
            };
            database.Reservations.Add(reservation);
            await database.SaveChangesAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);

            return new CreateReservationResult.Created(new ReservationResponse(
                reservation.Id,
                reservation.RoomId,
                new DateTimeOffset(reservation.StartDateTime),
                new DateTimeOffset(reservation.EndDateTime),
                reservation.Title,
                new DateTimeOffset(reservation.CreateDateTime)));
        }
        finally
        {
            // A failed transaction start can leave EF's connection reference open without a transaction to dispose.
            if (!transactionStarted)
            {
                await database.Database.CloseConnectionAsync();
            }
        }
    }
}
