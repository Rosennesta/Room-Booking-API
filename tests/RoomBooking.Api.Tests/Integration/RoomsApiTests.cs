using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RoomBooking.Api.Contracts;
using RoomBooking.Api.Data;
using Xunit;

namespace RoomBooking.Api.Tests.Integration;

public sealed class RoomsApiTests
{
    private const string Path = "/api/rooms/1/reservations";
    private const string ValidBody =
        """{"start":"2026-11-10T10:00:00Z","end":"2026-11-10T11:00:00Z","title":"Meeting"}""";

    [Fact]
    public async Task Rooms_are_listed_and_empty_room_lists_are_supported()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        var rooms = await client.GetFromJsonAsync<RoomResponse[]>("/api/rooms");
        Assert.NotNull(rooms);
        Assert.Equal([1, 2, 3], rooms.Select(room => room.Id));
        Assert.Equal(new RoomResponse(1, "Small Room", 4), rooms[0]);

        using var scope = factory.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<BookingDbContext>();
        await database.Rooms.ExecuteDeleteAsync();
        Assert.Empty((await client.GetFromJsonAsync<RoomResponse[]>("/api/rooms"))!);
    }

    [Fact]
    public async Task Create_normalizes_response_and_ignores_client_owned_server_fields()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var response = await Post(client,
            """
            {"id":999,"roomId":3,"createdAt":"2000-01-01T00:00:00Z",
             "start":"2026-11-10T18:00:00.1234567+08:00",
             "end":"2026-11-10T06:00:00.1234567-05:00","title":" \t Planning \r\n"}
            """);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Null(response.Headers.Location);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = await Json(response);
        Assert.NotEqual(999, json.GetProperty("id").GetInt32());
        Assert.Equal(1, json.GetProperty("roomId").GetInt32());
        Assert.Equal("Planning", json.GetProperty("title").GetString());
        Assert.Equal("2026-11-10T10:00:00.1234567Z", json.GetProperty("start").GetString());
        Assert.Equal("2026-11-10T11:00:00.1234567Z", json.GetProperty("end").GetString());
        Assert.EndsWith("Z", json.GetProperty("createdAt").GetString());
        Assert.Equal(factory.Now, json.GetProperty("createdAt").GetDateTimeOffset());

        var reservations = await client.GetFromJsonAsync<ReservationResponse[]>(Path);
        Assert.Equal(json.GetProperty("id").GetInt32(), Assert.Single(reservations!).Id);
    }

    [Fact]
    public async Task Conflict_adjacent_and_other_room_results_use_http_contract()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var first = await Post(client, ValidBody);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var conflict = await Post(client, ValidBody);
        await AssertProblem(conflict, 409, "reservation_conflict");
        using var adjacent = await Post(client,
            """{"start":"2026-11-10T11:00:00Z","end":"2026-11-10T12:00:00Z","title":"Next"}""");
        Assert.Equal(HttpStatusCode.Created, adjacent.StatusCode);
        using var other = await Post(client, ValidBody, "/api/rooms/2/reservations");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal(2, (await client.GetFromJsonAsync<ReservationResponse[]>(Path))!.Length);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Creation_compares_start_with_server_time(int ticks)
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync(Path, new CreateReservationRequest(
            factory.Now.AddTicks(ticks).ToOffset(TimeSpan.FromHours(8)),
            factory.Now.AddHours(1).ToOffset(TimeSpan.FromHours(-5)), "Meeting"));

        if (ticks < 0)
        {
            var problem = await AssertProblem(response, 400);
            Assert.Equal("The start must not be earlier than the current time.",
                Assert.Single(problem.GetProperty("errors").GetProperty("start").EnumerateArray()).GetString());
            Assert.Empty((await client.GetFromJsonAsync<ReservationResponse[]>(Path))!);
        }
        else
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Single((await client.GetFromJsonAsync<ReservationResponse[]>(Path))!);
        }
    }

    [Fact]
    public async Task Past_start_is_rejected_before_missing_room_lookup()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.PostAsJsonAsync("/api/rooms/999/reservations",
            new CreateReservationRequest(factory.Now.AddHours(-2), factory.Now.AddHours(-1), "Meeting"));

        var problem = await AssertProblem(response, 400);
        Assert.True(problem.GetProperty("errors").TryGetProperty("start", out _));
    }

    [Fact]
    public async Task Missing_room_and_empty_results_are_distinct()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        Assert.Empty((await client.GetFromJsonAsync<ReservationResponse[]>(Path))!);
        using var missing = await client.GetAsync("/api/rooms/999/reservations");
        await AssertProblem(missing, 404, "room_not_found");
        using var create = await Post(client, ValidBody, "/api/rooms/999/reservations");
        await AssertProblem(create, 404, "room_not_found");
    }

    [Theory]
    [InlineData("", new[] { "Early", "Crossing", "Late" })]
    [InlineData("?from=2026-11-10T10:00:00Z", new[] { "Crossing", "Late" })]
    [InlineData("?to=2026-11-10T11:00:00Z", new[] { "Early", "Crossing" })]
    [InlineData("?from=2026-11-10T18:00:00%2B08:00&to=2026-11-10T11:00:00Z", new[] { "Crossing" })]
    [InlineData("?from=2026-11-10T12:00:00Z", new string[0])]
    public async Task Queries_apply_optional_overlap_bounds(string query, string[] titles)
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        foreach (var (start, end, title) in new[]
        {
            ("11:00:00", "12:00:00", "Late"),
            ("09:30:00", "10:30:00", "Crossing"),
            ("09:00:00", "09:30:00", "Early")
        })
        {
            using var created = await client.PostAsJsonAsync(Path, new
            {
                start = $"2026-11-10T{start}Z", end = $"2026-11-10T{end}Z", title
            });
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        }

        var result = await client.GetFromJsonAsync<ReservationResponse[]>(Path + query);
        Assert.Equal(titles, result!.Select(item => item.Title));
    }

    public static IEnumerable<object[]> InvalidBodies()
    {
        yield return ["", ""];
        yield return ["null", ""];
        yield return ["{", "$"];
        yield return ["[]", "$"];
        yield return ["{}", "$"];
        yield return ["""{"end":"2026-11-10T11:00:00Z","title":"Meeting"}""", "$"];
        yield return ["""{"start":"2026-11-10T10:00:00Z","title":"Meeting"}""", "$"];
        yield return ["""{"start":"2026-11-10T10:00:00Z","end":"2026-11-10T11:00:00Z"}""", "$"];
        yield return [ValidBody.Replace("\"Meeting\"", "null"), "Title"];
        yield return [ValidBody.Replace("\"Meeting\"", "42"), "$.title"];
        yield return [ValidBody.Replace("Meeting", " \\t\\r\\n"), "Title"];
        yield return [ValidBody.Replace("Meeting", new string('x', 201)), "title"];
        yield return [ValidBody.Replace("11:00:00", "10:00:00"), "end"];
        yield return [ValidBody.Replace("11:00:00", "09:00:00"), "end"];
        yield return [ValidBody.Replace("\"2026-11-10T10:00:00Z\"", "null"), "$.start"];
        yield return [ValidBody.Replace("\"2026-11-10T10:00:00Z\"", "123"), "$.start"];
    }

    [Theory]
    [MemberData(nameof(InvalidBodies))]
    public async Task Invalid_bodies_return_validation_errors_without_inserting(string body, string field)
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var response = await Post(client, body);
        var problem = await AssertProblem(response, 400);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _), problem.ToString());
        Assert.Empty((await client.GetFromJsonAsync<ReservationResponse[]>(Path))!);
    }

    [Theory]
    [InlineData("2026-11-10")]
    [InlineData("2026-11-10Z")]
    [InlineData("2026-11-10T10:00")]
    [InlineData("2026-11-10T10:00:00")]
    [InlineData("2026-11-10T10:00:00.12345678Z")]
    [InlineData("2026-02-30T10:00:00Z")]
    [InlineData("2026-11-10T10:00:00+15:00")]
    [InlineData("0001-01-01T00:00:00+01:00")]
    [InlineData("9999-12-31T23:59:59-01:00")]
    [InlineData("November 10, 2026 10:00 +00:00")]
    [InlineData("2026/11/10T10:00Z")]
    [InlineData("2026-11-10 10:00Z")]
    [InlineData("2026-11-10T10:00:00Z\n")]
    [InlineData("")]
    public async Task Body_and_query_reject_the_same_invalid_timestamps(string timestamp)
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var body = await client.PostAsJsonAsync(Path, new
        {
            start = timestamp, end = "2026-11-10T11:00:00Z", title = "Meeting"
        });
        var bodyProblem = await AssertProblem(body, 400);
        Assert.True(bodyProblem.GetProperty("errors").TryGetProperty("$.start", out _));
        using var query = await client.GetAsync(Path + "?from=" + Uri.EscapeDataString(timestamp));
        var queryProblem = await AssertProblem(query, 400);
        Assert.True(queryProblem.GetProperty("errors").TryGetProperty("from", out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("2147483648")]
    public async Task Invalid_route_ids_return_400_not_route_not_found(string id)
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        var path = $"/api/rooms/{id}/reservations";
        using var get = await client.GetAsync(path);
        var problem = await AssertProblem(get, 400);
        Assert.True(problem.GetProperty("errors").TryGetProperty("roomId", out _));
        using var post = await Post(client, ValidBody, path);
        await AssertProblem(post, 400);
    }

    [Theory]
    [InlineData("?from=2026-11-10T10:00:00Z&to=2026-11-10T10:00:00Z", "to")]
    [InlineData("?from=2026-11-10T11:00:00Z&to=2026-11-10T10:00:00Z", "to")]
    [InlineData("?to=", "to")]
    [InlineData("?from=2026-11-10T10:00:00Z&from=2026-11-10T11:00:00Z", "from")]
    public async Task Invalid_query_ranges_are_field_errors(string query, string field)
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Path + query);
        var problem = await AssertProblem(response, 400);
        Assert.True(problem.GetProperty("errors").TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Trimmed_maximum_title_is_accepted()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var response = await Post(client, ValidBody.Replace("Meeting", " " + new string('x', 200) + " "));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Theory]
    [InlineData("2026-11-10T10:00Z")]
    [InlineData("2026-11-10T18:00+08:00")]
    [InlineData("2026-11-10T05:00-05:00")]
    [InlineData("2026-11-10T10:00:00Z")]
    [InlineData("2026-11-10T10:00:00.1Z")]
    [InlineData("2026-11-10T10:00:00.1234567Z")]
    [InlineData("2026-11-10T18:00:00+08:00")]
    [InlineData("2026-11-10T05:00:00-05:00")]
    public async Task Body_and_query_accept_explicit_offsets_and_supported_precision(string timestamp)
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var created = await client.PostAsJsonAsync(Path, new
        {
            start = timestamp, end = "2026-11-10T11:00Z", title = "Meeting"
        });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var reservations = await client.GetFromJsonAsync<ReservationResponse[]>(
            Path + "?from=" + Uri.EscapeDataString(timestamp));
        Assert.Single(reservations!);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_before_missing_room_lookup()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var response = await Post(client, ValidBody.Replace("Meeting", ""), "/api/rooms/999/reservations");
        await AssertProblem(response, 400);
    }

    [Fact]
    public async Task Framework_errors_use_problem_details()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var missing = await client.GetAsync("/does-not-exist");
        await AssertProblem(missing, 404);
        using var method = await client.DeleteAsync(Path);
        await AssertProblem(method, 405);
        Assert.Contains("GET", method.Content.Headers.Allow);
        using var media = await client.PostAsync(Path, new StringContent(ValidBody, Encoding.UTF8, "text/plain"));
        await AssertProblem(media, 415);
    }

    [Fact]
    public async Task Busy_database_returns_503_and_does_not_insert()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder { DataSource = factory.DatabasePath, Pooling = false }.ToString());
        await connection.OpenAsync();
        await using var transaction = connection.BeginTransaction(deferred: false);
        using var response = await Post(client, ValidBody);
        await AssertProblem(response, 503, "database_busy");
        await transaction.RollbackAsync();
        Assert.Empty((await client.GetFromJsonAsync<ReservationResponse[]>(Path))!);
    }

    [Fact]
    public async Task Unexpected_database_failure_returns_safe_500_even_in_development()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<BookingDbContext>()
                .Database.ExecuteSqlRawAsync("DROP TABLE Reservations");
        }

        using var response = await Post(client, ValidBody);
        var problem = await AssertProblem(response, 500, "internal_error");
        Assert.Equal("An unexpected server error occurred.", problem.GetProperty("detail").GetString());
        var raw = problem.ToString();
        Assert.DoesNotContain("Sqlite", raw);
        Assert.DoesNotContain(factory.DatabasePath, raw);
        Assert.False(problem.TryGetProperty("exception", out _));
    }

    [Fact]
    public async Task Concurrent_http_creates_return_one_created_and_one_conflict()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        var responses = await Task.WhenAll(
            Task.Run(() => Post(client, ValidBody)), Task.Run(() => Post(client, ValidBody)));
        try
        {
            Assert.Equal([201, 409], responses.Select(response => (int)response.StatusCode).Order());
            Assert.Single((await client.GetFromJsonAsync<ReservationResponse[]>(Path))!);
        }
        finally
        {
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    [Fact]
    public async Task Openapi_describes_operations_time_parameters_and_responses()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await Json(response);
        var paths = document.GetProperty("paths");
        Assert.True(paths.GetProperty("/api/rooms").TryGetProperty("get", out _));
        var reservations = paths.GetProperty("/api/rooms/{roomId}/reservations");
        var get = reservations.GetProperty("get");
        foreach (var name in new[] { "from", "to" })
        {
            var parameter = get.GetProperty("parameters").EnumerateArray()
                .Single(item => item.GetProperty("name").GetString() == name);
            Assert.Equal("query", parameter.GetProperty("in").GetString());
            Assert.False(parameter.TryGetProperty("required", out var required) && required.GetBoolean());
            Assert.Equal("date-time", parameter.GetProperty("schema").GetProperty("format").GetString());
        }
        var post = reservations.GetProperty("post");
        foreach (var status in new[] { "201", "400", "404", "409", "415", "500", "503" })
        {
            Assert.True(post.GetProperty("responses").TryGetProperty(status, out _), status);
        }
        Assert.True(post.GetProperty("requestBody").GetProperty("required").GetBoolean());
        var schema = document.GetProperty("components").GetProperty("schemas")
            .GetProperty("CreateReservationRequest");
        Assert.Equal(["end", "start", "title"], schema.GetProperty("required")
            .EnumerateArray().Select(value => value.GetString()).Order());
    }

    private static Task<HttpResponseMessage> Post(HttpClient client, string body, string path = Path)
        => client.PostAsync(path, new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> Json(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static async Task<JsonElement> AssertProblem(HttpResponseMessage response, int status, string? code = null)
    {
        Assert.Equal(status, (int)response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var json = await Json(response);
        Assert.Equal(status, json.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("type").GetString()));
        if (code is null)
        {
            Assert.False(json.TryGetProperty("code", out _));
        }
        else
        {
            Assert.Equal(code, json.GetProperty("code").GetString());
            Assert.Equal("about:blank", json.GetProperty("type").GetString());
            Assert.Equal(response.RequestMessage!.RequestUri!.AbsolutePath, json.GetProperty("instance").GetString());
        }
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("traceId").GetString()));
        return json;
    }
}
