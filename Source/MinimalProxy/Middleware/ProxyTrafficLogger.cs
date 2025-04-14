namespace MinimalProxy.Middleware;

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MinimalProxy.Classes;
using Serilog;


/// <summary>
/// Configuration options for the proxy traffic logging system
/// </summary>
public class ProxyTrafficLoggerOptions
{
    /// <summary>
    /// Whether the proxy traffic logging is enabled
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Maximum number of log entries to keep in memory queue
    /// </summary>
    public int QueueCapacity { get; set; } = 10000;

    /// <summary>
    /// Storage type: "sqlite" or "file"
    /// </summary>
    public string StorageType { get; set; } = "file";

    /// <summary>
    /// Path to the SQLite database file (if using SQLite storage)
    /// </summary>
    public string SqlitePath { get; set; } = "log/traffic_logs.db";

    /// <summary>
    /// Directory path for log files (if using file storage)
    /// </summary>
    public string LogDirectory { get; set; } = "log/traffic";

    /// <summary>
    /// Maximum size of a single log file in MB before rolling (if using file storage)
    /// </summary>
    public int MaxFileSizeMB { get; set; } = 50;

    /// <summary>
    /// Maximum number of log files to keep (if using file storage)
    /// </summary>
    public int MaxFileCount { get; set; } = 10;

    /// <summary>
    /// Log file prefix (if using file storage)
    /// </summary>
    public string FilePrefix { get; set; } = "proxy_traffic_";

    /// <summary>
    /// Batch size for writing logs
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Max time in milliseconds to wait before flushing a batch of logs
    /// </summary>
    public int FlushIntervalMs { get; set; } = 1000;
    
    /// <summary>
    /// Whether to include request bodies in logs
    /// </summary>
    public bool IncludeRequestBodies { get; set; } = false;
    
    /// <summary>
    /// Whether to include response bodies in logs
    /// </summary>
    public bool IncludeResponseBodies { get; set; } = false;
    
    /// <summary>
    /// Maximum size in bytes to capture for request/response bodies
    /// </summary>
    public int MaxBodyCaptureSizeBytes { get; set; } = 4096;
    
    /// <summary>
    /// Whether to capture request headers
    /// </summary>
    public bool CaptureHeaders { get; set; } = true;
    
    /// <summary>
    /// Whether to log info-level messages to Serilog for each request
    /// </summary>
    public bool EnableInfoLogging { get; set; } = true;
}

