namespace MinimalProxy.Classes;

using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

/// <summary>
/// Represents an endpoint entity with extended support for composite operations
/// </summary>
public class ExtendedEndpointEntity
{
    public string Url { get; set; } = string.Empty;
    public List<string> Methods { get; set; } = new List<string>();
    public string Type { get; set; } = "Standard"; // "Standard" or "Composite"
    public CompositeDefinition? CompositeConfig { get; set; }
    public bool IsPrivate { get; set; } = false; // If true, endpoint won't be exposed in the API
}

/// <summary>
/// Defines a composite endpoint that represents a multi-step API process
/// </summary>
public class CompositeDefinition
{
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<CompositeStep> Steps { get; set; } = new List<CompositeStep>();
}

/// <summary>
/// Represents a step within a composite endpoint process
/// </summary>
public class CompositeStep
{
    public string Name { get; set; } = string.Empty;
    public string Endpoint { get; set; } = string.Empty;
    public string Method { get; set; } = "POST";
    public string? DependsOn { get; set; }
    public bool IsArray { get; set; } = false;
    public string? ArrayProperty { get; set; }
    public string? SourceProperty { get; set; }
    public Dictionary<string, string> TemplateTransformations { get; set; } = new();
}

/// <summary>
/// Execution context to maintain state between composite step executions
/// </summary>
public class ExecutionContext
{
    public string RequestId { get; set; } = Guid.NewGuid().ToString();
    public Dictionary<string, object> Variables { get; set; } = new();
    
    public void SetVariable(string name, object value)
    {
        Variables[name] = value;
    }
    
    public T? GetVariable<T>(string name)
    {
        if (Variables.TryGetValue(name, out var value))
        {
            if (value is T typedValue)
            {
                return typedValue;
            }
            
            try
            {
                // Try to convert if direct cast fails
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch
            {
                return default;
            }
        }
        
        return default;
    }
}

/// <summary>
/// Result of a composite endpoint execution
/// </summary>
public class CompositeResult
{
    public bool Success { get; set; }
    public Dictionary<string, object> StepResults { get; set; } = new();
    public string? ErrorStep { get; set; }
    public string? ErrorMessage { get; set; }
}