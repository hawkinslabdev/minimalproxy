# 🌐 Minimal Proxy for Exact Globe Plus

A lightweight, easy-to-deploy proxy for Exact Globe Plus services. Provides secure access to your Exact services through a modern API gateway with multiple environment support.

![Screenshot of Swagger UI](https://raw.githubusercontent.com/hawkinslabdev/minimalproxy/main/Source/example.png)

## 🚀 What is Minimal Proxy?

Minimal Proxy creates a secure gateway to your Exact Globe Plus services while adding:

- 🔐 Secure token-based authentication
- 🌍 Support for multiple environments (test, production, etc.)
- 📄 Simple configuration through JSON files
- 📝 Interactive Swagger documentation
- 🔄 Automatic request/response handling

## 📋 Installation Guide for Windows IIS

### Prerequisites

- Windows Server with IIS installed
- .NET 8.0 Runtime ([Download](https://dotnet.microsoft.com/en-us/download/dotnet/8.0))
- IIS URL Rewrite Module ([Download](https://www.iis.net/downloads/microsoft/url-rewrite))
- Basic knowledge of IIS administration

### Step 1: Prepare the Server

1. Install the .NET 8.0 Hosting Bundle
2. Ensure IIS is properly configured with Application Pool for .NET Core

### Step 2: Deploy the Application

1. Download the latest release or build the application
2. Create a new folder on your server (e.g., `C:\inetpub\wwwroot\MinimalProxy`)
3. Extract all files to this folder

### Step 3: Configure IIS

1. Open IIS Manager
2. Create a new Application Pool:
   - Name: `MinimalProxyPool`
   - .NET CLR Version: `No Managed Code`
   - Managed pipeline mode: `Integrated`

3. Create a new Website or Application:
   - Site name: `MinimalProxy`
   - Physical path: `C:\inetpub\wwwroot\MinimalProxy` (or your chosen location)
   - Application Pool: `MinimalProxyPool`
   - Binding: Choose appropriate port (default 80) or host name

4. Set proper permissions:
   - Give `IIS_IUSRS` and your Application Pool identity read/write permissions to the application folder

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
TokenGenerator.exe username                 Generate token for user
```

## 🌍 Using Multiple Environments

Access different Exact environments through the API by specifying the environment in the URL:

```
http://your-server/api/600/Items   (For environment 600)
http://your-server/api/700/Items   (For environment 700)
```

## 🔧 Troubleshooting

- **Application Won't Start**: Check Application Pool settings and permissions
- **Can't Connect to Database**: Verify the path to `auth.db` is correct and writable
- **Authentication Errors**: Ensure you're using a valid token with the Bearer prefix
- **Missing Endpoints**: Confirm all JSON configurations in the `endpoints` folder

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
│   └── ...
├── environments/              # Environment settings
│   └── settings.json
├── tools/                     # Utility tools
│   └── TokenGenerator/        # Token management utility
└── log/                       # Application logs
```

## 📘 Need Help?

For more detailed documentation or assistance, please contact your system administrator or refer to the internal documentation.

---

*Updated: March 2025*