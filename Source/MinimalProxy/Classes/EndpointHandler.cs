namespace MinimalProxy.Classes;

using System.Text.Json;
using Serilog;

public static class EndpointHandler
{
    /// <summary>
    /// Scans the specified directory for endpoint definition files and returns a dictionary of endpoints.
    /// </summary>
    /// <param name="endpointsDirectory">Directory containing endpoint definitions</param>
    /// <returns>Dictionary with endpoint names as keys and tuples of (url, methods) as values</returns>
    public static Dictionary<string, (string Url, HashSet<string> Methods)> GetEndpoints(string endpointsDirectory)
    {
        var endpointMap = new Dictionary<string, (string Url, HashSet<string> Methods)>(StringComparer.OrdinalIgnoreCase);
        
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

                        // Add or update the endpoint
                        endpointMap[endpointName] = (entity.Url, new HashSet<string>(entity.Methods, StringComparer.OrdinalIgnoreCase));
                        Log.Debug("♨️ Loaded endpoint: {Name} -> {Url}", endpointName, entity.Url);
                    }
                    else
                    {
                        Log.Warning("⚠️ Failed to load endpoint from {File}", file);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "❌ Error parsing endpoint file: {File}", file);
                }
            }

            Log.Debug($"✅ Loaded {endpointMap.Count} endpoints from {endpointsDirectory}");
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
                var sample = new EndpointEntity
                {
                    Url = "https://jsonplaceholder.typicode.com/posts",
                    Methods = new List<string> { "GET", "POST" }
                };

                var json = JsonSerializer.Serialize(sample, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(samplePath, json);
                Log.Information($"✅ Created sample endpoint definition: {samplePath}");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error creating sample endpoint definition");
        }
    }
}

/// <summary>
/// Represents an endpoint entity loaded from a JSON file
/// </summary>
public class EndpointEntity
{
    public string Url { get; set; } = string.Empty;
    public List<string> Methods { get; set; } = new List<string>();
}
