using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace RoomBooking.Api.Infrastructure;

// Invoked by ASP.NET Core exception middleware for unhandled downstream request exceptions.
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        // Leave aborted requests to the framework rather than reporting them as server failures.
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        // SQLite errors can surface directly or be wrapped by EF Core during an update.
        var sqlite = exception as SqliteException
            ?? (exception is DbUpdateException ? exception.InnerException as SqliteException : null);
        // SQLITE_BUSY (5) and SQLITE_LOCKED (6) are treated as temporary unavailability.
        var busy = sqlite?.SqliteErrorCode is 5 or 6;
        var traceId = Activity.Current?.Id ?? httpContext.TraceIdentifier;

        if (busy)
        {
            logger.LogWarning(exception, "Database lock wait expired. TraceId: {TraceId}",
                traceId);
        }
        else
        {
            logger.LogError(exception, "Unexpected API failure. TraceId: {TraceId}",
                traceId);
        }

        // Keep exception details in logs; return a safe message and a trace ID to the client.
        var status = busy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status500InternalServerError;
        var problem = ApiProblems.Create(httpContext, status,
            busy ? "database_busy" : "internal_error",
            busy ? "The database is busy. Please try again later." : "An unexpected server error occurred.");
        
        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, options: (System.Text.Json.JsonSerializerOptions?)null,
            contentType: "application/problem+json", cancellationToken: cancellationToken);
        
        // Signal that the response is complete and no further exception handler is needed.
        return true;
    }
}
