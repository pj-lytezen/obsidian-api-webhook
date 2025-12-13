# Obsidian API Webhook

A C# .NET 10 webhook service that acts as a proxy to post messages to Obsidian vaults via the Obsidian Local REST API. The service uses PostgreSQL to store vault configurations and authentication credentials.

## Features

- **Periodic Notes Integration** - Append content to daily, weekly, monthly, quarterly, or yearly notes
- **Queue-Based Delivery** - All notes queued in database before delivery with automatic cleanup on success
- **Batch Flush Endpoint** - Process all queued notes for a vault with detailed success/failure reporting
- **Database-Driven Configuration** - Store API keys and vault configurations in PostgreSQL
- **RESTful Webhook API** - Simple HTTP POST interface for external integrations
- **Docker Support** - Containerized deployment with multi-stage builds
- **OpenAPI Documentation** - Built-in Swagger/OpenAPI support for API exploration
- **Audit Trail** - All incoming notes logged with timestamp for debugging and retry scenarios

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

2a. Configure Obsidian API URL in `ObsidianWebhook/ObsidianWebhook/appsettings.json`:
   ```json
   {
     "Obsidian": {
       "ApiUrl": "http://localhost:27123"
     }
   }
   ```
   **Note:** Change the URL to match your Obsidian Local REST API server address.

3. Create the required database tables:
   ```sql
   -- Vault configuration table
   CREATE TABLE public."VaultConfig" (
       "Name" text COLLATE pg_catalog."default" NOT NULL,
       "ApiKey" text COLLATE pg_catalog."default" NOT NULL,
       CONSTRAINT "VaultConfig_pkey" PRIMARY KEY ("Name", "ApiKey")
   );

   -- Note queue table for audit/retry
   CREATE TABLE public."NoteQueue" (
       "Vault" text COLLATE pg_catalog."default" NOT NULL,
       "Note" text COLLATE pg_catalog."default" NOT NULL,
       "CreatedAt" timestamp without time zone NOT NULL DEFAULT now(),
       "Id" integer NOT NULL GENERATED ALWAYS AS IDENTITY ( INCREMENT 1 START 1 MINVALUE 1 MAXVALUE 2147483647 CACHE 1 ),
       CONSTRAINT "NoteQueue_pkey" PRIMARY KEY ("Id")
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

### Flush Queued Notes

**Endpoint:** `POST /periodic/{vault}/flush`

**Parameters:**
- `vault` - Vault name from VaultConfig table

**Description:** Processes all queued notes for the specified vault and sends them to Obsidian's daily periodic note.

**Example:**
```bash
curl -X POST "http://localhost:5135/periodic/MyVault/flush"
```

**Response:**
```json
{
  "success": true,
  "message": "Processed 5 queued notes for vault 'MyVault'",
  "vault": "MyVault",
  "totalNotes": 5,
  "successCount": 5,
  "failureCount": 0,
  "errors": null
}
```

### Test Database Connection

**Endpoint:** `GET /db-test`

Returns PostgreSQL connection status and version information.

## Use Cases

### Real-time Note Delivery
Use `POST /periodic/{vault}/{period}` for immediate delivery of notes to Obsidian. Notes are queued first for audit purposes, then delivered, and cleaned up on success.

### Retry Failed Deliveries
If Obsidian API is temporarily unavailable or network issues occur, failed notes remain in the queue. Use `POST /periodic/{vault}/flush` to retry all queued notes for a vault.

### Scheduled Batch Processing
Set up a scheduled task (cron, systemd timer, etc.) to periodically call the flush endpoint to process any accumulated notes.

### Manual Queue Management
Query the `NoteQueue` table directly to inspect queued notes:
```sql
SELECT * FROM public."NoteQueue" WHERE "Vault" = 'MyVault' ORDER BY "CreatedAt" DESC;
```

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
    "Id" INT PRIMARY KEY,       -- Auto-incrementing queue ID
    "Vault" VARCHAR NOT NULL,      -- Vault identifier
    "Note" TEXT NOT NULL,          -- Markdown note content
    "CreatedAt" TIMESTAMP DEFAULT NOW() -- Queue timestamp
);
```

### Obsidian Local REST API Setup

1. Install the "Local REST API" plugin in Obsidian
2. Enable the plugin and note the API key
3. Configure the server (default: HTTP on port 27123)
4. Update `appsettings.json` with the Obsidian server URL
5. Add the API key to your VaultConfig table

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
docker run -p 5135:5135 -p 5136:5136 obsidian-webhook
```

**Port Mapping:**
- `5135` - HTTP
- `5136` - HTTPS

## Technologies

- **.NET 10** - Modern web framework
- **ASP.NET Core Minimal APIs** - Lightweight HTTP API
- **Npgsql 9.0.2** - PostgreSQL data provider
- **Microsoft.AspNetCore.OpenApi** - API documentation
- **Dependency Injection** - Service-based architecture with DataStore service class

## Known Limitations

- Single Obsidian URL configured globally (no per-vault URL support)
- Only supports periodic note endpoints (not full Obsidian API)
- Flush endpoint sends all queued notes to daily periodic notes (doesn't preserve original period type)

## Future Enhancements

- [ ] Add `ObsidianUrl` column to VaultConfig for multi-instance support
- [ ] Add `Period` column to NoteQueue to preserve original destination period
- [ ] Support additional Obsidian API endpoints (vault files, search, etc.)
- [ ] Add authentication/authorization for webhook endpoints
- [ ] Implement request logging and monitoring
- [ ] Add retry logic for failed Obsidian API calls
- [ ] Add batch size limits for flush operations to prevent timeouts

## License

[Specify License]

## Contributing

[Contribution guidelines]
