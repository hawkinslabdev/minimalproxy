namespace MinimalProxy.Classes;

using System.Text.Json;
using Serilog;

public enum EndpointType
{
    Standard,
    Composite,
    Private
}

/// <summary>
/// Unified endpoint definition that handles all endpoint types
/// </summary>
public class EndpointDefinition
{
    public string Url { get; set; } = string.Empty;
    public List<string> Methods { get; set; } = new List<string>();
    public EndpointType Type { get; set; } = EndpointType.Standard;
    public CompositeDefinition? CompositeConfig { get; set; }
    public bool IsPrivate { get; set; } = false;

    // Helper properties to simplify type checking
    public bool IsStandard => Type == EndpointType.Standard && !IsPrivate;
    public bool IsComposite => Type == EndpointType.Composite || 
                              (CompositeConfig != null && !string.IsNullOrEmpty(CompositeConfig.Name));
                              
    // Helper method to get a consistent tuple format compatible with existing code
    public (string Url, HashSet<string> Methods, bool IsPrivate, string Type) ToTuple()
    {
        string typeString = this.Type.ToString();
        return (Url, new HashSet<string>(Methods, StringComparer.OrdinalIgnoreCase), IsPrivate, typeString);
    }
}

public static class EndpointHandler
{
    // Cache for loaded endpoints to avoid multiple loads
    private static Dictionary<string, EndpointDefinition>? _loadedEndpoints = null;
    private static readonly object _loadLock = new object();
    
