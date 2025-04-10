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
