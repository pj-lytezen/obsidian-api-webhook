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
cd ObsidianWebhook
docker build -t obsidian-webhook -f ObsidianWebhook/Dockerfile .

# Tag for registry deployment
docker tag obsidian-webhook <registry-host>:5000/obsidian-webhook:1.0.1

# Push to private registry
docker push <registry-host>:5000/obsidian-webhook:1.0.1

# Run Docker container locally (HTTP only)
docker run -p 5135:5135 \
  -e 'ConnectionStrings__DefaultConnection=Host=localhost;Port=5432;Database=YourDB;Username=user;Password=pass' \
  -e 'API__BEARERTOKEN=your-secure-token-here' \
  -e 'Obsidian__ApiUrl=http://localhost:27123' \
  obsidian-webhook

# Run from private registry with environment variables
docker run -d --name obsidian-webhook \
  -p 5135:5135 \
  -e 'ConnectionStrings__DefaultConnection=Host=<db-host>;Port=5432;Database=YourDB;Username=user;Password=pass' \
  -e 'API__BEARERTOKEN=your-secure-token-here' \
  -e 'Obsidian__ApiUrl=http://<obsidian-host>:27123' \
  <registry-host>:5000/obsidian-webhook:1.0.1
```

**Important Docker Notes:**
- Container is configured for HTTP only (port 5135). For HTTPS in production, use a reverse proxy like nginx or Traefik.
- Use environment variables to override `appsettings.json` configuration
- Connection strings use double underscore (`__`) syntax for nested configuration
- **REQUIRED:** `API__BEARERTOKEN` must be set - application will fail to start without it
- Generate secure tokens using: `openssl rand -base64 32` or `python -c "import secrets; print(secrets.token_urlsafe(32))"`
- Current production version: 1.0.1

### Testing Endpoints
Use the `ObsidianWebhook.http` file with Visual Studio HTTP client or REST Client extension. Default host: `http://localhost:5135`

## Architecture

### Key Components

**Program.cs** - Application entry point using minimal APIs:
- Service registration and configuration
- HTTP client factory for Obsidian API calls
- Bearer token authentication middleware (validates all requests)
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
- Bearer token for API authentication (Api:BearerToken)
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

### Authentication

**Bearer Token Middleware:**
All API endpoints require bearer token authentication via the `Authorization` header:
```
Authorization: Bearer <token-from-config>
```

- Token is configured via `Api:BearerToken` in appsettings.json or `API__BEARERTOKEN` environment variable
- Middleware executes before all endpoints and returns HTTP 401 if:
  - Authorization header is missing
  - Authorization header doesn't start with "Bearer "
  - Token doesn't match the configured value
- Application startup fails if bearer token is not configured (required configuration)
- **Exception:** The `/health` endpoint bypasses authorization for monitoring system access

**Response for unauthorized requests:**
```json
{
  "success": false,
  "message": "Missing or invalid Authorization header. Provide 'Bearer <token>'"
}
```

### API Endpoints

**GET /health** (No Authorization Required)
- Health check endpoint for monitoring and load balancers
- Tests database connectivity using TestDatabaseConnectionAsync()
- Returns HTTP 200 with "healthy" status if database is accessible
- Returns HTTP 503 with "unhealthy" status if database connection fails
- Response includes timestamp and detailed database check results
- **IMPORTANT:** This endpoint bypasses bearer token authentication

**POST /periodic/{vault}/{period}** (Authorization Required)
- `vault`: Vault name to lookup in VaultConfig table
- `period`: daily, weekly, monthly, quarterly, or yearly
- Body: Markdown content (text/markdown)
- Queries database for API key → Queues note → Calls Obsidian API → Deletes from queue on success

**POST /periodic/{vault}/flush** (Authorization Required)
- `vault`: Vault name to flush queued notes for
- Processes all queued notes for the specified vault
- Sends each note to daily periodic note endpoint
- Deletes successfully delivered notes from queue
- Returns summary with totalNotes, successCount, failureCount, and errors

### External API Integration

The service integrates with **Obsidian Local REST API** (spec in `obsidian-open-api.yaml`):
- Requires bearer token authentication (from database)
- Supports periodic notes, vault files, active files, search, and commands
- HTTPS typically on port 27124, HTTP on port 27123

**CRITICAL NETWORK CONFIGURATION:**
- Obsidian Local REST API plugin must bind to `0.0.0.0` (not `127.0.0.1`) to accept network connections
- If bound to `127.0.0.1` (localhost only), Docker containers or remote services cannot connect
- Verify binding address in Obsidian plugin settings before deploying to Docker
- For Docker deployments, use the host machine's network IP address (e.g., `http://192.168.0.192:27123`)
- Localhost/127.0.0.1 URLs will NOT work from inside Docker containers

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

**Connection Refused (192.168.x.x:27123):**
- Most common cause: Obsidian Local REST API plugin bound to `127.0.0.1` instead of `0.0.0.0`
- Fix: In Obsidian plugin settings, change binding address from `127.0.0.1` to `0.0.0.0`
- Test connectivity from Docker host: `curl http://<obsidian-ip>:27123`
- Verify firewall allows incoming connections on ports 27123/27124
- Check container environment variable: `docker inspect <container-id> | grep Obsidian`

**Name or Service Not Known (hostname:27123):**
- Hostname is not resolvable from Docker container
- Use IP addresses instead of hostnames in `Obsidian__ApiUrl` environment variable
- For Windows hosts file mappings, note they don't apply inside Docker containers
- Test DNS resolution from container: `docker exec <container-id> nslookup <hostname>`

**HTTP 401 Unauthorized:**
- Missing or invalid bearer token in Authorization header
- Verify `API__BEARERTOKEN` environment variable matches token used in requests
- Check Authorization header format: `Authorization: Bearer <token>` (case-insensitive "Bearer")
- For Docker, inspect environment: `docker exec <container-id> printenv | grep API__BEARERTOKEN`
- Application logs will show "Missing or invalid Authorization header" or "Invalid bearer token"

## Known Issues & Future Improvements

- Consider adding column `"ObsidianUrl"` to VaultConfig table for per-vault URL support (multi-instance routing)
- Flush endpoint should store period type in NoteQueue table to preserve original destination
- Consider adding batch size limits for flush operations to prevent timeout on large queues
- Add periodic background job to automatically retry failed deliveries
- Add environment variable support for Obsidian:ApiUrl configuration
