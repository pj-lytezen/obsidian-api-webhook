# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

This is a C# .NET 10 webhook API that acts as a proxy service to post messages to Obsidian vaults via the Obsidian Local REST API. The service queries vault configurations from a PostgreSQL database and forwards requests to the appropriate Obsidian instance with proper authentication.

## Development Commands

### Build and Run
```bash
# Navigate to project directory
cd ObsidianWebhook/ObsidianWebhook

# Restore dependencies
dotnet restore

# Build the project
dotnet build

# Run the application
dotnet run

# Run with watch (auto-reload on changes)
dotnet watch run
```

### Docker
```bash
# Build Docker image (from ObsidianWebhook directory)
docker build -t obsidian-webhook -f ObsidianWebhook/Dockerfile .

# Run Docker container (HTTP only)
docker run -p 5135:5135 obsidian-webhook
```

**Note:** Docker container is configured for HTTP only (port 5135). For HTTPS in production, use a reverse proxy like nginx or Traefik.

### Testing Endpoints
Use the `ObsidianWebhook.http` file with Visual Studio HTTP client or REST Client extension. Default host: `http://localhost:5135`

## Architecture

### Key Components

**Program.cs** - Application entry point using minimal APIs:
- Service registration and configuration
- HTTP client factory for Obsidian API calls
- Minimal API endpoint definitions
- No direct database operations (delegated to DataStore)

**DataStore.cs** - Database access service class:
- Handles all PostgreSQL database operations
- Methods for vault configuration, note queue management
- Registered as singleton service
- Encapsulates all Npgsql/database logic

**local.settings.json** / **appsettings.Development.json** - Local development configuration (gitignored):
- PostgreSQL connection string
- **CRITICAL**: Contains credentials - never commit this file

**appsettings.json** - Application configuration:
- Obsidian API URL configuration
- Logging settings
- Allowed hosts

### Database Schema

**VaultConfig Table** (PostgreSQL):
```sql
CREATE TABLE public."VaultConfig" (
    "Name" VARCHAR PRIMARY KEY,  -- Vault identifier (used in API path parameter)
    "ApiKey" VARCHAR NOT NULL    -- Obsidian Local REST API bearer token
);
```

**NoteQueue Table** (PostgreSQL):
```sql
CREATE TABLE public."NoteQueue" (
    "Id" SERIAL PRIMARY KEY,          -- Auto-incrementing queue ID
    "Vault" VARCHAR NOT NULL,         -- Vault identifier
    "Note" TEXT NOT NULL,             -- Markdown note content
    "CreatedAt" TIMESTAMP DEFAULT NOW() -- Queue timestamp
);
```

**Important PostgreSQL Syntax Notes:**
- Use double quotes (`"`) for identifiers (table/column names) when case-sensitive
- Use single quotes (`'`) for string literal values
- Column reference: `"VaultConfig"."Name"` not `"VaultConfig.Name"`

### API Endpoints

**POST /periodic/{vault}/{period}**
- `vault`: Vault name to lookup in VaultConfig table
- `period`: daily, weekly, monthly, quarterly, or yearly
- Body: Markdown content (text/markdown)
- Queries database for API key → Queues note → Calls Obsidian API → Deletes from queue on success

**POST /periodic/{vault}/flush**
- `vault`: Vault name to flush queued notes for
- Processes all queued notes for the specified vault
- Sends each note to daily periodic note endpoint
- Deletes successfully delivered notes from queue
- Returns summary with totalNotes, successCount, failureCount, and errors

**GET /db-test**
- Database connection diagnostic endpoint
- Returns PostgreSQL version and connection details

### External API Integration

The service integrates with **Obsidian Local REST API** (spec in `obsidian-open-api.yaml`):
- Requires bearer token authentication (from database)
- Supports periodic notes, vault files, active files, search, and commands
- HTTPS typically on port 27124, HTTP on port 27123

### Configuration Flow

