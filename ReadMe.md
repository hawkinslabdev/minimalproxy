
# 🌐 Minimal Proxy API

A lightweight, environment-aware reverse proxy for .NET (ASP.NET Core), with Bearer token authentication and endpoint mapping via JSON configuration files. Built-in support for SQLite token storage, XML/JSON URL rewriting, and detailed logging via Serilog.

---

## 🚀 Features

- ✅ Simple API Gateway for internal/external services
- 🔐 Bearer Token Authentication (stored in local SQLite)
- 🌍 Environment-based endpoint routing (e.g. `/api/dev/accounts`)
- 🔄 Proxy forwarding for all HTTP methods with header + body support
- ✍️ Automatic URL rewriting for XML and JSON responses
- 📄 Config-driven endpoints (`/endpoints/*.json`) and settings (`/environments/settings.json`)
- 🪵 Logging to file and console via Serilog
- 🧪 Built-in SQLite token seeding if empty

---

## 📦 Requirements

- [.NET 8+ SDK](https://dotnet.microsoft.com/en-us/download)
- Local write access to `log/`, `auth.db`, and `endpoints/` folders

---

## 🛠️ Setup

### 1. Clone the repository

```bash
git clone https://github.com/your-org/minimal-proxy.git
cd minimal-proxy
```

### 2. Create required folders

```bash
mkdir log
mkdir environments
mkdir endpoints
```

### 3. Add a settings file

**`environments/settings.json`**

```json
{
  "Environment": {
    "ServerName": "localhost",
    "AllowedEnvironments": [ "dev", "prod", "test" ]
  }
}
```

### 4. Add an endpoint config

**Example: `endpoints/accounts/endpoint.json`**

```json
{
  "Url": "https://example.com/api/accounts",
  "Methods": [ "GET", "POST", "PUT", "DELETE" ]
}
```

### 5. Run the app

```bash
dotnet run
```

---

## 🔐 Authentication

- On first run, a SQLite database `auth.db` will be created.
- If no tokens exist, a default token will be generated and logged:

```text
🗝️ Generated token: 8f3e7b9e-4c7a-4e5c-b6c1-fc129ad6fe65
```

- Include it in requests as:

```http
Authorization: Bearer YOUR_TOKEN
```

---

## 🔄 API Usage

**Proxy pattern:**

```
/api/{environment}/{endpoint}/{optional-path}
```

**Example:**

```http
GET /api/dev/accounts/123
```

Will forward to:

```http
https://example.com/api/accounts/123
```

(If configured in `endpoints/accounts/endpoint.json`)

---

## 🪵 Logging

- Logs are stored in the `/log` folder and rotate daily
- Console output includes timestamps
- Logging level for EF Core database commands is overridden to `Warning`

---

## 📤 Response Rewriting

Automatically rewrites:

- XML attributes and values (e.g. `xml:base`, `href`, `id`)
- JSON values containing the original base URL
- Avoids duplicate rewriting if already proxied

This allows OData/SOAP APIs to work without client modifications.

---

## 🧪 Development Notes

- `AuthDbContext` uses raw SQL to ensure the `Tokens` table exists
- Token validation occurs for every incoming request before proxying
- Internal requests use `HttpClientFactory` with default credentials enabled

---

## 📁 Project Structure

| Path                        | Description                                |
|-----------------------------|--------------------------------------------|
| `/log`                      | Rolling file logs                          |
| `/auth.db`                  | SQLite database storing bearer tokens      |
| `/endpoints`                | Folder for endpoint configs                |
| `/environments/settings.json` | Environment + server configuration       |

---

## 🧾 Example `endpoint.json`

```json
{
  "Url": "https://api.contoso.com/data",
  "Methods": [ "GET", "POST" ]
}
```

---

## 📘 License

MIT — Feel free to use, extend, and contribute!

---

## ✨ Credits

Built with ❤️ using:

- [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/)
- [Serilog](https://serilog.net/)
- [SQLite](https://www.sqlite.org/index.html)

*Generated on 2025-03-21*
