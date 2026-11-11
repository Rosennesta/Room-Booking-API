# Room Booking MVP

A conference room booking REST API built with .NET 10, ASP.NET Core, EF Core,
and local SQLite. It lists rooms, queries reservations by an optional time
window, and creates reservations while preventing overlapping bookings or out-dated bookings.

## Prerequisites

- .NET 10 SDK.
- Run the commands below from the repository root, which contains `RoomBooking.sln`.
  The examples use PowerShell.

## Run

```powershell
dotnet restore .\RoomBooking.sln
dotnet run --project .\src\RoomBooking.Api --launch-profile http
```

Open **[Swagger UI](http://localhost:5080/swagger)** to explore the three endpoints
and send requests using **Try it out**. Keep the terminal running; press **Ctrl+C**
to stop the application. The OpenAPI document is at
[`/openapi/v1.json`](http://localhost:5080/openapi/v1.json).

The `http` launch profile enables Development mode. Startup applies migrations
and creates three sample rooms automatically; no separate database setup is
needed. Data is stored in `src\RoomBooking.Api\App_Data\room-booking.db` and
survives restarts. Swagger requests use this database, not a disposable test database.

Migration history has been consolidated into a single `InitialCreate` for this
unreleased MVP. Databases created with the earlier migrations must be recreated;
this reset is only suitable for disposable demo/test data. Future schema changes
should use new migrations rather than rewriting the initial migration.

When creating a reservation, use ISO 8601 timestamps with `Z` or an explicit offset
(seconds are optional, e.g. `2026-11-12T10:00Z`) and set the end later than the start.
The start must not be earlier than the server's
current UTC time when validated; a past start returns `400` with a `start` field error.
Reusing an occupied time range returns `409`;
an adjacent reservation is allowed.

## Test

```powershell
dotnet test .\RoomBooking.sln
```

Tests create isolated temporary SQLite databases and do not modify the application's
database. The command builds the solution before running tests; no API process
needs to be started separately.
