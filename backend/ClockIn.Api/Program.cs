using System.Globalization;
using ClockIn.Api.Data;
using ClockIn.Api.Services.Attendance;
using ClockIn.Api.Services.Time;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        if (context.ProblemDetails.Status == StatusCodes.Status500InternalServerError)
        {
            context.ProblemDetails.Extensions["code"] = "INTERNAL_ERROR";
        }
    };
});
builder.Services.AddDbContext<ClockInDbContext>((services, options) =>
    options.UseSqlServer(services.GetRequiredService<IConfiguration>().GetConnectionString("ClockIn")));
builder.Services.AddAuthoritativeTime(builder.Configuration);
builder.Services.AddScoped<AttendanceService>();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

var app = builder.Build();
if (string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("ClockIn")))
{
    throw new InvalidOperationException(
        "Configure ConnectionStrings:ClockIn using user secrets or the ConnectionStrings__ClockIn environment variable.");
}

// Configure the HTTP request pipeline.
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseStatusCodePages(async statusCodeContext =>
{
    var context = statusCodeContext.HttpContext;
    var status = context.Response.StatusCode;
    if (status is not (StatusCodes.Status404NotFound or StatusCodes.Status405MethodNotAllowed)
        || GetAttendanceEmployeeId(context.Request.Path) is not { } employeeId)
    {
        return;
    }

    if (status == StatusCodes.Status405MethodNotAllowed
        && !int.TryParse(employeeId, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
    {
        status = StatusCodes.Status404NotFound;
        context.Response.Headers.Remove(HeaderNames.Allow);
    }

    var code = status == StatusCodes.Status404NotFound ? "EMPLOYEE_NOT_FOUND" : "METHOD_NOT_ALLOWED";
    await Results.Problem(
        statusCode: status,
        extensions: new Dictionary<string, object?> { ["code"] = code }).ExecuteAsync(context);
});

app.UseAuthorization();

app.MapControllers();

app.Run();

static string? GetAttendanceEmployeeId(PathString path)
{
    if (!path.StartsWithSegments("/api/employees", out var remaining))
    {
        return null;
    }

    var segments = remaining.Value?.TrimEnd('/').Split('/');
    return segments is { Length: 3 or 4 }
        && segments[1].Length > 0
        && segments[2].Equals("attendance", StringComparison.OrdinalIgnoreCase)
        && (segments.Length == 3
            || segments[3].ToLowerInvariant() is "clock-in" or "clock-out" or "missing-clock-out")
        ? segments[1]
        : null;
}

public partial class Program;
