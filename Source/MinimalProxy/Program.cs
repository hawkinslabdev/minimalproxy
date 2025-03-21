using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;
using Serilog;

Directory.CreateDirectory("log");

Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .WriteTo.File(
        path: "log/minimalproxy-.log",
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 5,
        buffered: true)
    .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", Serilog.Events.LogEventLevel.Warning)
    .CreateLogger();

Log.Information("✅ Logging initialized successfully.");

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();

var config = builder.Configuration;

// Configure Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.FormatterName = "simple");
builder.Logging.AddSimpleConsole(options => options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ");


// Configure SQLite Authentication Database
var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "auth.db");
builder.Services.AddDbContext<AuthDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
builder.Services.AddAuthorization();
//builder.Services.AddHttpClient();

builder.Services.AddHttpClient("ProxyClient")
    .ConfigurePrimaryHttpMessageHandler(() =>
    {
        return new HttpClientHandler
        {
            UseDefaultCredentials = true, // Hiermee gebruikt de HttpClient de Windows credentials van de applicatie
            PreAuthenticate = true
        };
    });


var app = builder.Build();

// Initialize Database & Create Default Token if needed
using (var scope = app.Services.CreateScope())
{
    var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

    try
    {
        context.Database.EnsureCreated();
        context.EnsureTablesCreated();

        if (!context.Tokens.Any())
        {
            var token = new AuthToken { Token = Guid.NewGuid().ToString() };
            context.Tokens.Add(token);
            context.SaveChanges();
            Log.Debug("🗝️ Generated token: {Token}", token.Token);
        }
        else
        {
            var tokens = context.Tokens.Select(t => t.Token).ToList();
            Log.Debug("Existing tokens: {Tokens}", string.Join(", ", tokens));
        }
    }
    catch (Exception ex)
    {
        Log.Error("❌ Database initialization failed: {Message}", ex.Message);
    }
}

