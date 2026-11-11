using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.OpenApi;
using RoomBooking.Api.Data;
using RoomBooking.Api.Infrastructure;
using RoomBooking.Api.Services.Impl;
using RoomBooking.Api.Services.Interfaces;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers()
    // Require explicit offsets in JSON timestamps and serialize them as UTC.
    .AddJsonOptions(options =>
        options.JsonSerializerOptions.Converters.Add(new ExplicitOffsetTimestampJsonConverter()));
            
builder.Services.AddOpenApi(options => options.AddSchemaTransformer((schema, context, cancellationToken) =>
{
    if (context.JsonTypeInfo.Type == typeof(int) &&
        context.ParameterDescription?.Source == BindingSource.Path)
    {
        // JSON number handling should not turn integer route parameters into integer|string unions.
        schema.Type = JsonSchemaType.Integer;
        schema.Pattern = null;
    }

    return Task.CompletedTask;
}));

builder.Services.AddProblemDetails();

// The exception middleware invokes this handler; controllers do not call it directly.
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<IBookingService, BookingService>();
builder.Services.AddBookingData();

var app = builder.Build();

// Wrap downstream request handling so unhandled exceptions reach the registered handler.
app.UseExceptionHandler();

// Add problem JSON to otherwise empty error responses, such as routing 404s and 405s.
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    await app.Services.InitializeBookingDatabaseAsync();
    app.MapOpenApi();
    
    // Expose an interactive client for the OpenAPI document only in development.
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("../openapi/v1.json", "Room Booking API v1");
        options.DocumentTitle = "Room Booking API";
    });
}

app.MapControllers();
app.Run();

public partial class Program
{
}
