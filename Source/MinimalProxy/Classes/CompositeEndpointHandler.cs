namespace MinimalProxy.Classes;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Serilog;

public class CompositeEndpointHandler
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)> _endpointMap;
    private readonly Dictionary<string, CompositeDefinition> _compositeDefinitions;
    private readonly string _serverName;
    
    public CompositeEndpointHandler(
        IHttpClientFactory httpClientFactory,
        Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)> endpointMap,
        string serverName)
    {
        _httpClientFactory = httpClientFactory;
        _endpointMap = endpointMap;
        _serverName = serverName;
        _compositeDefinitions = LoadCompositeDefinitions();
    }
    
    /// <summary>
    /// Load all composite endpoint definitions from the endpoints directory
    /// </summary>
    private Dictionary<string, CompositeDefinition> LoadCompositeDefinitions()
    {
        var compositeDefinitions = new Dictionary<string, CompositeDefinition>(StringComparer.OrdinalIgnoreCase);
        
        try
        {
            string endpointsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");
            if (!Directory.Exists(endpointsDirectory))
            {
                Log.Warning("⚠️ Endpoints directory not found: {Directory}", endpointsDirectory);
                return compositeDefinitions;
            }
            
            // Get all JSON files in the endpoints directory and subdirectories
            foreach (var file in Directory.GetFiles(endpointsDirectory, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    // Read and parse the endpoint definition
                    var json = File.ReadAllText(file);
                    var entity = JsonSerializer.Deserialize<ExtendedEndpointEntity>(json, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (entity != null && entity.Type?.Equals("Composite", StringComparison.OrdinalIgnoreCase) == true 
                        && entity.CompositeConfig != null)
                    {
                        // Extract endpoint name from directory name
                        var endpointName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                        
                        // Skip if no valid name could be extracted
                        if (string.IsNullOrWhiteSpace(endpointName))
                        {
                            Log.Warning("⚠️ Could not determine endpoint name for composite endpoint: {File}", file);
                            continue;
                        }
                        
                        // Add to composite definitions
                        compositeDefinitions[endpointName] = entity.CompositeConfig;
                        Log.Information("✅ Loaded composite endpoint: {Name} with {StepCount} steps", 
                            endpointName, entity.CompositeConfig.Steps.Count);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "❌ Error parsing composite endpoint file: {File}", file);
                }
            }
            
            Log.Information("✅ Loaded {Count} composite endpoints", compositeDefinitions.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error loading composite endpoints");
        }
        
        return compositeDefinitions;
    }
    
    /// <summary>
    /// Process a composite endpoint request
    /// </summary>
    public async Task<IResult> ProcessCompositeEndpointAsync(
        HttpContext context, 
        string env, 
        string endpointName, 
        string requestBody)
    {
        try
        {
            Log.Information("⚙️ Processing composite endpoint: {Endpoint} for environment: {Environment}", 
                endpointName, env);
                
            // Check if composite endpoint exists
            if (!_compositeDefinitions.TryGetValue(endpointName, out var compositeDefinition))
            {
                Log.Warning("❌ Composite endpoint not found: {Endpoint}", endpointName);
                return Results.NotFound(new { error = $"Composite endpoint '{endpointName}' not found" });
            }
            
            // Parse request body
            JsonNode? requestData;
            try
            {
                requestData = JsonNode.Parse(requestBody);
                if (requestData == null)
                {
                    throw new JsonException("Request body cannot be null");
                }
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "❌ Invalid JSON in request body for composite endpoint: {Endpoint}", endpointName);
                return Results.BadRequest(new { error = "Invalid request format", details = ex.Message });
            }
            
            // Create execution context and result container
            var executionContext = new ExecutionContext();
            var result = new CompositeResult { Success = true };
            
            // Execute each step in the composite definition
            foreach (var step in compositeDefinition.Steps)
            {
                Log.Information("▶️ Executing step: {StepName} for composite endpoint: {Endpoint}", 
                    step.Name, endpointName);
                    
                try
                {
                    var stepResult = await ExecuteStepAsync(step, requestData, result.StepResults, executionContext, env);
                    result.StepResults[step.Name] = stepResult;
                }
                catch (Exception ex)
                {
                    // Step execution failed
                    result.Success = false;
                    result.ErrorStep = step.Name;
                    result.ErrorMessage = ex.Message;
                    
                    Log.Error(ex, "❌ Error executing step {StepName} for composite endpoint {Endpoint}: {ErrorMessage}", 
                        step.Name, endpointName, ex.Message);
                        
                    return Results.BadRequest(new
                    {
                        error = $"Error executing step '{step.Name}'",
                        details = ex.Message,
                        step = step.Name,
                        result
                    });
                }
            }
            
            Log.Information("✅ Successfully executed composite endpoint: {Endpoint}", endpointName);
            return Results.Ok(result);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Unhandled error processing composite endpoint {Endpoint}: {ErrorMessage}", 
                endpointName, ex.Message);
                
            return Results.BadRequest(new
            {
                error = "Error processing composite endpoint",
                details = ex.Message
            });
        }
    }
    
    /// <summary>
    /// Execute a single step in a composite endpoint
    /// </summary>
    private async Task<object> ExecuteStepAsync(
        CompositeStep step, 
        JsonNode requestData, 
        Dictionary<string, object> previousResults, 
        ExecutionContext context, 
        string env)
    {
        // Find the target endpoint for this step
        if (!_endpointMap.TryGetValue(step.Endpoint, out var endpoint))
        {
            throw new Exception($"Target endpoint '{step.Endpoint}' not found for step '{step.Name}'");
        }
        
        // Check if the endpoint supports the requested method
        if (!endpoint.Methods.Contains(step.Method))
        {
            throw new Exception(
                $"Method '{step.Method}' not supported by endpoint '{step.Endpoint}' for step '{step.Name}'");
        }
        
        // Handle array processing if needed
        if (step.IsArray && !string.IsNullOrEmpty(step.ArrayProperty))
        {
            JsonNode? arrayNode = null;
            
            // Try to get the array from the request data
            if (requestData is JsonObject requestObj)
            {
                arrayNode = requestObj[step.ArrayProperty];
            }
            
            if (arrayNode is JsonArray array)
            {
                var results = new List<object>();
                
                // Process each item in the array
                foreach (var item in array)
                {
                    // Create a deep clone of the item to avoid parent node issues
                    var clonedItem = item?.DeepClone();
                    if (clonedItem != null)
                    {
                        var itemResult = await ProcessSingleItemAsync(step, clonedItem, endpoint.Url, context, env, previousResults);
                        results.Add(itemResult);
                    }
                }
                
                return results;
            }
            else
            {
                throw new Exception(
                    $"Property '{step.ArrayProperty}' is not an array or not found in the request data for step '{step.Name}'");
            }
        }
        else
        {
            // Process a single item (non-array step)
            JsonNode nodeToProcess;
            
            // If this step depends on a previous step and not from the request data
            if (!string.IsNullOrEmpty(step.DependsOn) && previousResults.ContainsKey(step.DependsOn))
            {
                // Use the result from the previous step
                nodeToProcess = JsonSerializer.SerializeToNode(previousResults[step.DependsOn])!;
            }
            else if (!string.IsNullOrEmpty(step.SourceProperty) && requestData is JsonObject reqObj && reqObj.TryGetPropertyValue(step.SourceProperty, out var sourceNode))
            {
                // Use the specific source property from the request data
                nodeToProcess = sourceNode?.DeepClone() ?? JsonNode.Parse("{}") ?? throw new Exception("Failed to create empty JSON object");
            }
            else
            {
                // Use the full request data for this step
                nodeToProcess = requestData.DeepClone();
            }
            
            return await ProcessSingleItemAsync(step, nodeToProcess, endpoint.Url, context, env, previousResults);
        }
    }
    
    /// <summary>
    /// Process a single item for a step (either a direct item or an item within an array)
    /// </summary>
    private async Task<object> ProcessSingleItemAsync(
        CompositeStep step, 
        JsonNode itemData, 
        string endpointUrl, 
        ExecutionContext context, 
        string env,
        Dictionary<string, object> previousResults)
    {
        // Apply template transformations
        ApplyTemplateTransformations(step, itemData, context, previousResults);
        
        // Create the HttpClient
        var client = _httpClientFactory.CreateClient("ProxyClient");
        
        // Prepare the URL for the request
        string fullUrl = endpointUrl;
        
        // Create the request
        var request = new HttpRequestMessage(new HttpMethod(step.Method), fullUrl);
        
        // Add headers
        request.Headers.Add("ServerName", _serverName);
        request.Headers.Add("DatabaseName", env);
        request.Headers.Add("Accept", "application/json,text/javascript; charset=utf-8");
        
        // Add content if needed
        if (step.Method != "GET" && step.Method != "DELETE")
        {
            var content = new StringContent(
                itemData.ToJsonString(), 
                Encoding.UTF8, 
                "application/json");
                
            request.Content = content;
        }
        
        // Execute the request
        var response = await client.SendAsync(request);
        
        // Ensure success
        if (!response.IsSuccessStatusCode)
        {
            var errorContent = await response.Content.ReadAsStringAsync();
            throw new Exception(
                $"Error executing step '{step.Name}': {response.StatusCode} - {errorContent}");
        }
        
        // Parse and return the response
        var responseContent = await response.Content.ReadAsStringAsync();
        
        try
        {
            // Try to parse as JSON object
            var jsonResponse = JsonSerializer.Deserialize<object>(responseContent, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                
            return jsonResponse!;
        }
        catch
        {
            // If not valid JSON, return as string
            return responseContent;
        }
    }
    
    /// <summary>
    /// Apply template transformations to the request data
    /// </summary>
    private void ApplyTemplateTransformations(
        CompositeStep step, 
        JsonNode data, 
        ExecutionContext context,
        Dictionary<string, object> previousResults)
    {
        if (data is not JsonObject jsonObj)
        {
            return;
        }
        
        foreach (var transform in step.TemplateTransformations)
        {
            var key = transform.Key;
            var valueTemplate = transform.Value;
            
            switch (valueTemplate.ToLowerInvariant())
            {
                case "$guid":
                    // Generate a new GUID
                    jsonObj[key] = Guid.NewGuid().ToString();
                    break;
                    
                case "$requestid":
                    // Use the request ID from the context
                    jsonObj[key] = context.RequestId;
                    break;
                    
                default:
                    // Handle variables and previous results references
                    if (valueTemplate.StartsWith("$context."))
                    {
                        var varName = valueTemplate.Substring("$context.".Length);
                        var value = context.GetVariable<string>(varName);
                        if (value != null)
                        {
                            // Create a new JsonNode for the value to avoid parent issues
                            jsonObj[key] = JsonValue.Create(value);
                        }
                    }
                    else if (valueTemplate.StartsWith("$prev."))
                    {
                        var parts = valueTemplate.Substring("$prev.".Length).Split('.');
                        if (parts.Length >= 2)
                        {
                            var stepName = parts[0];
                            var propPath = string.Join('.', parts.Skip(1));
                            
                            if (previousResults.TryGetValue(stepName, out var prevResult))
                            {
                                var prevJson = JsonSerializer.SerializeToNode(prevResult);
                                var value = GetNestedValue(prevJson, propPath);
                                if (value != null)
                                {
                                    // IMPORTANT: Extract the raw value and create a new JsonValue
                                    // instead of trying to reuse the JsonNode which would have a parent
                                    object? rawValue = null;
                                    if (value is JsonValue jsonValue)
                                    {
                                        // For simple values (string, numbers, etc.)
                                        rawValue = ExtractRawValue(jsonValue);
                                        jsonObj[key] = JsonValue.Create(rawValue);
                                    }
                                    else
                                    {
                                        // For complex objects, serialize and parse again to break parent relationship
                                        var serialized = value.ToJsonString();
                                        jsonObj[key] = JsonNode.Parse(serialized);
                                    }
                                }
                            }
                        }
                    }
                    break;
            }
        }
    }
    
    /// <summary>
    /// Extract the raw value from a JsonValue
    /// </summary>
    private object? ExtractRawValue(JsonValue jsonValue)
    {
        if (jsonValue.TryGetValue<string>(out var stringValue))
            return stringValue;
            
        if (jsonValue.TryGetValue<int>(out var intValue))
            return intValue;
            
        if (jsonValue.TryGetValue<long>(out var longValue))
            return longValue;
            
        if (jsonValue.TryGetValue<double>(out var doubleValue))
            return doubleValue;
            
        if (jsonValue.TryGetValue<bool>(out var boolValue))
            return boolValue;
            
        if (jsonValue.TryGetValue<DateTime>(out var dateValue))
            return dateValue;
            
        return null;
    }
    
    /// <summary>
    /// Get a nested value from a JSON node using a property path (e.g., "prop1.prop2.prop3")
    /// </summary>
    private JsonNode? GetNestedValue(JsonNode? node, string propertyPath)
    {
        if (node == null || string.IsNullOrEmpty(propertyPath))
        {
            return null;
        }
        
        var parts = propertyPath.Split('.');
        var current = node;
        
        foreach (var part in parts)
        {
            if (current is JsonObject obj && obj.TryGetPropertyValue(part, out var value))
            {
                current = value;
            }
            else if (current is JsonArray array && int.TryParse(part, out var index) && index >= 0 && index < array.Count)
            {
                current = array[index];
            }
            else
            {
                Log.Warning("Property path part '{Part}' not found in JSON object", part);
                return null;
            }
        }
        
        return current;
    }
}
