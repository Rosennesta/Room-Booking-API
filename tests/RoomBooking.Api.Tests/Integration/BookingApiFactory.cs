using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace RoomBooking.Api.Tests.Integration;

public sealed class BookingApiFactory : WebApplicationFactory<Program>
{
    private readonly TestDatabase _database = new();

    public string DatabasePath => _database.FilePath;

    public DateTimeOffset Now { get; } = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("Database:Path", _database.FilePath);
        builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Singleton<TimeProvider>(new FixedTimeProvider(Now))));
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            _database.Dispose();
        }
    }
}
