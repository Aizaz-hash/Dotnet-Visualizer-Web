using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "DAV";
});
builder.Services.AddAntiforgery();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAntiforgery();

// Minimal API Route Mapping linked directly to the extracted service logic
app.MapPost("/api/analyze", async (IFormFile file) =>
{
    if (file == null || file.Length == 0)
        return Results.BadRequest("No DLL uploaded.");

    try
    {
        var result = AssemblyAnalyzer.Analyze(file);
        return Results.Ok(result);
    }
    catch (InvalidDataException ex)
    {
        return Results.BadRequest(ex.Message);
    }
    catch (Exception ex)
    {
        return Results.BadRequest($"Failed to process raw DLL binary metadata stream: {ex.Message}");
    }
})
.DisableAntiforgery();

app.Run();