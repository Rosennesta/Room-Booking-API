using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Xunit;

namespace RoomBooking.Api.Tests.Integration;

public sealed class SwaggerUiTests
{
    [Theory]
    [InlineData("get")]
    [InlineData("post")]
    public async Task Room_id_is_a_required_integer_not_a_union_for_swagger_validation(string method)
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var parameter = document.RootElement.GetProperty("paths")
            .GetProperty("/api/rooms/{roomId}/reservations").GetProperty(method)
            .GetProperty("parameters").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "roomId");

        Assert.Equal("path", parameter.GetProperty("in").GetString());
        Assert.True(parameter.GetProperty("required").GetBoolean());
        var schema = parameter.GetProperty("schema");
        Assert.Equal("integer", schema.GetProperty("type").GetString());
        Assert.Equal("int32", schema.GetProperty("format").GetString());
        Assert.Equal(1, schema.GetProperty("minimum").GetInt32());
        Assert.Equal(int.MaxValue, schema.GetProperty("maximum").GetInt32());
        Assert.False(schema.TryGetProperty("pattern", out _));
    }

    [Fact]
    public async Task Development_serves_ui_assets_and_existing_openapi_document()
    {
        using var factory = new BookingApiFactory();
        using var client = factory.CreateClient();
        using var page = await client.GetAsync("/swagger");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("swagger-ui", html);
        Assert.Contains("Room Booking API", html);

        using var initializer = await client.GetAsync("/swagger/index.js");
        Assert.Equal(HttpStatusCode.OK, initializer.StatusCode);
        var script = await initializer.Content.ReadAsStringAsync();
        Assert.Contains("../openapi/v1.json", html + script);
        using var bundle = await client.GetAsync("/swagger/swagger-ui-bundle.js");
        Assert.Equal(HttpStatusCode.OK, bundle.StatusCode);
        using var css = await client.GetAsync("/swagger/swagger-ui.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        using var document = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, document.StatusCode);
        Assert.Contains("/api/rooms", await document.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("/swagger")]
    [InlineData("/swagger/index.html")]
    [InlineData("/swagger/swagger-ui-bundle.js")]
    [InlineData("/openapi/v1.json")]
    public async Task Production_does_not_expose_ui_or_document(string path)
    {
        using var factory = new BookingApiFactory();
        using var production = factory.WithWebHostBuilder(builder => builder.UseEnvironment("Production"));
        using var client = production.CreateClient();

        using var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
