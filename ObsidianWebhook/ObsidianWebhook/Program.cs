using Npgsql;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddHttpClient();

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection");

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Database connection test endpoint
app.MapGet("/db-test", async () =>
{
    try
    {
        using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        
        using var command = new NpgsqlCommand("SELECT version()", connection);
        var version = await command.ExecuteScalarAsync();
        
        return Results.Ok(new 
        { 
            success = true, 
            message = "Database connection successful!", 
            postgresVersion = version?.ToString(),
            database = connection.Database,
            host = connection.Host,
            port = connection.Port
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new 
        { 
            success = false, 
            message = "Database connection failed", 
            error = ex.Message,
            stackTrace = ex.StackTrace
        });
    }
})
.WithName("TestDatabaseConnection");

// Periodic Notes endpoint - Posts content to Obsidian vault periodic notes
app.MapPost("/periodic/{vault}/{period}", async (
    string vault,
    string period,
    HttpRequest request,
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
        string? apiKey = null;
        string? obsidianUrl = "http://mylocalserver:27123";

        using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();

            var query = "SELECT \"ApiKey\" FROM public.\"VaultConfig\" WHERE public.\"VaultConfig\".\"Name\" = @name;";
            using var command = new NpgsqlCommand(query, connection);
            command.Parameters.AddWithValue("@name", vault);

            using var reader = await command.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                apiKey = reader.GetString(0);
            }
            else
            {
                return Results.NotFound(new
                {
                    success = false,
                    message = $"Vault configuration '{vault}' not found in database"
                });
            }
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

        // Insert note into queue database
        using (var connection = new NpgsqlConnection(connectionString))
        {
            await connection.OpenAsync();

            var insertQuery = @"INSERT INTO public.""NoteQueue""(""Vault"", ""Note"")
                                VALUES (@vault, @note);";
            using var command = new NpgsqlCommand(insertQuery, connection);
            command.Parameters.AddWithValue("@vault", vault);
            command.Parameters.AddWithValue("@note", content);

            await command.ExecuteNonQueryAsync();
        }

        // Call Obsidian Local REST API
        var httpClient = httpClientFactory.CreateClient();
        var obsidianEndpoint = $"{obsidianUrl}/periodic/{period}/";

        var httpRequest = new HttpRequestMessage(HttpMethod.Post, obsidianEndpoint);
        httpRequest.Headers.Add("Authorization", $"Bearer {apiKey}");
        httpRequest.Content = new StringContent(content, Encoding.UTF8, "text/markdown");

        var response = await httpClient.SendAsync(httpRequest);

        if (response.IsSuccessStatusCode)
        {
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

app.Run();

internal record WeatherForecast(DateOnly Date, int TemperatureC, string? Summary)
{
    public int TemperatureF => 32 + (int)(TemperatureC / 0.5556);
}