// Middleware to log request headers
app.Use(async (context, next) =>
{
    Log.Information("➡️ [{Timestamp}] Incoming request: {Path}", DateTime.UtcNow, context.Request.Path);


    if (!context.Request.Headers.TryGetValue("Authorization", out var providedToken))
    {
        Log.Warning("❌ Authorization header missing.");
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
        return;
    }

    string tokenString = providedToken.ToString();
    Log.Debug("🔍 Received token: {Token}", tokenString);

    if (tokenString.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
    {
        tokenString = tokenString.Substring("Bearer ".Length).Trim();
    }

    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<AuthDbContext>();

    dbContext.Database.EnsureCreated();

    var storedTokens = dbContext.Tokens.Select(t => t.Token).ToList();
    Log.Debug("🔑 Stored tokens: {Tokens}", string.Join(", ", storedTokens));

    bool isValid = storedTokens.Any(t => string.Equals(t, tokenString, StringComparison.OrdinalIgnoreCase));

    if (!isValid)
    {
        Log.Warning("❌ Invalid token: {Token}", tokenString);
        context.Response.StatusCode = 403;
        await context.Response.WriteAsJsonAsync(new { error = "Forbidden" });
        return;
    }

    Log.Information("✅ Authorized request with valid token.");
    await next();

});


// Load ServerName from /environments/settings.json
// Load ServerName and Allowed Environments from /environments/settings.json
var settingsFile = Path.Combine(Directory.GetCurrentDirectory(), "environments", "settings.json");

string serverName = ".";
HashSet<string> allowedEnvironments = new(StringComparer.OrdinalIgnoreCase);

if (File.Exists(settingsFile))
{
    var settingsJson = File.ReadAllText(settingsFile);
    var settings = JsonSerializer.Deserialize<Settings>(settingsJson);

    if (settings?.Environment?.ServerName != null)
    {
        serverName = settings.Environment.ServerName;
    }

    if (settings?.Environment?.AllowedEnvironments != null)
    {
        allowedEnvironments = new HashSet<string>(settings.Environment.AllowedEnvironments, StringComparer.OrdinalIgnoreCase);
    }
}
else
{
    Log.Warning("⚠️ settings.json not found. Using defaults.");
}


// Load endpoint configurations from /endpoints folder
var endpointsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");
var endpointMap = new Dictionary<string, (string Url, HashSet<string> Methods)>(StringComparer.OrdinalIgnoreCase);

foreach (var file in Directory.GetFiles(endpointsDirectory, "*.json", SearchOption.AllDirectories))
{
    var json = File.ReadAllText(file);
    var entity = JsonSerializer.Deserialize<EndpointEntity>(json);
    if (entity != null && !string.IsNullOrWhiteSpace(entity.Url) && entity.Methods != null)
    {
        var endpointName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
        endpointMap[endpointName] = (entity.Url, new HashSet<string>(entity.Methods, StringComparer.OrdinalIgnoreCase));

        Log.Information("✅ Loaded endpoint: {Name} -> {Url}", endpointName, entity.Url);
    }
    else
    {
        Log.Warning("⚠️ Failed to load endpoint from {File}", file);
    }
}

app.UseAuthorization();
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Proxy Endpoint with Environment Handling
app.Map("/api/{env}/{**catchall}", async (
    HttpContext context,
    string env,
    string catchall,
    [FromServices] IHttpClientFactory httpClientFactory
) =>
{
    Log.Information("🌍 [{Timestamp}] Received request: {Path} {Method}", DateTime.UtcNow, context.Request.Path, context.Request.Method);

    if (!allowedEnvironments.Contains(env))
    {
        Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { error = $"Environment '{env}' is not allowed." });
        return;
    }

    var match = Regex.Match(catchall, @"^([a-zA-Z0-9_]+)");
    var endpointName = match.Success ? match.Groups[1].Value : "";
   
    var remainingPath = catchall.Length > endpointName.Length
    ? catchall.Substring(endpointName.Length).TrimStart('/')
    : "";

    if (!endpointMap.TryGetValue(endpointName, out var endpointConfig))
    {
        Log.Warning("404 Not Found: {Path}", context.Request.Path);
        context.Response.StatusCode = 404;
        await context.Response.WriteAsJsonAsync(new { error = "Not Found" });
        return;
    }

    if (!endpointConfig.Methods.Contains(context.Request.Method))
    {
        Log.Warning("405 Method Not Allowed: {Method} on {Path}", context.Request.Method, context.Request.Path);
        context.Response.StatusCode = 405;
        await context.Response.WriteAsJsonAsync(new { error = "Method Not Allowed" });
        return;
    }

    var queryString = context.Request.QueryString.Value; // Get the querystring
    var encodedPath = Uri.EscapeDataString(remainingPath);
    if (remainingPath.StartsWith("(") && remainingPath.EndsWith(")"))
    {
        var inner = remainingPath.Substring(1, remainingPath.Length - 2);
        var encodedInner = Uri.EscapeDataString(inner);
        encodedPath = $"({encodedInner})";
    }
    else
    {
        encodedPath = Uri.EscapeDataString(remainingPath);
    }
    var fullUrl = string.IsNullOrEmpty(remainingPath)
        ? $"{endpointConfig.Url}{queryString}"
        : $"{endpointConfig.Url}{(encodedPath.StartsWith("(") ? "" : "/")}{encodedPath}{queryString}";


    var client = httpClientFactory.CreateClient("ProxyClient");

    // Create a new HttpRequestMessage with the same method and target URL
    var requestMessage = new HttpRequestMessage(new HttpMethod(context.Request.Method), fullUrl);

    // Copy the request body for methods that can have body content
    if (HttpMethods.IsPost(context.Request.Method) ||
        HttpMethods.IsPut(context.Request.Method) ||
        HttpMethods.IsPatch(context.Request.Method) ||
        HttpMethods.IsDelete(context.Request.Method) ||
        HttpMethods.IsOptions(context.Request.Method) ||
        string.Equals(context.Request.Method, "MERGE", StringComparison.OrdinalIgnoreCase))
    {
        // Read the request body into a memory stream to preserve it
        var memoryStream = new MemoryStream();
        await context.Request.Body.CopyToAsync(memoryStream);
        memoryStream.Position = 0;
        
        // Set the content with the same data
        requestMessage.Content = new StreamContent(memoryStream);
    }

    // First, copy content-related headers if we have content
    if (requestMessage.Content != null)
    {
        foreach (var header in context.Request.Headers)
        {
            // These headers will go into Content.Headers
            if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                requestMessage.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
    }

    // Copy all other headers
    foreach (var header in context.Request.Headers)
    {
        // Skip content headers as they're already handled
        if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            continue;
            
        // Skip host header as it will be set by HttpClient
        if (header.Key.Equals("Host", StringComparison.OrdinalIgnoreCase))
            continue;
            
        // Skip our custom headers as we'll add them later
        if (header.Key.Equals("DatabaseName", StringComparison.OrdinalIgnoreCase) ||
            header.Key.Equals("ServerName", StringComparison.OrdinalIgnoreCase))
            continue;

        // Try to add the header to the request message
        if (!requestMessage.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
        {
            Log.Debug("Could not add header {HeaderKey} to request", header.Key);
        }
    }

    // Add custom headers
    requestMessage.Headers.Add("DatabaseName", env);
    requestMessage.Headers.Add("ServerName", serverName);

    Log.Information("🔄 Proxying {Method} request from {Path} to {Url}", context.Request.Method, context.Request.Path, fullUrl);
    Log.Debug("Request headers: {Headers}", JsonSerializer.Serialize(requestMessage.Headers.ToDictionary(h => h.Key, h => h.Value)));
    
    try
    {
        using var response = await client.SendAsync(requestMessage);
        
        // Copy response headers to the client response
        foreach (var header in response.Headers)
        {
            context.Response.Headers[header.Key] = header.Value.ToArray();
        }

        // Copy content headers
        if (response.Content != null)
        {
            foreach (var header in response.Content.Headers)
            {
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
        }
        
        context.Response.StatusCode = (int)response.StatusCode;
        
        // Copy the content body to the response
        var originalContent = response.Content != null
            ? await response.Content.ReadAsStringAsync()
            : string.Empty;

        // Parse original URL parts for replacement
        if (!Uri.TryCreate(endpointConfig.Url, UriKind.Absolute, out var originalUri))
        {
            Log.Warning("❌ Could not parse endpointConfig.Url as URI: {Url}", endpointConfig.Url);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "Invalid endpoint URL." });
            return;
        }

        var originalHost = $"{originalUri.Scheme}://{originalUri.Host}:{originalUri.Port}";
        var originalPath = originalUri.AbsolutePath.TrimEnd('/');

        // Proxy path = /api/{env}/{endpoint}
        var proxyHost = $"{context.Request.Scheme}://{context.Request.Host}";
        var proxyPath = $"/api/{env}/{endpointName}";

        // Perform the rewrite (always)
        var rewrittenContent = RewriteUrl(originalContent, originalHost, originalPath, proxyHost, proxyPath);

        // Fix potential Content-Length mismatch
        context.Response.Headers.Remove("Content-Length");

        // Set the content type (fallback if missing)
        context.Response.ContentType = GetSafeContentType(response);

        // Write the rewritten content
        await context.Response.WriteAsync(rewrittenContent);

        Log.Information("✅ Response {StatusCode} received for {Method} request to {Path}", 
            response.StatusCode, context.Request.Method, context.Request.Path);
    }
    catch (Exception ex)
    {
        Log.Error("❌ Error during proxy request: {Error}", ex.Message);
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Internal Server Error", details = ex.Message });
    }
});

string RewriteUrl(string content, string originalHost, string originalPath, string proxyHost, string proxyPath)
{
    if (string.IsNullOrWhiteSpace(content)) return content;

    string originalBaseUrl = $"{originalHost}{originalPath}".TrimEnd('/');
    string proxyBaseUrl = $"{proxyHost}{proxyPath}".TrimEnd('/');

    try
    {
        var xml = XDocument.Parse(content);

        foreach (var element in xml.Descendants())
        {
            // Replace xml:base manually — it's namespaced
            var xmlBaseAttr = element.Attribute(XNamespace.Xml + "base");
            if (xmlBaseAttr != null && xmlBaseAttr.Value.StartsWith(originalBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                xmlBaseAttr.Value = xmlBaseAttr.Value.Replace(originalBaseUrl, proxyBaseUrl, StringComparison.OrdinalIgnoreCase);
            }

            // Rewrite regular attributes (like href, id, etc.)
            foreach (var attr in element.Attributes())
            {
                if (attr.IsNamespaceDeclaration) continue;

                // ✅ Handle xml:base
                if (attr.Name.LocalName == "base" && attr.Name.Namespace == XNamespace.Xml &&
                    attr.Value.StartsWith(originalBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    attr.Value = attr.Value.Replace(originalBaseUrl, proxyBaseUrl, StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                // ✅ Rewrite only if value starts with the original base URL
                if (attr.Value.StartsWith(originalBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    attr.Value = attr.Value.Replace(originalBaseUrl, proxyBaseUrl, StringComparison.OrdinalIgnoreCase);
                }

                // ❌ DO NOT rewrite again if it's already rewritten!
                else if (attr.Value.StartsWith(proxyBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    // Already rewritten — skip
                }

                // ✅ Handle relative hrefs like href="Account(...)"
                else if (attr.Name.LocalName == "href" &&
                        !attr.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                        !attr.Value.StartsWith("/", StringComparison.OrdinalIgnoreCase))
                {
                    // Only prefix once
                    attr.Value = $"{proxyPath}/{attr.Value}".TrimEnd('/');
                }
            }


            // Rewrite element values like <id>
            if (!element.HasElements && element.Value.StartsWith(originalBaseUrl, StringComparison.OrdinalIgnoreCase))
            {
                element.Value = element.Value
                    .Replace(originalBaseUrl, proxyBaseUrl, StringComparison.OrdinalIgnoreCase)
                    .Replace(proxyHost, proxyBaseUrl, StringComparison.OrdinalIgnoreCase);
            }
        }

        return xml.Declaration?.ToString() + Environment.NewLine + xml.ToString(SaveOptions.DisableFormatting);
    }
    catch
    {
        // Fallback to regex (for JSON)
        string escaped = Regex.Escape(originalBaseUrl);
        return Regex.Replace(content, @$"{escaped}(/[^""'\s]*)?", match =>
        {
            var suffix = match.Value.Substring(originalBaseUrl.Length);
            return proxyBaseUrl + suffix;
        }, RegexOptions.IgnoreCase);
    }
}



try
{
    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "❌ Application failed to start.");
}
finally
{
    Log.CloseAndFlush();
}

string GetSafeContentType(HttpResponseMessage response, string fallback = "application/json")
{
    return response.Content?.Headers?.ContentType?.ToString() ?? fallback;
}

public class AuthDbContext : DbContext
{
    public AuthDbContext(DbContextOptions<AuthDbContext> options) : base(options) { }
    public DbSet<AuthToken> Tokens { get; set; }

    public void EnsureTablesCreated()
    {
        Database.ExecuteSqlRaw("CREATE TABLE IF NOT EXISTS Tokens (Id INTEGER PRIMARY KEY AUTOINCREMENT, Token TEXT NOT NULL UNIQUE, CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP)");
    }
}

public class AuthToken
{
    public int Id { get; set; }
    public required string Token { get; set; }
}

public class Settings
{
    public EnvironmentConfig? Environment { get; set; }
}

public class EnvironmentConfig
{
    public string? ServerName { get; set; }
    public List<string>? AllowedEnvironments { get; set; }
}


public class EndpointEntity
{
    public required string Url { get; set; }
    public required List<string> Methods { get; set; }
}