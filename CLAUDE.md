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

# Run Docker container
docker run -p 8080:8080 -p 8081:8081 obsidian-webhook
```

### Testing Endpoints
Use the `ObsidianWebhook.http` file with Visual Studio HTTP client or REST Client extension. Default host: `http://localhost:5135`

## Architecture

### Key Components

**Program.cs** - Entire application logic using minimal APIs:
- Database connection configuration via connection strings
- HTTP client factory for Obsidian API calls
- Minimal API endpoints for webhook operations

**local.settings.json** - Local development configuration (gitignored):
- PostgreSQL connection string
- **CRITICAL**: Contains credentials - never commit this file

### Database Schema

**VaultConfig Table** (PostgreSQL):
```sql
CREATE TABLE public."VaultConfig" (
    "Name" VARCHAR PRIMARY KEY,  -- Vault identifier (used in API path parameter)
    "ApiKey" VARCHAR NOT NULL    -- Obsidian Local REST API bearer token
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
- Queries database for API key → Calls hardcoded Obsidian API → Returns result

**GET /db-test**
- Database connection diagnostic endpoint
- Returns PostgreSQL version and connection details

### External API Integration

The service integrates with **Obsidian Local REST API** (spec in `obsidian-open-api.yaml`):
- Requires bearer token authentication (from database)
- Supports periodic notes, vault files, active files, search, and commands
- HTTPS typically on port 27124, HTTP on port 27123

### Configuration Flow

1. Request comes to `/periodic/{vault}/{period}` with markdown body
2. Validate `{period}` is one of: daily, weekly, monthly, quarterly, yearly
3. Query `VaultConfig` table using `{vault}` parameter to get `ApiKey`
4. Forward request to hardcoded Obsidian URL: `http://mylocalserver:27123/periodic/{period}/`
5. Authenticate using bearer token from database
6. Return Obsidian API response to caller

### Dependencies

- **Npgsql 9.0.2** - PostgreSQL database provider
- **Microsoft.AspNetCore.OpenApi 10.0.1** - OpenAPI/Swagger support
- HttpClient - For calling Obsidian REST API

## Important Notes

- All API responses use structured JSON with `success`, `message`, and contextual fields
- Database credentials are in `local.settings.json` (local dev) or connection strings configuration
- **Obsidian URL is hardcoded** to `http://mylocalserver:27123` (line 79 in Program.cs)
- The `vault` path parameter is used for database lookup to retrieve the API key
- `.WithOpenApi()` has been removed due to .NET 10 deprecation (replaced by built-in OpenAPI generation)
- Obsidian API may use self-signed certificates requiring certificate trust configuration
- Error responses include stack traces for debugging purposes

## Known Issues & Future Improvements

- Obsidian URL should be retrieved from database or configuration instead of being hardcoded
- Consider adding column `"ObsidianUrl"` to VaultConfig table for multi-instance support
