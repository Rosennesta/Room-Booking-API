using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RoomBooking.Api.Data;

public static class BookingDataExtensions
{
    public static IServiceCollection AddBookingData(this IServiceCollection services)
    {
        services.AddDbContext<BookingDbContext>((provider, options) =>
        {
            var configuration = provider.GetRequiredService<IConfiguration>();
            var environment = provider.GetRequiredService<IHostEnvironment>();
            var configuredPath = configuration["Database:Path"];
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                throw new InvalidOperationException("Database:Path must specify a SQLite database file.");
            }

            var path = Path.GetFullPath(configuredPath, environment.ContentRootPath);
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException("Database:Path must identify a file, not a filesystem root.");
            Directory.CreateDirectory(directory);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = path,
                ForeignKeys = true,
                DefaultTimeout = 5
            }.ToString();

            options.UseSqlite(connectionString, sqlite => sqlite.CommandTimeout(5));
        });

        return services;
    }

    public static async Task InitializeBookingDatabaseAsync(
        this IServiceProvider services,
        CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        await database.Database.MigrateAsync(cancellationToken);
    }
}
