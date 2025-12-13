using System.Text;
using ObsidianWebhook;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");
var obsidianApiUrl = builder.Configuration["Obsidian:ApiUrl"] ?? "http://localhost:27123";
var apiBearerToken = builder.Configuration["Api:BearerToken"];

if (string.IsNullOrWhiteSpace(apiBearerToken))
{
    throw new InvalidOperationException("Api:BearerToken configuration is required. Set it in appsettings.json or via environment variable API__BEARERTOKEN");
}

// Register DataStore service
builder.Services.AddSingleton(new DataStore(connectionString!));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Health check endpoint (no authorization required)
app.MapGet("/health", async (DataStore dataStore) =>
{
    var (dbSuccess, dbMessage, dbData) = await dataStore.TestDatabaseConnectionAsync();

    var healthStatus = new
    {
        status = dbSuccess ? "healthy" : "unhealthy",
        timestamp = DateTime.UtcNow,
        checks = new
        {
            database = new
            {
                status = dbSuccess ? "healthy" : "unhealthy",
                message = dbMessage,
                data = dbData
            }
        }
    };

    return dbSuccess ? Results.Ok(healthStatus) : Results.Json(healthStatus, statusCode: 503);
})
.WithName("HealthCheck");

// Bearer token authorization middleware
app.Use(async (context, next) =>
{
    // Skip authorization for health check endpoint
    if (context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    var authHeader = context.Request.Headers["Authorization"].FirstOrDefault();

    if (authHeader == null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = 401;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"success\":false,\"message\":\"Missing or invalid Authorization header. Provide 'Bearer <token>'\"}");
        return;
    }

    var token = authHeader.Substring("Bearer ".Length).Trim();

    if (token != apiBearerToken)
    {
        context.Response.StatusCode = 401;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"success\":false,\"message\":\"Invalid bearer token\"}");
        return;
    }

    await next();
});

// Periodic Notes endpoint - Posts content to Obsidian vault periodic notes
app.MapPost("/periodic/{vault}/{period}", async (
    string vault,
    string period,
    HttpRequest request,
    DataStore dataStore,
    IHttpClientFactory httpClientFactory) =>
{
    try
    {
        // Validate period parameter
        var validPeriods = new[] { "daily", "weekly", "monthly", "quarterly", "yearly" };
        if (!validPeriods.Contains(period.ToLower()))
        {
            return Results.BadRequest(new
            {
                success = false,
                message = $"Invalid period '{period}'. Must be one of: {string.Join(", ", validPeriods)}"
            });
        }

        // Query VaultConfig table to get API key for the specified vault name
        var apiKey = await dataStore.GetVaultApiKeyAsync(vault);
        if (apiKey == null)
        {
            return Results.NotFound(new
            {
                success = false,
                message = $"Vault configuration '{vault}' not found in database"
            });
        }

        // Read the request body (markdown content)
        string content;
        using (var reader = new StreamReader(request.Body))
        {
            content = await reader.ReadToEndAsync();
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            return Results.BadRequest(new
            {
                success = false,
                message = "Request body cannot be empty. Provide markdown content to append."
            });
        }

        // Insert note into queue database and get the generated Id
        var noteQueueId = await dataStore.InsertNoteToQueueAsync(vault, content);

        // Call Obsidian Local REST API
        var httpClient = httpClientFactory.CreateClient();
        var obsidianEndpoint = $"{obsidianApiUrl}/periodic/{period}/";

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, obsidianEndpoint);
        httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
        httpRequest.Content = new StringContent(content, Encoding.UTF8, "text/markdown");

        var response = await httpClient.SendAsync(httpRequest);

        if (response.IsSuccessStatusCode)
        {
            // Remove note from queue database
            await dataStore.DeleteNoteFromQueueAsync(noteQueueId);

            return Results.Ok(new
            {
                success = true,
                message = $"Successfully appended content to {period} periodic note in vault '{vault}'",
                vault = vault,
                period = period,
                statusCode = (int)response.StatusCode
            });
        }
        else
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            return Results.Json(new
            {
                success = false,
                message = "Failed to append content to Obsidian vault",
                vault = vault,
                period = period,
                statusCode = (int)response.StatusCode,
                error = errorContent
            }, statusCode: (int)response.StatusCode);
        }
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            success = false,
            message = "An error occurred while processing the request",
            error = ex.Message,
            stackTrace = ex.StackTrace
        }, statusCode: 500);
    }
})
.WithName("AppendToPeriodicNote");

// Flush endpoint - Processes all queued notes for a vault
app.MapPost("/periodic/{vault}/flush", async (
    string vault,
    DataStore dataStore,
    IHttpClientFactory httpClientFactory) =>
{
    try
    {
        // Query VaultConfig table to get API key for the specified vault
        var apiKey = await dataStore.GetVaultApiKeyAsync(vault);
        if (apiKey == null)
        {
            return Results.NotFound(new
            {
                success = false,
                message = $"Vault configuration '{vault}' not found in database"
            });
        }

        // Get all queued notes for this vault
        var queuedNotes = await dataStore.GetQueuedNotesForVaultAsync(vault);

        if (queuedNotes.Count == 0)
        {
            return Results.Ok(new
            {
                success = true,
                message = $"No queued notes found for vault '{vault}'",
                vault = vault,
                totalNotes = 0,
                successCount = 0,
                failureCount = 0
            });
        }

        // Process each queued note
        var httpClient = httpClientFactory.CreateClient();
        var successCount = 0;
        var failureCount = 0;
        var processedIds = new List<int>();
        var errors = new List<string>();

        foreach (var (id, note) in queuedNotes)
        {
            try
            {
                // Default to daily periodic note for flush
                var obsidianEndpoint = $"{obsidianApiUrl}/periodic/daily/";

                var httpRequest = new HttpRequestMessage(HttpMethod.Post, obsidianEndpoint);
                httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
                httpRequest.Content = new StringContent(note, Encoding.UTF8, "text/markdown");

                var response = await httpClient.SendAsync(httpRequest);

                if (response.IsSuccessStatusCode)
                {
                    processedIds.Add(id);
                    successCount++;
                }
                else
                {
                    failureCount++;
                    errors.Add($"Note ID {id}: {response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                failureCount++;
                errors.Add($"Note ID {id}: {ex.Message}");
            }
        }

        // Delete successfully processed notes from queue
        await dataStore.DeleteMultipleNotesFromQueueAsync(processedIds.ToArray());

        return Results.Ok(new
        {
            success = failureCount == 0,
            message = $"Processed {queuedNotes.Count} queued notes for vault '{vault}'",
            vault = vault,
            totalNotes = queuedNotes.Count,
            successCount = successCount,
            failureCount = failureCount,
            errors = errors.Count > 0 ? errors : null
        });
    }
    catch (Exception ex)
    {
        return Results.Json(new
        {
            success = false,
            message = "An error occurred while flushing queued notes",
            error = ex.Message,
            stackTrace = ex.StackTrace
        }, statusCode: 500);
    }
})
.WithName("FlushQueuedNotes");

app.Run();