# 🌐 Minimal Proxy for internal webservices

A lightweight, easy-to-deploy proxy for internal webservices. Provides secure access to your Exact services through a modern API gateway with multiple environment(s) support.

![Screenshot of Swagger UI](https://raw.githubusercontent.com/hawkinslabdev/minimalproxy/main/Source/example.png)

## 🚀 What is Minimal Proxy?

Minimal Proxy creates a secure gateway to your internal webservices (e.g. for Exact Globe+) services while adding:

- 🔐 Secure token-based authentication
- 🌍 Support for multiple environments (test, production, etc.)
- 📄 Simple configuration through JSON files
- 📝 Interactive Swagger documentation
- ♻️ Composite requests for complex operations
- 🔄 Automatic request/response handling
- 🔍 Detailed logging/tracing with automatic flushing

## 📋 Installation Guide for Windows IIS

### Prerequisites

- Windows Server with IIS installed
- .NET 8.0 Runtime ([Download](https://dotnet.microsoft.com/en-us/download/dotnet/8.0))
- IIS URL Rewrite Module ([Download](https://www.iis.net/downloads/microsoft/url-rewrite))
- Basic knowledge of IIS administration

### Step 1: Prepare the Server

1. Install the .NET 8.0 Hosting Bundle
2. Ensure IIS is properly configured

### Step 2: Deploy the Application

1. Download the latest release or build the application
2. Create a new folder on your server (e.g., `C:\Apps\MinimalProxy`)
3. Extract all files to this folder
4. Use the included `.publish.ps1` script for automated deployment (optional)

### Step 3: Configure IIS

1. Open IIS Manager
2. Create a new Application Pool
3. Change the default user for this pool to the Windows-user with the permissions to use the internal service (based on NTLM)

4. Create a new Website or Application:
   - Site name: `MinimalProxy`
   - Physical path: `C:\Apps\MinimalProxy` (or your chosen location)
   - Application Pool: `MinimalProxyPool`
   - Binding: Choose appropriate port (default 80) or host name
   - Use either the HTTP Rewrite Module or a reverse proxy to enforce HTTPS

### Step 4: Basic Configuration

1. Configure server environments by editing `environments/settings.json`:

```json
{
  "Environment": {
    "ServerName": "YourServerName",
    "AllowedEnvironments": [ "600", "700" ]
  }
}
```

2. Configure your endpoints in the `endpoints` folder (example: `endpoints/Items/entity.json`):

```json
{ 
  "Url": "http://your-server:8020/services/Exact.Entity.REST.EG/Items", 
  "Methods": ["GET", "POST"] 
}
```

3. Start your application and navigate to the Swagger documentation at `http://your-server/swagger`

## 🔐 Authentication Management

### Using the Token Generator

The MinimalProxy comes with a token management tool located in the `tools` folder:

1. Navigate to the `tools/TokenGenerator` folder
2. Run the tool using: `TokenGenerator.exe`
3. Follow the on-screen instructions to generate authentication tokens
4. Use the tokens in your API requests with the `Authorization: Bearer YOUR_TOKEN` header

### Command-Line Options

The Token Generator supports various command-line options:

```
TokenGenerator.exe -h                       Show help message
TokenGenerator.exe -d "path\to\auth.db"     Specify database location
TokenGenerator.exe -t "tokens/folder"       Specify token output folder
TokenGenerator.exe username                 Generate token for user
```

## 🌍 Using Multiple Environments

Access different Exact environments through the API by specifying the environment in the URL:

```
http://your-server/api/600/Items   (For environment 600)
http://your-server/api/700/Items   (For environment 700)
```

## 🧩 Working with Composite Endpoints

Composite endpoints allow you to chain multiple operations in a single request:

1. Create private endpoints for your internal services (like SalesOrderLine and SalesOrderHeader):

```json
{
  "Url": "http://your-server:8020/services/Exact.Entity.REST.EG/SalesOrderLine", 
  "Methods": ["POST"],
  "IsPrivate": true
}
```

3. Then create a composite endpoint configuration in the endpoints folder (example: endpoints/SalesOrder/entity.json):

```json
{
  "Type": "Composite",
  "Url": "http://localhost:8020/services/Exact.Entity.REST.EG",
  "Methods": ["POST"],
  "CompositeConfig": {
    "Name": "SalesOrder",
    "Description": "Creates a complete sales order with multiple order lines and a header",
    "Steps": [
      {
        "Name": "CreateOrderLines",
        "Endpoint": "SalesOrderLine",
        "Method": "POST",
        "IsArray": true,
        "ArrayProperty": "Lines",
        "TemplateTransformations": {
          "TransactionKey": "$guid"
        }
      },
      {
        "Name": "CreateOrderHeader",
        "Endpoint": "SalesOrderHeader",
        "Method": "POST",
        "SourceProperty": "Header",
        "TemplateTransformations": {
          "TransactionKey": "$prev.CreateOrderLines.0.d.TransactionKey"
        }
      }
    ]
  }
}
```

2. Use the endpoint with a single request containing all necessary data:

```http
POST http://your-server/api/600/composite/SalesOrder
Content-Type: application/json

{
  "Header": {
    "OrderDebtor": "60093",
    "YourReference": "Connect async"
  },
  "Lines": [
    {
      "Itemcode": "BEK0001",
      "Quantity": 2,
      "Price": 0
    },
    {
      "Itemcode": "BEK0002",
      "Quantity": 4,
      "Price": 0
    }
  ]
}
```

If either of the (3) requests fail, the chain of requests will halt immediately, returning detailed error information including which step failed, the error message, and any information returned from the proxied service.

## 🛡️ Network Protection

Configure URL validation and network security by editing `environments/network-access-policy.json`:

```json
{
  "allowedHosts": [
    "localhost",
    "127.0.0.1",
    "your-internal-server.domain"
  ],
  "blockedIpRanges": [
    "10.0.0.0/8",
    "172.16.0.0/12",
    "192.168.0.0/16",
    "169.254.0.0/16"
  ]
}
```

## 🔧 Troubleshooting

- **Application Won't Start**: Check Application Pool settings and permissions
- **Can't Connect to Database**: Verify the path to `auth.db` is correct and writable
- **Authentication Errors**: Ensure you're using a valid token with the Bearer prefix
- **Missing Endpoints**: Confirm all JSON configurations in the `endpoints` folder
- **Network Access Issues**: Check your `network-access-policy.json` configuration

## 📁 Directory Structure

```
MinimalProxy/
├── appsettings.json           # Main application settings
├── auth.db                    # Authentication database
├── endpoints/                 # Endpoint configuration files
│   ├── Items/
│   │   └── entity.json
│   ├── Account/
│   │   └── entity.json
│   ├── SalesOrder/
│   │   └── entity.json        # Composite configuration
│   └── ...
├── environments/              # Environment settings
│   ├── settings.json
│   └── network-access-policy.json
├── tools/                     # Utility tools
│   └── TokenGenerator/        # Token management utility
└── log/                       # Application logs
```

## ✨ Credits
Built with ❤️ using:
- [ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/)
- [Serilog](https://serilog.net/)
- [SQLite](https://www.sqlite.org/index.html)

Feel free to submit a PR if you'd like to contribute.