**Regular POST /periodic/{vault}/{period}:**
1. Request comes to `/periodic/{vault}/{period}` with markdown body
2. Validate `{period}` is one of: daily, weekly, monthly, quarterly, yearly
3. Query `VaultConfig` table using `{vault}` parameter to get `ApiKey`
4. **Insert note into `NoteQueue` table** and capture returned `Id`
5. Forward request to configured Obsidian URL from `appsettings.json`: `{Obsidian:ApiUrl}/periodic/{period}/`
6. Authenticate using bearer token from database
7. **On success: DELETE note from queue using captured `Id`**
8. Return Obsidian API response to caller

**Flush POST /periodic/{vault}/flush:**
1. Query `VaultConfig` table to get `ApiKey` for vault
2. SELECT all queued notes for vault (ordered by CreatedAt ASC)
3. If queue is empty, return success with zero counts
4. Loop through each note:
   - POST to `/periodic/daily/` with bearer auth
   - Track success/failure with note ID
   - Add successful IDs to deletion list
5. Bulk DELETE successfully processed notes using `ANY(@ids)` array
6. Return summary with totalNotes, successCount, failureCount, errors

### DataStore Service Methods

The `DataStore` class provides the following async methods:

- `GetVaultApiKeyAsync(string vaultName)` - Retrieves API key for a vault
- `InsertNoteToQueueAsync(string vault, string note)` - Inserts note and returns generated Id
- `DeleteNoteFromQueueAsync(int noteId)` - Deletes a single note by Id
- `GetQueuedNotesForVaultAsync(string vault)` - Gets all queued notes (ordered by CreatedAt)
- `DeleteMultipleNotesFromQueueAsync(int[] noteIds)` - Bulk delete using PostgreSQL ANY operator
- `TestDatabaseConnectionAsync()` - Tests connection and returns version info

All database operations are isolated in this service for maintainability and testability.

### Dependencies

- **Npgsql 9.0.2** - PostgreSQL database provider
- **Microsoft.AspNetCore.OpenApi 10.0.1** - OpenAPI/Swagger support
- HttpClient - For calling Obsidian REST API

## Important Notes

- All API responses use structured JSON with `success`, `message`, and contextual fields
- Database credentials are in `local.settings.json` (local dev) or connection strings configuration
- **Obsidian API URL is configured** in `appsettings.json` under `Obsidian:ApiUrl` (default: `http://localhost:27123`)
- The `vault` path parameter is used for database lookup to retrieve the API key
- `.WithOpenApi()` has been removed due to .NET 10 deprecation (replaced by built-in OpenAPI generation)
- Obsidian API may use self-signed certificates requiring certificate trust configuration
- Error responses include stack traces for debugging purposes
- **Flush endpoint defaults to daily periodic notes** - all queued notes are sent to `/periodic/daily/`

## Troubleshooting

### Check Queue Status
```sql
-- Count queued notes by vault
SELECT "Vault", COUNT(*) as "QueuedNotes"
FROM public."NoteQueue"
GROUP BY "Vault";

-- View oldest queued notes
SELECT * FROM public."NoteQueue"
ORDER BY "CreatedAt" ASC
LIMIT 10;
```

### Common Scenarios

**Notes stuck in queue:**
- Check if Obsidian Local REST API is running and accessible
- Verify API key in VaultConfig matches Obsidian plugin settings
- Verify `Obsidian:ApiUrl` in `appsettings.json` matches your Obsidian server URL
- Call flush endpoint to retry delivery

**Database connection issues:**
- Use `/db-test` endpoint to verify connection
- Check connection string in `local.settings.json` or `appsettings.Development.json`
- Verify PostgreSQL server is running and accepting connections

## Known Issues & Future Improvements

- Consider adding column `"ObsidianUrl"` to VaultConfig table for per-vault URL support (multi-instance routing)
- Flush endpoint should store period type in NoteQueue table to preserve original destination
- Consider adding batch size limits for flush operations to prevent timeout on large queues
- Add periodic background job to automatically retry failed deliveries
- Add environment variable support for Obsidian:ApiUrl configuration
