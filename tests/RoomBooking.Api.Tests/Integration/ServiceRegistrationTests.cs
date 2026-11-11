using Microsoft.Extensions.DependencyInjection;
using RoomBooking.Api.Services.Impl;
using RoomBooking.Api.Services.Interfaces;
using Xunit;

namespace RoomBooking.Api.Tests.Integration;

public sealed class ServiceRegistrationTests : IClassFixture<BookingApiFactory>
{
    private readonly BookingApiFactory _factory;

    public ServiceRegistrationTests(BookingApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public void Booking_service_is_registered_with_scoped_lifetime()
    {
        using var firstScope = _factory.Services.CreateScope();
        using var secondScope = _factory.Services.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<IBookingService>();
        var repeated = firstScope.ServiceProvider.GetRequiredService<IBookingService>();
        var second = secondScope.ServiceProvider.GetRequiredService<IBookingService>();

        Assert.IsType<BookingService>(first);
        Assert.Same(first, repeated);
        Assert.NotSame(first, second);
    }
}
