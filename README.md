# Obsidian API Webhook

A C# .NET 10 webhook service that acts as a proxy to post messages to Obsidian vaults via the Obsidian Local REST API. The service uses PostgreSQL to store vault configurations and authentication credentials.

## Features

- **Periodic Notes Integration** - Append content to daily, weekly, monthly, quarterly, or yearly notes
- **Database-Driven Configuration** - Store API keys and vault configurations in PostgreSQL
- **RESTful Webhook API** - Simple HTTP POST interface for external integrations
- **Docker Support** - Containerized deployment with multi-stage builds
- **OpenAPI Documentation** - Built-in Swagger/OpenAPI support for API exploration

## Quick Start

### Prerequisites

- .NET 10 SDK
- PostgreSQL 9.0+
- Obsidian with Local REST API plugin installed and configured

### Installation

1. Clone the repository:
   ```bash
   git clone <repository-url>
   cd obsidian-api-webhook
   ```

2. Configure database connection in `ObsidianWebhook/ObsidianWebhook/local.settings.json`:
   ```json
   {
     "ConnectionStrings": {
       "DefaultConnection": "Host=localhost;Port=5432;Database=YourDB;Username=user;Password=pass"
     }
   }
   ```

3. Create the required database tables:
   ```sql
   -- Vault configuration table
   CREATE TABLE public."VaultConfig" (
       "Name" VARCHAR PRIMARY KEY,
       "ApiKey" VARCHAR NOT NULL
   );

   -- Note queue table for audit/retry
   CREATE TABLE public."NoteQueue" (
       "Id" SERIAL PRIMARY KEY,
       "Vault" VARCHAR NOT NULL,
       "Note" TEXT NOT NULL,
       "CreatedAt" TIMESTAMP DEFAULT NOW()
   );
   ```

4. Insert vault configuration:
   ```sql
   INSERT INTO public."VaultConfig" ("Name", "ApiKey")
   VALUES ('MyVault', 'your-obsidian-api-key-here');
   ```

5. Run the application:
   ```bash
   cd ObsidianWebhook/ObsidianWebhook
   dotnet run
   ```

## API Usage

### Append to Periodic Note

**Endpoint:** `POST /periodic/{vault}/{period}`

**Parameters:**
- `vault` - Vault name from VaultConfig table
- `period` - One of: `daily`, `weekly`, `monthly`, `quarterly`, `yearly`

**Request Body:** Markdown content (text/markdown)

**Example:**
```bash
curl -X POST "http://localhost:5135/periodic/MyVault/daily" \
  -H "Content-Type: text/markdown" \
  -d "## Meeting Notes
- Discussed project timeline
- Action items assigned"
```

**Response:**
```json
{
  "success": true,
  "message": "Successfully appended content to daily periodic note in vault 'MyVault'",
  "vault": "MyVault",
  "period": "daily",
  "statusCode": 204
}
```

### Test Database Connection

**Endpoint:** `GET /db-test`

Returns PostgreSQL connection status and version information.

## Configuration

### Database Schema

```sql
-- Vault configuration
CREATE TABLE public."VaultConfig" (
    "Name" VARCHAR PRIMARY KEY,    -- Vault identifier
    "ApiKey" VARCHAR NOT NULL      -- Obsidian Local REST API token
);

-- Note queue for audit and retry
CREATE TABLE public."NoteQueue" (
    "Id" SERIAL PRIMARY KEY,       -- Auto-incrementing queue ID
    "Vault" VARCHAR NOT NULL,      -- Vault identifier
    "Note" TEXT NOT NULL,          -- Markdown note content
    "CreatedAt" TIMESTAMP DEFAULT NOW() -- Queue timestamp
);
```

### Obsidian Local REST API Setup

1. Install the "Local REST API" plugin in Obsidian
2. Enable the plugin and note the API key
3. Configure the server (default: HTTP on port 27123)
4. Add the API key to your VaultConfig table

## Architecture

```
External Client
    ↓ POST /periodic/{vault}/{period}
Webhook Service (this app)
    ↓ Query VaultConfig for API key
PostgreSQL Database
    ↓ Return API key
Webhook Service
    ↓ INSERT into NoteQueue
PostgreSQL Database (NoteQueue)
    ↓ Note queued
Webhook Service
    ↓ POST /periodic/{period}/ with Bearer auth
Obsidian Local REST API
    ↓ Append to periodic note
Obsidian Vault
```

## Development

### Build
```bash
dotnet build
```

### Run with auto-reload
```bash
dotnet watch run
```

### Docker Build
```bash
cd ObsidianWebhook
docker build -t obsidian-webhook -f ObsidianWebhook/Dockerfile .
docker run -p 8080:8080 obsidian-webhook
```

## Technologies

- **.NET 10** - Modern web framework
- **ASP.NET Core Minimal APIs** - Lightweight HTTP API
- **Npgsql 9.0.2** - PostgreSQL data provider
- **Microsoft.AspNetCore.OpenApi** - API documentation

## Known Limitations

- Obsidian URL is currently hardcoded to `http://mylocalserver:27123`
- Only supports periodic note endpoints (not full Obsidian API)
- Single Obsidian instance support (no multi-instance routing)

## Future Enhancements

- [ ] Add `ObsidianUrl` column to VaultConfig for multi-instance support
- [ ] Support additional Obsidian API endpoints (vault files, search, etc.)
- [ ] Add authentication/authorization for webhook endpoints
- [ ] Implement request logging and monitoring
- [ ] Add retry logic for failed Obsidian API calls

## License

[Specify License]

## Contributing

[Contribution guidelines]
