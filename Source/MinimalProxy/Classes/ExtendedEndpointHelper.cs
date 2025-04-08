namespace MinimalProxy.Classes;

using System.Text.Json;
using Serilog;

public static class ExtendedEndpointHandler
{
    /// <summary>
    /// Scans the specified directory for endpoint definition files and returns a dictionary of endpoints.
    /// Supports both standard and composite endpoints.
    /// </summary>
    /// <param name="endpointsDirectory">Directory containing endpoint definitions</param>
    /// <returns>Dictionary with endpoint names as keys and tuples of (url, methods, isPrivate, type) as values</returns>
    public static Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)> GetEndpoints(string endpointsDirectory)
    {
        var endpointMap = new Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)>(StringComparer.OrdinalIgnoreCase);
        
        try
        {
            if (!Directory.Exists(endpointsDirectory))
            {
                Log.Warning($"⚠️ Endpoints directory not found: {endpointsDirectory}");
                Directory.CreateDirectory(endpointsDirectory);
                return endpointMap;
            }

            // Get all JSON files in the endpoints directory and subdirectories
            foreach (var file in Directory.GetFiles(endpointsDirectory, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    // Read and parse the endpoint definition
                    var json = File.ReadAllText(file);
                    
                    // First try to parse as an ExtendedEndpointEntity
                    var extendedEntity = JsonSerializer.Deserialize<ExtendedEndpointEntity>(json, 
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    
                    if (extendedEntity != null && !string.IsNullOrWhiteSpace(extendedEntity.Url) && extendedEntity.Methods != null)
                    {
                        // Extract endpoint name from directory name
                        var endpointName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                        
                        // Skip if no valid name could be extracted
                        if (string.IsNullOrWhiteSpace(endpointName))
                        {
                            Log.Warning("⚠️ Could not determine endpoint name for {File}", file);
                            continue;
                        }

                        // Add or update the endpoint
                        endpointMap[endpointName] = (
                            extendedEntity.Url, 
                            new HashSet<string>(extendedEntity.Methods, StringComparer.OrdinalIgnoreCase),
                            extendedEntity.IsPrivate,
                            extendedEntity.Type ?? "Standard"
                        );
                        
                        // Log different message for composite endpoints
                        if (extendedEntity.Type?.Equals("Composite", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            Log.Debug("♨️ Loaded composite endpoint: {Name} -> {Url}", endpointName, extendedEntity.Url);
                        }
                        else if (extendedEntity.IsPrivate)
                        {
                            Log.Debug("♨️ Loaded private endpoint: {Name} -> {Url}", endpointName, extendedEntity.Url);
                        }
                        else
                        {
                            Log.Debug("♨️ Loaded standard endpoint: {Name} -> {Url}", endpointName, extendedEntity.Url);
                        }
                    }
                    else
                    {
                        // Try to parse as a standard EndpointEntity as fallback
                        var entity = JsonSerializer.Deserialize<EndpointEntity>(json);
                        
                        if (entity != null && !string.IsNullOrWhiteSpace(entity.Url) && entity.Methods != null)
                        {
                            // Extract endpoint name from directory name
                            var endpointName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                            
                            // Skip if no valid name could be extracted
                            if (string.IsNullOrWhiteSpace(endpointName))
                            {
                                Log.Warning("⚠️ Could not determine endpoint name for {File}", file);
                                continue;
                            }

                            // Add or update the endpoint - legacy entity has no IsPrivate, so assume false
                            endpointMap[endpointName] = (
                                entity.Url, 
                                new HashSet<string>(entity.Methods, StringComparer.OrdinalIgnoreCase),
                                false, // Not private
                                "Standard"
                            );
                            Log.Debug("♨️ Loaded standard endpoint: {Name} -> {Url}", endpointName, entity.Url);
                        }
                        else
                        {
                            Log.Warning("⚠️ Failed to load endpoint from {File}", file);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "❌ Error parsing endpoint file: {File}", file);
                }
            }

            Log.Information($"✅ Loaded {endpointMap.Count} endpoints from {endpointsDirectory}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error scanning endpoints directory: {Directory}", endpointsDirectory);
        }

        return endpointMap;
    }

    /// <summary>
    /// Creates a sample endpoint definition if none exist
    /// </summary>
    /// <param name="endpointsDirectory">Directory to create sample in</param>
    public static void CreateSampleEndpoint(string endpointsDirectory)
    {
        try
        {
            if (!Directory.Exists(endpointsDirectory))
            {
                Directory.CreateDirectory(endpointsDirectory);
            }

            // Create a sample endpoint directory
            var sampleDir = Path.Combine(endpointsDirectory, "Sample");
            if (!Directory.Exists(sampleDir))
            {
                Directory.CreateDirectory(sampleDir);
            }

            // Only create sample if directory is empty
            var samplePath = Path.Combine(sampleDir, "endpoint.json");
            if (!File.Exists(samplePath))
            {
                var sample = new ExtendedEndpointEntity
                {
                    Url = "https://jsonplaceholder.typicode.com/posts",
                    Methods = new List<string> { "GET", "POST" },
                    Type = "Standard"
                };

                var json = JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(samplePath, json);
                Log.Information($"✅ Created sample endpoint definition: {samplePath}");
            }
            
            // Create a sample composite endpoint
            var compositeSampleDir = Path.Combine(endpointsDirectory, "SampleComposite");
            if (!Directory.Exists(compositeSampleDir))
            {
                Directory.CreateDirectory(compositeSampleDir);
            }
            
            var compositeSamplePath = Path.Combine(compositeSampleDir, "endpoint.json");
            if (!File.Exists(compositeSamplePath))
            {
                var compositeSample = new ExtendedEndpointEntity
                {
                    Url = "http://localhost:8020/services/Exact.Entity.REST.EG",
                    Methods = new List<string> { "POST" },
                    Type = "Composite",
                    CompositeConfig = new CompositeDefinition
                    {
                        Name = "SampleComposite",
                        Description = "Sample composite endpoint",
                        Steps = new List<CompositeStep>
                        {
                            new CompositeStep
                            {
                                Name = "Step1",
                                Endpoint = "SampleEndpoint1",
                                Method = "POST",
                                TemplateTransformations = new Dictionary<string, string>
                                {
                                    { "TransactionKey", "$guid" }
                                }
                            },
                            new CompositeStep
                            {
                                Name = "Step2",
                                Endpoint = "SampleEndpoint2",
                                Method = "POST",
                                DependsOn = "Step1",
                                TemplateTransformations = new Dictionary<string, string>
                                {
                                    { "TransactionKey", "$prev.Step1.TransactionKey" }
                                }
                            }
                        }
                    }
                };
                
                var json = JsonSerializer.Serialize(compositeSample, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(compositeSamplePath, json);
                Log.Information($"✅ Created sample composite endpoint definition: {compositeSamplePath}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error creating sample endpoint definition");
        }
    }
}