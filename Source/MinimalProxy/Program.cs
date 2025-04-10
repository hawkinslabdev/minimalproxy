using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;
using Serilog;
using Microsoft.OpenApi.Models;
using MinimalProxy.Classes;
using MinimalProxy.Middleware;
using MinimalProxy.Services;
using MinimalProxy.Helpers;

// Create log directory
Directory.CreateDirectory("log");

// Configure logger
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information)
    .WriteTo.File(
        path: "log/minimalproxy-.log",
        rollingInterval: RollingInterval.Day,
        fileSizeLimitBytes: 10 * 1024 * 1024,
        rollOnFileSizeLimit: true,
        retainedFileCountLimit: 10,
        restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Information,
        buffered: true,
        flushToDiskInterval: TimeSpan.FromSeconds(30))
    .MinimumLevel.Information() // Change default from Debug to Information
    .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("System", Serilog.Events.LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", Serilog.Events.LogEventLevel.Warning)
    .Filter.ByExcluding(logEvent => 
        logEvent.Properties.ContainsKey("RequestPath") && 
        (logEvent.Properties["RequestPath"].ToString().Contains("/swagger") ||
         logEvent.Properties["RequestPath"].ToString().Contains("/index.html")))
    .CreateLogger();

Log.Information("✅ Logging initialized successfully");

try
{
    // Create WebApplication Builder
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // Add services
    builder.Services.AddControllers();
    builder.Services.AddEndpointsApiExplorer();

    var config = builder.Configuration;

    // Define server name
    string serverName = Environment.MachineName;

    // Configure Logging
    builder.Logging.ClearProviders();
    builder.Logging.AddConsole(options => options.FormatterName = "simple");
    builder.Logging.AddSimpleConsole(options => options.TimestampFormat = "[yyyy-MM-dd HH:mm:ss] ");

    // Configure SQLite Authentication Database
    var dbPath = Path.Combine(Directory.GetCurrentDirectory(), "auth.db");
    builder.Services.AddDbContext<AuthDbContext>(options => options.UseSqlite($"Data Source={dbPath}"));
    builder.Services.AddScoped<TokenService>();
    builder.Services.AddAuthorization();
    builder.Services.AddHostedService<LogFlusher>();

    // Configure HTTP client
    builder.Services.AddHttpClient("ProxyClient")
        .ConfigurePrimaryHttpMessageHandler(() =>
        {
            return new HttpClientHandler
            {
                UseDefaultCredentials = true, // Uses Windows credentials of the application
                PreAuthenticate = true
            };
        });

    // Register EnvironmentSettings
    builder.Services.AddSingleton<EnvironmentSettings>();

    // Load endpoints
    var endpointsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");
    var endpointMap = EndpointHandler.GetEndpoints(endpointsDirectory);

    // Create sample endpoints if directory is empty
    if (!endpointMap.Any())
    {
        EndpointHandler.CreateSampleEndpoints(endpointsDirectory);
        // Reload endpoints after creating samples
        endpointMap = EndpointHandler.GetEndpoints(endpointsDirectory);
    }

    // Register CompositeEndpointHandler with loaded endpoints
    builder.Services.AddSingleton<CompositeEndpointHandler>(provider => 
        new CompositeEndpointHandler(
            provider.GetRequiredService<IHttpClientFactory>(),
            endpointMap,
            serverName
        )
    );

    // Configure Swagger
    var swaggerSettings = SwaggerConfiguration.ConfigureSwagger(builder);
    builder.Services.AddSingleton<DynamicEndpointDocumentFilter>();
    builder.Services.AddSingleton<DynamicEndpointOperationFilter>();
    builder.Services.AddSingleton<CompositeEndpointDocumentFilter>();

    // Build the application
    var app = builder.Build();

    // Configure middleware pipeline
    app.UseExceptionHandlingMiddleware();
    app.UseDefaultFiles(new DefaultFilesOptions
    {
        DefaultFileNames = new List<string> { "index.html" }
    });
    app.UseStaticFiles();

    // Configure Swagger
    app.UseSwagger(options =>
    {
        options.RouteTemplate = "/openapi/{documentName}.json";
    });
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "MinimalProxy API v1");
        c.RoutePrefix = "swagger";
        c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
        c.DefaultModelsExpandDepth(-1);
        c.DisplayRequestDuration();
        c.EnableFilter();
        c.EnableDeepLinking();
        c.EnableValidator();
    });

    app.UseSwagger();

    // Initialize Database & Create Default Token if needed
    using (var scope = app.Services.CreateScope())
    {
        var context = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

        try
        {
            // Set up database and migrate if needed
            context.Database.EnsureCreated();
            context.EnsureTablesCreated();

            // Create a default token if none exist
            var activeTokens = await tokenService.GetActiveTokensAsync();
            if (!activeTokens.Any())
            {
                var token = await tokenService.GenerateTokenAsync(serverName);
                Log.Information("🗝️ Generated new default token for {ServerName}", serverName);
                Log.Information("📁 Token has been saved to tokens/{ServerName}.txt", serverName);
            }
            else
            {
                Log.Information("✅ Using existing tokens. Total active tokens: {Count}", activeTokens.Count());
                Log.Information("📁 Tokens are available in the tokens directory");
            }
        }
        catch (Exception ex)
        {
            Log.Error("❌ Database initialization failed: {Message}", ex.Message);
        }
    }

    // Get the environment settings service
    var environmentSettings = app.Services.GetRequiredService<EnvironmentSettings>();

    // Log loaded endpoints
    foreach (var entry in endpointMap)
    {
        string endpointName = entry.Key;
        var (url, methods, isPrivate, type) = entry.Value;
        
        if (isPrivate)
        {
            Log.Information($"🔒 Private Endpoint: {endpointName}; Proxy URL: {url}, Methods: {string.Join(", ", methods)}");
        }
        else if (type.Equals("Composite", StringComparison.OrdinalIgnoreCase))
        {
            Log.Information($"🧩 Composite Endpoint: {endpointName}; Proxy URL: {url}, Methods: {string.Join(", ", methods)}");
        }
        else
        {
            Log.Information($"✅ Endpoint: {endpointName}; Proxy URL: {url}, Methods: {string.Join(", ", methods)}");
        }
    }

    // Authentication middleware
    app.Use(async (context, next) =>
    {
        Log.Debug("Request Path: {Path}", context.Request.Path);

        var path = context.Request.Path.Value?.ToLowerInvariant();
        
        // Skip token validation for Swagger routes
        if (path != null && (
            path.StartsWith("/swagger") || 
            path == "/" ||
            path == "/index.html") ||
            context.Request.Path.StartsWithSegments("/favicon.ico")
            )
        {
            await next();
            return;
        }
        
        // Continue with authentication logic
        Log.Information("🔀 [{Timestamp}] Incoming request: {Path}", DateTime.UtcNow, context.Request.Path);

        if (!context.Request.Headers.TryGetValue("Authorization", out var providedToken))
        {
            Log.Warning("❌ [{Timestamp}] Authorization header missing.",  DateTime.UtcNow);
            context.Response.StatusCode = 401;
            await context.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }

        string tokenString = providedToken.ToString();
        
        // Extract the token from "Bearer token"
        if (tokenString.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            tokenString = tokenString.Substring("Bearer ".Length).Trim();
        }

        using var scope = app.Services.CreateScope();
        var tokenService = scope.ServiceProvider.GetRequiredService<TokenService>();

        // Validate token
        bool isValid = await tokenService.VerifyTokenAsync(tokenString);

        if (!isValid)
        {
            Log.Warning("❌ [{Timestamp}] Invalid token provided",  DateTime.UtcNow);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden" });
            return;
        }

        Log.Information("✅ [{Timestamp}] Authorized request with valid token",  DateTime.UtcNow);
        await next();
    });

    app.UseAuthorization();
    app.UseForwardedHeaders(new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    });

    // Proxy endpoint with environment handling
    app.Map("/api/{env}/{**catchall}", async (
        HttpContext context,
        string env,
        string? catchall,
        [FromServices] IHttpClientFactory httpClientFactory
    ) =>
    {
        Log.Information("🌍 [{Timestamp}] Received request: {Path} {Method}", DateTime.UtcNow, context.Request.Path, context.Request.Method);

        try
        {
            // Handle case where catchall is null (root level endpoint)
            catchall = catchall ?? "";

            if (!environmentSettings.IsEnvironmentAllowed(env))
            {
                Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = $"Environment '{env}' is not allowed." });
                return;
            }

            // Extract endpoint name from the beginning of catchall
            var endpointName = "";
            var remainingPath = "";
            
            var match = Regex.Match(catchall, @"^([a-zA-Z0-9_]+)");
            if (match.Success)
            {
                endpointName = match.Groups[1].Value;
                remainingPath = catchall.Length > endpointName.Length
                    ? catchall.Substring(endpointName.Length).TrimStart('/')
                    : "";
            }
            else if (string.IsNullOrEmpty(catchall))
            {
                // For URLs like /api/env/ without an endpoint specified
                Log.Warning("❌ Missing endpoint name in request: {Path}", context.Request.Path);
                context.Response.StatusCode = 400;
                await context.Response.WriteAsJsonAsync(new { error = "Missing endpoint name" });
                return;
            }

        if (!endpointMap.TryGetValue(endpointName, out var endpointConfig))
        {
            Log.Warning("404 Not Found: {Path}", context.Request.Path);
            context.Response.StatusCode = 404;
            await context.Response.WriteAsJsonAsync(new { error = "Not Found" });
            return;
        }

        // Check if the endpoint is private or composite (internal only)
        if (endpointConfig.IsPrivate || endpointConfig.Type.Equals("Composite", StringComparison.OrdinalIgnoreCase))
        {
            Log.Warning("403 Forbidden: Attempt to access private/internal endpoint: {Path}", context.Request.Path);
            context.Response.StatusCode = 403;
            await context.Response.WriteAsJsonAsync(new { error = "Endpoint not accessible directly" });
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
                ? $"{endpointConfig.Item1}{queryString}"
                : $"{endpointConfig.Item1}{(encodedPath.StartsWith("(") ? "" : "/")}{encodedPath}{queryString}";

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
            if (!Uri.TryCreate(endpointConfig.Item1, UriKind.Absolute, out var originalUri))
            {
                Log.Warning("❌ Could not parse endpoint URL as URI: {Url}", endpointConfig.Item1);
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
            var rewrittenContent = UrlHelper.RewriteUrl(originalContent, originalHost, originalPath, proxyHost, proxyPath);

            // Fix potential Content-Length mismatch
            context.Response.Headers.Remove("Content-Length");

            // Set the content type (fallback if missing)
            context.Response.ContentType = UrlHelper.GetSafeContentType(response);

            // Write the rewritten content
            await context.Response.WriteAsync(rewrittenContent);

            Log.Information("✅ Response {StatusCode} received for {Method} request to {Path}", 
                response.StatusCode, context.Request.Method, context.Request.Path);
        }
        catch (Exception ex)
        {
            Log.Error("❌ Error during proxy request: {Error}", ex.Message);
            context.Response.StatusCode = 500;
            await context.Response.WriteAsJsonAsync(new { error = "Internal Server Error" });
        }
    });

    // Composite endpoint handler
    app.Map("/api/{env}/composite/{endpointName}", async (
        HttpContext context,
        string env,
        string endpointName,
        [FromServices] CompositeEndpointHandler compositeHandler
    ) =>
    {
        Log.Information("🧩 [{Timestamp}] Received composite request: {Path} {Method}", 
            DateTime.UtcNow, context.Request.Path, context.Request.Method);

        try
        {
            // Check environment
            if (!environmentSettings.IsEnvironmentAllowed(env))
            {
                Log.Warning("❌ Environment '{Env}' is not in the allowed list.", env);
                return Results.BadRequest(new { error = $"Environment '{env}' is not allowed." });
            }

            // Read the request body
            string requestBody;
            using (var reader = new StreamReader(context.Request.Body))
            {
                requestBody = await reader.ReadToEndAsync();
            }
            
            // Process the composite endpoint
            return await compositeHandler.ProcessCompositeEndpointAsync(context, env, endpointName, requestBody);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error processing composite endpoint: {Error}", ex.Message);
            return Results.Problem(
                detail: ex.Message,
                title: "Internal Server Error",
                statusCode: 500
            );
        }
    });

    // Log application URLs
    var urls = app.Urls;
    if (urls != null && urls.Any())
    {
        Log.Information("🌐 Application is hosted on the following URLs:");
        foreach (var url in urls)
        {
            Log.Information("   {Url}", url);
        }
    }
    else
    {
        var serverUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS") 
            ?? builder.Configuration["Kestrel:Endpoints:Http:Url"] 
            ?? builder.Configuration["urls"]
            ?? "http://localhost:5000";
        
        Log.Information("🌐 Application is hosted on: {Urls}", serverUrls);
    }

    // Register application shutdown handler
    app.Lifetime.ApplicationStopping.Register(() => 
    {
        Log.Information("Application shutting down...");
        Log.CloseAndFlush();
    });

    // Run the application
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

