using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using RoomBooking.Api.Infrastructure;
using Xunit;

namespace RoomBooking.Api.Tests.Validation;

public sealed class ApiExceptionHandlerTests
{
    [Theory]
    [InlineData(5, false, 503, "database_busy")]
    [InlineData(6, false, 503, "database_busy")]
    [InlineData(5, true, 503, "database_busy")]
    [InlineData(6, true, 503, "database_busy")]
    [InlineData(19, true, 500, "internal_error")]
    public async Task Only_known_sqlite_lock_errors_are_classified_as_busy(
        int errorCode, bool wrapped, int status, string code)
    {
        var sqlite = new SqliteException("private database detail", errorCode);
        Exception exception = wrapped ? new DbUpdateException("private wrapper", sqlite) : sqlite;
        await AssertHandled(exception, status, code);
    }

    [Fact]
    public async Task Unexpected_argument_errors_are_not_misclassified_as_client_validation()
        => await AssertHandled(new ArgumentException("private programming error"), 500, "internal_error");

    [Fact]
    public async Task Request_cancellation_is_not_written_as_a_business_or_server_error()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        using var body = new MemoryStream();
        context.Response.Body = body;

        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);
        var handled = await handler.TryHandleAsync(
            context, new OperationCanceledException(cancellation.Token), cancellation.Token);

        Assert.False(handled);
        Assert.Equal(0, body.Length);
    }

    private static async Task AssertHandled(Exception exception, int status, string code)
    {
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        using var body = new MemoryStream();
        context.Response.Body = body;
        context.Request.Path = "/api/rooms/1/reservations";
        var handler = new ApiExceptionHandler(NullLogger<ApiExceptionHandler>.Instance);

        Assert.True(await handler.TryHandleAsync(context, exception, CancellationToken.None));

        Assert.Equal(status, context.Response.StatusCode);
        body.Position = 0;
        using var json = await JsonDocument.ParseAsync(body);
        Assert.Equal(code, json.RootElement.GetProperty("code").GetString());
        Assert.DoesNotContain("private", json.RootElement.ToString());
    }
}
