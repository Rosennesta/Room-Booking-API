using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using RoomBooking.Api.Data;

namespace RoomBooking.Api.Tests.Integration;

public sealed class TestDatabase : IDisposable
{
    public string FilePath { get; } = Path.Combine(
        Path.GetTempPath(), $"room-booking-tests-{Guid.NewGuid():N}.db");

    public string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = FilePath,
        ForeignKeys = true,
        DefaultTimeout = 5
    }.ToString();

    public BookingDbContext CreateContext(params IInterceptor[] interceptors)
    {
        var options = new DbContextOptionsBuilder<BookingDbContext>()
            .UseSqlite(ConnectionString, sqlite => sqlite.CommandTimeout(5))
            .AddInterceptors(interceptors)
            .Options;

        return new BookingDbContext(options);
    }

    public void Dispose()
    {
        using var connection = new SqliteConnection(ConnectionString);
        SqliteConnection.ClearPool(connection);

        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" })
        {
            File.Delete(FilePath + suffix);
        }
    }
}
