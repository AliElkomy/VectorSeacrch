using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using OllamaSharp;
 
using VectorSeacrch.Data;

var builder = WebApplication.CreateBuilder(args);

// 1. Add Controllers & OpenAPI/Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();


// 2. Configure SQL Server 2025 Connection with EF Core
string? connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException(
        "Connection string 'DefaultConnection' is not configured. Set it via user-secrets: "
        + "dotnet user-secrets set \"ConnectionStrings:DefaultConnection\" \"<your connection string>\"");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(connectionString));

// 3. Register OllamaSharp Services via Microsoft.Extensions.AI
Uri ollamaUri = new("http://localhost:11434");

// Embedding runs on CPU and costs ~130ms per chunk locally, so a large document is a
// multi-minute job. HttpClient's 100s default aborts the socket mid-request
// (SocketException 995 / ERROR_OPERATION_ABORTED), so the shared client carries no
// timeout of its own. The controller bounds each request by batching instead, and ties
// cancellation to HttpContext.RequestAborted.
var ollamaHttpClient = new HttpClient
{
    // Required by the OllamaApiClient(HttpClient, ...) constructor, which throws otherwise.
    BaseAddress = ollamaUri,
    Timeout = Timeout.InfiniteTimeSpan
};
builder.Services.AddSingleton(ollamaHttpClient);

builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    new OllamaApiClient(ollamaHttpClient, defaultModel: "qwen3-embedding:0.6b"));

builder.Services.AddSingleton<IChatClient>(
    new OllamaApiClient(ollamaHttpClient, defaultModel: "qwen2.5:1.5b")); //llama3.2

var app = builder.Build();

// Auto Apply EF Core Migrations
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Vector Search API v1");
        c.RoutePrefix = string.Empty; // Serves Swagger UI at application root
    });

}


app.UseAuthorization();

try
{
    app.MapControllers();
}
catch (System.Reflection.ReflectionTypeLoadException ex)
{
    foreach (var loaderException in ex.LoaderExceptions)
    {
        System.Diagnostics.Debug.WriteLine($"LOADER ERROR: {loaderException?.Message}");
    }
    throw;
}

app.Run();
