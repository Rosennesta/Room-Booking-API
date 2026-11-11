using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;

namespace RoomBooking.Api.Infrastructure;

// Business errors and safe exception responses; framework errors use standard ProblemDetails.
public static class ApiProblems
{
    public static ProblemDetails Create(HttpContext context, int status, string code, string detail)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Type = "about:blank",
            Title = ReasonPhrases.GetReasonPhrase(status),
            Detail = detail,
            Instance = context.Request.Path
        };
        problem.Extensions["code"] = code;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier;
        return problem;
    }

    public static ObjectResult Result(ProblemDetails problem) => new(problem)
    {
        StatusCode = problem.Status,
        ContentTypes = { "application/problem+json" }
    };
}