/// <summary>
/// Represents a proxy traffic log entry
/// </summary>
public class ProxyTrafficLogEntry
{
    public long Id { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string QueryString { get; set; } = string.Empty;
    public string Environment { get; set; } = string.Empty;
    public string EndpointName { get; set; } = string.Empty;
    public string TargetUrl { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public long RequestSize { get; set; }
    public long ResponseSize { get; set; }
    public int DurationMs { get; set; }
    public string? Username { get; set; }
    public string ClientIp { get; set; } = string.Empty;
    public string? RequestBody { get; set; }
    public string? ResponseBody { get; set; }
    public string TraceId { get; set; } = string.Empty;
    public Dictionary<string, string> RequestHeaders { get; set; } = new Dictionary<string, string>();
}

/// <summary>
/// Interface for log storage providers
/// </summary>
public interface ITrafficLogStorage
{
    Task InitializeAsync();
    Task SaveLogsAsync(IEnumerable<ProxyTrafficLogEntry> logs);
}

/// <summary>
/// SQLite implementation of log storage
/// </summary>
public class SqliteTrafficLogStorage : ITrafficLogStorage
{
    private readonly ProxyTrafficLoggerOptions _options;
    private readonly string _connectionString;

    public SqliteTrafficLogStorage(IOptions<ProxyTrafficLoggerOptions> options)
    {
        _options = options.Value;
        _connectionString = $"Data Source={_options.SqlitePath};Cache=Shared";
    }

    public async Task InitializeAsync()
    {
        try
        {
            // Ensure the directory exists
            var directory = Path.GetDirectoryName(_options.SqlitePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create the database and table if they don't exist
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS ProxyTrafficLogs (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    TraceId TEXT NOT NULL,
                    Timestamp TEXT NOT NULL,
                    Method TEXT NOT NULL,
                    Path TEXT NOT NULL,
                    QueryString TEXT,
                    Environment TEXT NOT NULL,
                    EndpointName TEXT NOT NULL,
                    TargetUrl TEXT NOT NULL,
                    StatusCode INTEGER NOT NULL,
                    RequestSize INTEGER NOT NULL,
                    ResponseSize INTEGER NOT NULL,
                    DurationMs INTEGER NOT NULL,
                    Username TEXT,
                    ClientIp TEXT NOT NULL,
                    RequestBody TEXT,
                    ResponseBody TEXT,
                    RequestHeaders TEXT
                );
                CREATE INDEX IF NOT EXISTS idx_traffic_logs_timestamp ON ProxyTrafficLogs(Timestamp);
                CREATE INDEX IF NOT EXISTS idx_traffic_logs_traceid ON ProxyTrafficLogs(TraceId);
                CREATE INDEX IF NOT EXISTS idx_traffic_logs_endpoint ON ProxyTrafficLogs(EndpointName);
            ";
            
            await command.ExecuteNonQueryAsync();
            Log.Information("✅ SQLite traffic log storage initialized at {Path}", _options.SqlitePath);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error initializing SQLite traffic log storage");
            throw;
        }
    }

    public async Task SaveLogsAsync(IEnumerable<ProxyTrafficLogEntry> logs)
    {
        try
        {
            using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync();
            
            using var transaction = connection.BeginTransaction();
            
            foreach (var log in logs)
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO ProxyTrafficLogs (
                        TraceId, Timestamp, Method, Path, QueryString, Environment, EndpointName, 
                        TargetUrl, StatusCode, RequestSize, ResponseSize, DurationMs, 
                        Username, ClientIp, RequestBody, ResponseBody, RequestHeaders
                    ) VALUES (
                        @TraceId, @Timestamp, @Method, @Path, @QueryString, @Environment, @EndpointName,
                        @TargetUrl, @StatusCode, @RequestSize, @ResponseSize, @DurationMs,
                        @Username, @ClientIp, @RequestBody, @ResponseBody, @RequestHeaders
                    )
                ";
                
                command.Parameters.AddWithValue("@TraceId", log.TraceId);
                command.Parameters.AddWithValue("@Timestamp", log.Timestamp.ToString("o"));
                command.Parameters.AddWithValue("@Method", log.Method);
                command.Parameters.AddWithValue("@Path", log.Path);
                command.Parameters.AddWithValue("@QueryString", log.QueryString ?? string.Empty);
                command.Parameters.AddWithValue("@Environment", log.Environment);
                command.Parameters.AddWithValue("@EndpointName", log.EndpointName);
                command.Parameters.AddWithValue("@TargetUrl", log.TargetUrl);
                command.Parameters.AddWithValue("@StatusCode", log.StatusCode);
                command.Parameters.AddWithValue("@RequestSize", log.RequestSize);
                command.Parameters.AddWithValue("@ResponseSize", log.ResponseSize);
                command.Parameters.AddWithValue("@DurationMs", log.DurationMs);
                command.Parameters.AddWithValue("@Username", log.Username ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@ClientIp", log.ClientIp);
                command.Parameters.AddWithValue("@RequestBody", log.RequestBody ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@ResponseBody", log.ResponseBody ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@RequestHeaders", JsonSerializer.Serialize(log.RequestHeaders));
                
                await command.ExecuteNonQueryAsync();
            }
            
            transaction.Commit();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error saving traffic logs to SQLite");
            throw;
        }
    }
}

/// <summary>
/// File-based implementation of log storage
/// </summary>
public class FileTrafficLogStorage : ITrafficLogStorage
{
    private readonly ProxyTrafficLoggerOptions _options;
    private string _currentLogFile;
    private long _currentFileSize;
    private readonly object _fileLock = new object();

    public FileTrafficLogStorage(IOptions<ProxyTrafficLoggerOptions> options)
    {
        _options = options.Value;
        _currentLogFile = GenerateLogFileName();
        _currentFileSize = 0;
    }

    public Task InitializeAsync()
    {
        try
        {
            // Ensure log directory exists
            if (!Directory.Exists(_options.LogDirectory))
            {
                Directory.CreateDirectory(_options.LogDirectory);
                Log.Information("✅ Created traffic log directory: {Directory}", _options.LogDirectory);
            }

            // Check if the current log file exists and get its size
            if (File.Exists(_currentLogFile))
            {
                var fileInfo = new FileInfo(_currentLogFile);
                _currentFileSize = fileInfo.Length;
            }

            // Delete old log files if needed
            CleanupOldLogFiles();

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error initializing file storage for traffic logs");
            throw;
        }
    }

    public Task SaveLogsAsync(IEnumerable<ProxyTrafficLogEntry> logs)
    {
        try
        {
            var linesToWrite = new List<string>();
            
            foreach (var log in logs)
            {
                // Use JSON format for all details
                string logJson = JsonSerializer.Serialize(log, new JsonSerializerOptions
                {
                    WriteIndented = false
                });
                
                linesToWrite.Add(logJson);
            }

            // Write to file with lock to prevent concurrent access issues
            lock (_fileLock)
            {
                // Check if we need to roll over to a new file
                CheckRolloverNeeded(linesToWrite);
                
                // Append to the current log file
                File.AppendAllLines(_currentLogFile, linesToWrite);
                
                // Update current file size
                _currentFileSize += linesToWrite.Sum(l => Encoding.UTF8.GetByteCount(l) + Environment.NewLine.Length);
            }

            return Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error saving traffic logs to file");
            throw;
        }
    }

    private void CheckRolloverNeeded(List<string> linesToWrite)
    {
        // Calculate the size of the lines we're about to write
        long batchSize = linesToWrite.Sum(l => Encoding.UTF8.GetByteCount(l) + Environment.NewLine.Length);
        
        // Check if adding these lines would exceed the max file size
        if (_currentFileSize + batchSize > _options.MaxFileSizeMB * 1024 * 1024)
        {
            // Roll over to a new file
            _currentLogFile = GenerateLogFileName();
            _currentFileSize = 0;
            
            // Clean up old files
            CleanupOldLogFiles();
        }
    }

    private string GenerateLogFileName()
    {
        return Path.Combine(
            _options.LogDirectory,
            $"{_options.FilePrefix}{DateTime.UtcNow:yyyyMMdd_HHmmss}.json"
        );
    }

    private void CleanupOldLogFiles()
    {
        try
        {
            var logFiles = Directory.GetFiles(_options.LogDirectory, $"{_options.FilePrefix}*.json")
                .OrderByDescending(f => f)
                .ToList();
            
            // Keep only the most recent MaxFileCount files
            if (logFiles.Count > _options.MaxFileCount)
            {
                foreach (var file in logFiles.Skip(_options.MaxFileCount))
                {
                    File.Delete(file);
                    Log.Debug("🗑️ Deleted old traffic log file: {File}", file);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error cleaning up old traffic log files");
        }
    }
}

/// <summary>
/// Background service that processes log entries from the queue
/// </summary>
public class ProxyTrafficLoggerService : BackgroundService
{
    private readonly Channel<ProxyTrafficLogEntry> _logChannel;
    private readonly ProxyTrafficLoggerOptions _options;
    private readonly ITrafficLogStorage _logStorage;

    public ProxyTrafficLoggerService(
        Channel<ProxyTrafficLogEntry> logChannel, 
        IOptions<ProxyTrafficLoggerOptions> options,
        ITrafficLogStorage logStorage)
    {
        _logChannel = logChannel;
        _options = options.Value;
        _logStorage = logStorage;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Debug("🔍 Proxy Traffic Logger Service started");

        try
        {
            await _logStorage.InitializeAsync();
            Log.Information($"🔍 You've enabled traffic tracing with storage type: {_options.StorageType}!");

            var batch = new List<ProxyTrafficLogEntry>(_options.BatchSize);
            var flushInterval = TimeSpan.FromMilliseconds(_options.FlushIntervalMs);

            while (!stoppingToken.IsCancellationRequested)
            {
                bool itemsProcessed = false;
                
                // Read up to BatchSize items without blocking
                while (batch.Count < _options.BatchSize)
                {
                    if (_logChannel.Reader.TryRead(out var logEntry) && logEntry != null)
                    {
                        batch.Add(logEntry);
                    }
                    else
                    {
                        // No more items in the queue
                        break;
                    }
                }

                // If we have items, process them
                if (batch.Count > 0)
                {
                    await _logStorage.SaveLogsAsync(batch);
                    Log.Debug($"🔍 Processed {batch.Count} traffic log entries");
                    batch.Clear();
                    itemsProcessed = true;
                }
                
                // If no items were processed, wait for more data or the flush interval
                if (!itemsProcessed)
                {
                    try
                    {
                        // Use a cancellation token source with timeout
                        using var timeoutCts = new CancellationTokenSource(flushInterval);
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, stoppingToken);
                        
                        // Wait for an item or timeout
                        await _logChannel.Reader.WaitToReadAsync(linkedCts.Token).AsTask();
                    }
                    catch (OperationCanceledException)
                    {
                        // This is expected - either timeout or cancellation
                        if (stoppingToken.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error in Proxy Traffic Logger Service");
        }
        finally
        {
            Log.Information("🔍 Proxy Traffic Logger Service stopping...");
            
            // Flush any remaining logs before shutdown
            try
            {
                List<ProxyTrafficLogEntry> remainingLogs = new();
                while (_logChannel.Reader.TryRead(out var log))
                {
                    remainingLogs.Add(log);
                }
                
                if (remainingLogs.Count > 0)
                {
                    await _logStorage.SaveLogsAsync(remainingLogs);
                    Log.Information($"🔍 Flushed {remainingLogs.Count} remaining traffic log entries on shutdown");
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "❌ Error flushing traffic logs on shutdown");
            }
        }
    }
}

/// <summary>
/// Middleware for logging proxy traffic
/// </summary>
public class ProxyTrafficLoggerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ProxyTrafficLoggerOptions _options;
    private readonly Channel<ProxyTrafficLogEntry> _logChannel;
    private readonly IServiceProvider _serviceProvider;
    
    // Pre-define list of sensitive headers - using static readonly for better performance
    private static readonly string[] _sensitiveHeaders = new[]
    {
        "Authorization", "Cookie", "X-API-Key", "API-Key", "Password",
        "X-Auth-Token", "Token", "Secret", "Credential", "Access-Token", 
        "X-Access-Token"
    };

    public ProxyTrafficLoggerMiddleware(
        RequestDelegate next, 
        IOptions<ProxyTrafficLoggerOptions> options,
        Channel<ProxyTrafficLogEntry> logChannel,
        IServiceProvider serviceProvider)
    {
        _next = next;
        _options = options.Value;
        _logChannel = logChannel;
        _serviceProvider = serviceProvider;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Skip logging if not enabled or if this is a non-API request
        if (!_options.Enabled || !IsApiRequest(context))
        {
            await _next(context);
            return;
        }

        var traceId = Guid.NewGuid().ToString("N").Substring(0, 8);

        // Prepare log entry
        var logEntry = new ProxyTrafficLogEntry
        {
            Timestamp = DateTime.UtcNow,
            Method = context.Request.Method,
            Path = context.Request.Path.Value ?? string.Empty,
            QueryString = context.Request.QueryString.Value ?? string.Empty,
            ClientIp = GetClientIpAddress(context),
            TraceId = traceId
        };

        // Extract environment and endpoint from path
        ParseApiPath(context.Request.Path.Value, out string? env, out string? endpoint);
        logEntry.Environment = env ?? string.Empty;
        logEntry.EndpointName = endpoint ?? string.Empty;
        
        // Log at debug level only
        Log.Debug($"🔍 [Trace: {traceId}] Processing {context.Request.Method} request to {context.Request.Path}");

        // Extract target URL if available in Items
        if (context.Items.TryGetValue("TargetUrl", out var targetUrl) && targetUrl != null)
        {
            logEntry.TargetUrl = targetUrl.ToString() ?? string.Empty;
        }
        
        // Capture request headers if enabled
        if (_options.CaptureHeaders)
        {
            CaptureRequestHeaders(context, logEntry);
        }

        // Setup for request body capture
        var originalRequestBody = context.Request.Body;
        MemoryStream? requestBodyStream = null;

        // Setup for response capture - only do this if needed
        Stream? originalResponseBodyStream = null;
        MemoryStream? responseBodyStream = null;
        
        if (_options.IncludeResponseBodies)
        {
            originalResponseBodyStream = context.Response.Body;
            responseBodyStream = new MemoryStream();
            context.Response.Body = responseBodyStream;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Capture request body if enabled
            if (_options.IncludeRequestBodies)
            {
                // Create a new MemoryStream to capture the request body
                requestBodyStream = new MemoryStream();
                
                // Enable buffering to allow multiple reads
                context.Request.EnableBuffering();
                
                // Copy the original request body to our memory stream
                await context.Request.Body.CopyToAsync(requestBodyStream);
                
                // Reset the memory stream position to the beginning
                requestBodyStream.Position = 0;
                
                // Read the request body for logging
                using (var reader = new StreamReader(requestBodyStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                {
                    var requestBody = await reader.ReadToEndAsync();
                    
                    // Truncate the body if it's too large
                    if (requestBody.Length > _options.MaxBodyCaptureSizeBytes)
                    {
                        logEntry.RequestBody = requestBody.Substring(0, _options.MaxBodyCaptureSizeBytes) + "...";
                    }
                    else
                    {
                        logEntry.RequestBody = requestBody;
                    }
                }
                
                // Record the size
                logEntry.RequestSize = requestBodyStream.Length;
                
                // Reset the position for the next middleware
                requestBodyStream.Position = 0;
                
                // Reset the original request body position
                context.Request.Body.Position = 0;
            }
            else
            {
                // Just record content length
                logEntry.RequestSize = context.Request.ContentLength ?? 0;
            }

            // Extract username from the token in the Authorization header
            await ExtractUsernameFromTokenAsync(context, logEntry);

            // Call the next middleware in the pipeline
            await _next(context);

            // Record duration
            stopwatch.Stop();
            logEntry.DurationMs = (int)stopwatch.ElapsedMilliseconds;
            
            // Capture response details
            logEntry.StatusCode = context.Response.StatusCode;
            
            // Capture response body if enabled
            if (_options.IncludeResponseBodies && responseBodyStream != null && originalResponseBodyStream != null)
            {
                responseBodyStream.Position = 0;
                logEntry.ResponseSize = responseBodyStream.Length;
                
                // Only capture for specific content types
                var contentType = context.Response.ContentType?.ToLowerInvariant() ?? string.Empty;
                if ((contentType.Contains("json") || contentType.Contains("xml")) && responseBodyStream.Length > 0)
                {
                    // Read the response body for logging
                    using (var reader = new StreamReader(responseBodyStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
                    {
                        var responseBody = await reader.ReadToEndAsync();
                        
                        // Truncate the body if it's too large
                        if (responseBody.Length > _options.MaxBodyCaptureSizeBytes)
                        {
                            logEntry.ResponseBody = responseBody.Substring(0, _options.MaxBodyCaptureSizeBytes) + "...";
                        }
                        else
                        {
                            logEntry.ResponseBody = responseBody;
                        }
                    }
                    
                    // Reset the position for copying to the original stream
                    responseBodyStream.Position = 0;
                }
                
                // Copy the captured response to the original stream
                await responseBodyStream.CopyToAsync(originalResponseBodyStream);
            }
            else if (responseBodyStream != null && originalResponseBodyStream != null)
            {
                // Just record the size
                logEntry.ResponseSize = responseBodyStream.Length;
                
                // Copy the captured response to the original stream
                responseBodyStream.Position = 0;
                await responseBodyStream.CopyToAsync(originalResponseBodyStream);
            }
            
            // Log with Serilog for immediate visibility
            if (_options.EnableInfoLogging)
            {
                Log.Information($"🔍 [Trace: {traceId}] {logEntry.Method} {logEntry.Path} -> {logEntry.StatusCode} ({logEntry.DurationMs}ms)");
            }
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            logEntry.DurationMs = (int)stopwatch.ElapsedMilliseconds;
            logEntry.StatusCode = 500;  // Internal Server Error
            
            Log.Error(ex, $"❌ [Trace: {traceId}] Error during proxy request processing");
            
            // Re-throw the exception
            throw;
        }
        finally
        {
            // Restore the original request body if we changed it
            if (requestBodyStream != null)
            {
                context.Request.Body = originalRequestBody;
                await requestBodyStream.DisposeAsync();
            }
            
            // Restore the original response body stream if we changed it
            if (originalResponseBodyStream != null && responseBodyStream != null)
            {
                context.Response.Body = originalResponseBodyStream;
                await responseBodyStream.DisposeAsync();
            }
            
            // Try to add the log entry to the channel
            if (!_logChannel.Writer.TryWrite(logEntry))
            {
                Log.Warning($"❌ [Trace: {traceId}] Failed to write traffic log entry to channel - queue might be full");
            }
        }
    }

    private bool IsApiRequest(HttpContext context)
    {
        var path = context.Request.Path.Value;
        return path != null && 
                path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase) && 
                !path.Contains("/swagger", StringComparison.OrdinalIgnoreCase) && 
                !path.Contains("index.html", StringComparison.OrdinalIgnoreCase);
    }
    
    private void ParseApiPath(string? path, out string? env, out string? endpoint)
    {
        env = null;
        endpoint = null;
        
        if (string.IsNullOrEmpty(path))
            return;
        
        // Extract segments
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        
        if (segments.Length >= 2 && segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            // Set environment
            env = segments[1];
            
            // Set endpoint if available
            if (segments.Length >= 3)
            {
                endpoint = segments[2];
                
                // Handle composite endpoints
                if (endpoint.Equals("composite", StringComparison.OrdinalIgnoreCase) && segments.Length >= 4)
                {
                    endpoint = $"composite/{segments[3]}";
                }
            }
        }
    }
    
    private async Task ExtractUsernameFromTokenAsync(HttpContext context, ProxyTrafficLogEntry logEntry)
    {
        try
        {
            // Try to get from User Identity first
            logEntry.Username = context.User?.Identity?.Name;
            
            // If not available, check Authorization header
            if (string.IsNullOrEmpty(logEntry.Username) && 
                context.Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                string token = authHeader.ToString();
                if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                {
                    // Extract the actual token value
                    token = token.Substring("Bearer ".Length).Trim();
                    
                    // Use the token service to get the username for this token
                    using var scope = _serviceProvider.CreateScope();
                    var tokenService = scope.ServiceProvider.GetService<TokenService>();
                    
                    if (tokenService != null)
                    {
                        // Get active tokens
                        var tokens = await tokenService.GetActiveTokensAsync();
                        
                        // Check each token - we need to use VerifyTokenAsync because the token is hashed
                        foreach (var activeToken in tokens)
                        {
                            // Verify if this token belongs to this user
                            bool isValid = await tokenService.VerifyTokenAsync(token, activeToken.Username);
                            if (isValid)
                            {
                                logEntry.Username = activeToken.Username;
                                break;
                            }
                        }
                        
                        // If we couldn't find a username but the token is valid, use a generic name
                        if (string.IsNullOrEmpty(logEntry.Username) && await tokenService.VerifyTokenAsync(token))
                        {
                            logEntry.Username = "authenticated-user";
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"❌ [Trace: {logEntry.TraceId}] Error extracting username from token");
            logEntry.Username = "error-extracting-user";
        }
    }

    private void CaptureRequestHeaders(HttpContext context, ProxyTrafficLogEntry logEntry)
    {
        try
        {
            var headers = context.Request.Headers;
            
            // Pre-allocate dictionary capacity
            logEntry.RequestHeaders = new Dictionary<string, string>(headers.Count, StringComparer.OrdinalIgnoreCase);
            
            foreach (var header in headers)
            {
                string headerName = header.Key;
                string headerValue = header.Value.ToString();
                
                // Check if this is a sensitive header
                bool isSensitive = false;
                foreach (var sensitiveHeader in _sensitiveHeaders)
                {
                    if (string.Equals(headerName, sensitiveHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        isSensitive = true;
                        break;
                    }
                }
                
                // Add to dictionary with appropriate value
                logEntry.RequestHeaders[headerName] = isSensitive ? "[REDACTED]" : headerValue;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, $"❌ [Trace: {logEntry.TraceId}] Error capturing request headers");
        }
    }

    private string GetClientIpAddress(HttpContext context)
    {
        // Try to get the forwarded IP first
        string? ip = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        
        // If not available, use the connection remote IP
        if (string.IsNullOrEmpty(ip))
        {
            ip = context.Connection.RemoteIpAddress?.ToString();
        }
        
        return ip ?? "unknown";
    }
}

/// <summary>
/// Extension methods for setting up proxy traffic logging
/// </summary>
public static class ProxyTrafficLoggerExtensions
{
    /// <summary>
    /// Adds proxy traffic logging services to the service collection
    /// </summary>
    public static IServiceCollection AddProxyTrafficLogging(this IServiceCollection services, IConfiguration configuration)
    {
        // Bind options from configuration
        var optionsSection = configuration.GetSection("ProxyTrafficLogging");
        services.Configure<ProxyTrafficLoggerOptions>(optionsSection);
        
        // Get options for setup
        var options = optionsSection.Get<ProxyTrafficLoggerOptions>() ?? new ProxyTrafficLoggerOptions();
        
        // Only register services if enabled
        if (options.Enabled)
        {
            Log.Information("🔍 Traffic logging initialized succesfully, using {StorageType} storage", options.StorageType);
            
            // Create bounded channel with specified capacity
            services.AddSingleton(_ => Channel.CreateBounded<ProxyTrafficLogEntry>(
                new BoundedChannelOptions(options.QueueCapacity)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                    SingleWriter = false
                }));
            
            // Register the appropriate storage implementation
            if (string.Equals(options.StorageType, "sqlite", StringComparison.OrdinalIgnoreCase))
            {
                services.AddSingleton<ITrafficLogStorage, SqliteTrafficLogStorage>();
            }
            else
            {
                services.AddSingleton<ITrafficLogStorage, FileTrafficLogStorage>();
            }
            
            // Register the background service
            services.AddHostedService<ProxyTrafficLoggerService>();
        }
        
        return services;
    }

    /// <summary>
    /// Adds the proxy traffic logging middleware to the application pipeline
    /// </summary>
    public static IApplicationBuilder UseProxyTrafficLogging(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetService<IOptions<ProxyTrafficLoggerOptions>>();
        
        // Only use middleware if enabled
        if (options?.Value.Enabled == true)
        {
            // Check if the Channel service is registered
            var channel = app.ApplicationServices.GetService<Channel<ProxyTrafficLogEntry>>();
            if (channel != null)
            {
                app.UseMiddleware<ProxyTrafficLoggerMiddleware>();
                Log.Debug("🔍 Proxy traffic logging middleware enabled");
            }
            else
            {
                Log.Debug("⚠️ Proxy traffic logging middleware not enabled because required services are not registered");
            }
        }
        
        return app;
    }
}