    /// <summary>
    /// Scans the specified directory for endpoint definition files and returns a dictionary of endpoints.
    /// </summary>
    /// <param name="endpointsDirectory">Directory containing endpoint definitions</param>
    /// <returns>Dictionary with endpoint names as keys and tuples of (url, methods, isPrivate, type) as values</returns>
    public static Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)> GetEndpoints(string endpointsDirectory)
    {
        // Load endpoints if not already loaded
        LoadEndpointsIfNeeded(endpointsDirectory);
        
        // Convert to the legacy format
        var endpointMap = new Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _loadedEndpoints!)
        {
            endpointMap[kvp.Key] = kvp.Value.ToTuple();
        }
        
        return endpointMap;
    }
    
    /// <summary>
    /// Loads all composite endpoint definitions from the endpoints directory
    /// </summary>
    public static Dictionary<string, CompositeDefinition> GetCompositeDefinitions(Dictionary<string, (string Url, HashSet<string> Methods, bool IsPrivate, string Type)> endpointMap)
    {
        // We already have endpoints loaded, so just extract the composite configs
        var compositeDefinitions = new Dictionary<string, CompositeDefinition>(StringComparer.OrdinalIgnoreCase);
        
        // If endpoints haven't been loaded yet, load them (this shouldn't happen in normal flow)
        if (_loadedEndpoints == null)
        {
            string endpointsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");
            LoadEndpointsIfNeeded(endpointsDirectory);
        }
        
        foreach (var kvp in _loadedEndpoints!)
        {
            if (kvp.Value.IsComposite && kvp.Value.CompositeConfig != null)
            {
                compositeDefinitions[kvp.Key] = kvp.Value.CompositeConfig;
            }
        }
        
        return compositeDefinitions;
    }
    
    /// <summary>
    /// Creates sample endpoint definitions if none exist
    /// </summary>
    /// <param name="endpointsDirectory">Directory to create samples in</param>
    public static void CreateSampleEndpoints(string endpointsDirectory)
    {
        try
        {
            if (!Directory.Exists(endpointsDirectory))
            {
                Directory.CreateDirectory(endpointsDirectory);
            }

            // Create a sample standard endpoint
            CreateSampleStandardEndpoint(endpointsDirectory);
            
            // Create a sample composite endpoint
            CreateSampleCompositeEndpoint(endpointsDirectory);
            
            // Clear the cached endpoints to force a reload
            lock (_loadLock)
            {
                _loadedEndpoints = null;
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error creating sample endpoint definitions");
        }
    }
    
    /// <summary>
    /// Internal method to load endpoints if they haven't been loaded yet
    /// </summary>
    private static void LoadEndpointsIfNeeded(string endpointsDirectory)
    {
        // Use double-check locking pattern to ensure thread safety
        if (_loadedEndpoints == null)
        {
            lock (_loadLock)
            {
                if (_loadedEndpoints == null)
                {
                    _loadedEndpoints = LoadAllEndpoints(endpointsDirectory);
                }
            }
        }
    }
    
    /// <summary>
    /// Internal method to load all endpoints from the endpoints directory
    /// </summary>
    private static Dictionary<string, EndpointDefinition> LoadAllEndpoints(string endpointsDirectory)
    {
        var endpoints = new Dictionary<string, EndpointDefinition>(StringComparer.OrdinalIgnoreCase);
        
        try
        {
            if (!Directory.Exists(endpointsDirectory))
            {
                Log.Warning($"⚠️ Endpoints directory not found: {endpointsDirectory}");
                Directory.CreateDirectory(endpointsDirectory);
                return endpoints;
            }

            // Get all JSON files in the endpoints directory and subdirectories
            foreach (var file in Directory.GetFiles(endpointsDirectory, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    // Read and parse the endpoint definition
                    var json = File.ReadAllText(file);
                    var definition = ParseEndpointDefinition(json);
                    
                    if (definition != null && !string.IsNullOrWhiteSpace(definition.Url) && definition.Methods.Any())
                    {
                        // Extract endpoint name from directory name
                        var endpointName = Path.GetFileName(Path.GetDirectoryName(file)) ?? "";
                        
                        // Skip if no valid name could be extracted
                        if (string.IsNullOrWhiteSpace(endpointName))
                        {
                            Log.Warning("⚠️ Could not determine endpoint name for {File}", file);
                            continue;
                        }

                        // Add the endpoint to the dictionary
                        endpoints[endpointName] = definition;
                        
                        LogEndpointLoading(endpointName, definition);
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

            Log.Information($"✅ Loaded {endpoints.Count} endpoints from {endpointsDirectory}");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ Error scanning endpoints directory: {Directory}", endpointsDirectory);
        }

        return endpoints;
    }
    
    /// <summary>
    /// Parses an endpoint definition from JSON, handling both legacy and extended formats
    /// </summary>
    private static EndpointDefinition? ParseEndpointDefinition(string json)
    {
        try
        {
            // First try to parse as an ExtendedEndpointEntity (preferred format)
            var extendedEntity = JsonSerializer.Deserialize<ExtendedEndpointEntity>(json, 
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            
            if (extendedEntity != null && !string.IsNullOrWhiteSpace(extendedEntity.Url) && extendedEntity.Methods != null)
            {
                return new EndpointDefinition
                {
                    Url = extendedEntity.Url,
                    Methods = extendedEntity.Methods,
                    IsPrivate = extendedEntity.IsPrivate,
                    Type = ParseEndpointType(extendedEntity.Type),
                    CompositeConfig = extendedEntity.CompositeConfig
                };
            }
            
            // Try to parse as a standard EndpointEntity as fallback
            var entity = JsonSerializer.Deserialize<EndpointEntity>(json);
            
            if (entity != null && !string.IsNullOrWhiteSpace(entity.Url) && entity.Methods != null)
            {
                return new EndpointDefinition
                {
                    Url = entity.Url,
                    Methods = entity.Methods,
                    IsPrivate = false, // Legacy format doesn't support IsPrivate
                    Type = EndpointType.Standard,
                    CompositeConfig = null
                };
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error parsing endpoint definition");
        }
        
        return null;
    }

    /// <summary>
    /// Converts a string type to the EndpointType enum
    /// </summary>
    private static EndpointType ParseEndpointType(string? typeString)
    {
        if (string.IsNullOrWhiteSpace(typeString))
            return EndpointType.Standard;
            
        return typeString.ToLowerInvariant() switch
        {
            "composite" => EndpointType.Composite,
            "private" => EndpointType.Private,
            _ => EndpointType.Standard
        };
    }

    /// <summary>
    /// Logs information about a loaded endpoint with appropriate emoji based on type
    /// </summary>
    private static void LogEndpointLoading(string endpointName, EndpointDefinition definition)
    {
        if (definition.IsPrivate)
        {
            Log.Debug("🔒 Loaded private endpoint: {Name} -> {Url}", endpointName, definition.Url);
        }
        else if (definition.IsComposite)
        {
            Log.Debug("🧩 Loaded composite endpoint: {Name} -> {Url}", endpointName, definition.Url);
        }
        else
        {
            Log.Debug("♨️ Loaded standard endpoint: {Name} -> {Url}", endpointName, definition.Url);
        }
    }
    
    /// <summary>
    /// Creates a sample standard endpoint definition
    /// </summary>
    private static void CreateSampleStandardEndpoint(string endpointsDirectory)
    {
        var sampleDir = Path.Combine(endpointsDirectory, "Sample");
        if (!Directory.Exists(sampleDir))
        {
            Directory.CreateDirectory(sampleDir);
        }

        var samplePath = Path.Combine(sampleDir, "entity.json");
        if (!File.Exists(samplePath))
        {
            var sample = new EndpointDefinition
            {
                Url = "https://jsonplaceholder.typicode.com/posts",
                Methods = new List<string> { "GET", "POST" },
                Type = EndpointType.Standard,
                IsPrivate = false
            };

            // Convert to the format expected by existing code
            var entity = new
            {
                Url = sample.Url,
                Methods = sample.Methods
            };

            var json = JsonSerializer.Serialize(entity, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(samplePath, json);
            Log.Information($"✅ Created sample standard endpoint definition: {samplePath}");
        }
    }

    /// <summary>
    /// Creates a sample composite endpoint definition
    /// </summary>
    private static void CreateSampleCompositeEndpoint(string endpointsDirectory)
    {
        var compositeSampleDir = Path.Combine(endpointsDirectory, "SampleComposite");
        if (!Directory.Exists(compositeSampleDir))
        {
            Directory.CreateDirectory(compositeSampleDir);
        }
        
        var compositeSamplePath = Path.Combine(compositeSampleDir, "entity.json");
        if (!File.Exists(compositeSamplePath))
        {
            var compositeSample = new
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

    public class EndpointEntity
    {
        public string Url { get; set; } = string.Empty;
        public List<string> Methods { get; set; } = new List<string>();
    }
}