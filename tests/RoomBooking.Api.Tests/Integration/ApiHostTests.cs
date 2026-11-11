using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomBooking.Api.Data;
using Xunit;

namespace RoomBooking.Api.Tests.Integration;

public sealed class ApiHostTests : IClassFixture<BookingApiFactory>
{
    private readonly BookingApiFactory _factory;

    public ApiHostTests(BookingApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Unknown_route_returns_not_found()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/nonexistent-scaffold-route");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Development_startup_migrates_the_configured_database()
    {
        using var scope = _factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        var connectionString = new SqliteConnectionStringBuilder(database.Database.GetConnectionString());

        Assert.Equal(_factory.DatabasePath, connectionString.DataSource);
        Assert.True(connectionString.ForeignKeys);
        Assert.Equal(5, connectionString.DefaultTimeout);
        Assert.Equal(5, database.Database.GetCommandTimeout());
        Assert.Equal(3, await database.Rooms.CountAsync());
        Assert.Equal(database.Database.GetMigrations(), await database.Database.GetAppliedMigrationsAsync());
    }
}
