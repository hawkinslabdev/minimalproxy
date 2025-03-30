namespace MinimalProxy.Classes;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Serilog;
using Swashbuckle.AspNetCore.SwaggerGen;

public class DynamicEndpointDocumentFilter : IDocumentFilter
{
    private readonly ILogger<DynamicEndpointDocumentFilter> _logger;

    public DynamicEndpointDocumentFilter(ILogger<DynamicEndpointDocumentFilter> logger)
    {
        _logger = logger;
    }

    public void Apply(OpenApiDocument swaggerDoc, DocumentFilterContext context)
    {
        // Get your endpoint map
        var endpointsDirectory = Path.Combine(Directory.GetCurrentDirectory(), "endpoints");
        var endpointMap = EndpointHelper.GetEndpoints(endpointsDirectory);
        
        // Get allowed environments for parameter description only
        var allowedEnvironments = GetAllowedEnvironments();

        // Create paths for each endpoint - but only once, not per environment
        foreach (var (endpointName, (url, methods)) in endpointMap)
        {
            // Skip Swagger/Refresh endpoint
            if (string.Equals(endpointName, "Swagger", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Create a generic path with {env} parameter instead of specific environments
            string path = $"/api/{{env}}/{endpointName}";
            
            // If the path doesn't exist yet, create it
            if (!swaggerDoc.Paths.ContainsKey(path))
            {
                swaggerDoc.Paths.Add(path, new OpenApiPathItem());
            }

            // Add operations for each HTTP method
            foreach (var method in methods)
            {
                var operation = new OpenApiOperation
                {
                    Tags = new List<OpenApiTag> { new OpenApiTag { Name = endpointName } },
                    Summary = $"{method} {endpointName} endpoint",
                    Description = $"Proxies {method} requests to {url}",
                    OperationId = $"{method.ToLower()}_{endpointName}".Replace(" ", "_"),
                    Parameters = new List<OpenApiParameter>()
                };

                // Add environment parameter
                operation.Parameters.Add(new OpenApiParameter
                {
                    Name = "env",
                    In = ParameterLocation.Path,
                    Required = true,
                    Schema = new OpenApiSchema { Type = "string", Enum = allowedEnvironments.Select(e => new OpenApiString(e)).Cast<IOpenApiAny>().ToList() },
                    Description = $"Environment to target. Allowed values: {string.Join(", ", allowedEnvironments)}"
                });

                // Add OData style query parameters for GET requests
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    // Add $select parameter
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "$select",
                        In = ParameterLocation.Query,
                        Required = false,
                        Schema = new OpenApiSchema { Type = "string" },
                        Description = "Select specific fields (comma-separated list of property names)"
                    });

                    // Add $top parameter with default value
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "$top",
                        In = ParameterLocation.Query,
                        Required = false,
                        Schema = new OpenApiSchema { 
                            Type = "integer", 
                            Default = new OpenApiInteger(10),
                            Minimum = 1,
                            Maximum = 1000
                        },
                        Description = "Limit the number of results returned (default: 10, max: 1000)"
                    });

                    // Add $filter parameter
                    operation.Parameters.Add(new OpenApiParameter
                    {
                        Name = "$filter",
                        In = ParameterLocation.Query,
                        Required = false,
                        Schema = new OpenApiSchema { Type = "string" },
                        Description = "Filter the results based on a condition (e.g., Name eq 'Value')"
                    });
                }

                // Add example response
                operation.Responses = new OpenApiResponses
                {
                    ["200"] = new OpenApiResponse 
                    { 
                        Description = "Successful response",
                        Content = new Dictionary<string, OpenApiMediaType>
                        {
                            ["application/json"] = new OpenApiMediaType
                            {
                                Schema = new OpenApiSchema { Type = "object" }
                            }
                        }
                    }
                };

                // Add the operation to the path with the appropriate HTTP method
                AddOperationToPath(swaggerDoc.Paths[path], method, operation);
            }
        }

        // Remove any paths with {catchall} in them
        var pathsToRemove = swaggerDoc.Paths.Keys
            .Where(p => p.Contains("{catchall}"))
            .ToList();

        foreach (var path in pathsToRemove)
        {
            swaggerDoc.Paths.Remove(path);
        }
    }

    private List<string> GetAllowedEnvironments()
    {
        try
        {
            var settingsFile = Path.Combine(Directory.GetCurrentDirectory(), "environments", "settings.json");
            if (File.Exists(settingsFile))
            {
                var settingsJson = File.ReadAllText(settingsFile);
                
                // Match the structure used in EnvironmentSettings class
                var settings = JsonSerializer.Deserialize<SettingsModel>(settingsJson);
                if (settings?.Environment?.AllowedEnvironments != null && 
                    settings.Environment.AllowedEnvironments.Any())
                {
                    return settings.Environment.AllowedEnvironments;
                }
            }
            
            // Return default if settings not found
            return new List<string> { "dev", "test" };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading environment settings");
            return new List<string> { "dev", "test" };
        }
    }

    // Match the classes used in EnvironmentSettings
    private class SettingsModel
    {
        public EnvironmentModel Environment { get; set; } = new EnvironmentModel();
    }

    private class EnvironmentModel
    {
        public string ServerName { get; set; } = ".";
        public List<string> AllowedEnvironments { get; set; } = new List<string>();
    }

    private void AddOperationToPath(OpenApiPathItem pathItem, string method, OpenApiOperation operation)
    {
        switch (method.ToUpper())
        {
            case "GET":
                pathItem.Operations[OperationType.Get] = operation;
                break;
            case "POST":
                pathItem.Operations[OperationType.Post] = operation;
                break;
            case "PUT":
                pathItem.Operations[OperationType.Put] = operation;
                break;
            case "DELETE":
                pathItem.Operations[OperationType.Delete] = operation;
                break;
            case "PATCH":
                pathItem.Operations[OperationType.Patch] = operation;
                break;
            case "OPTIONS":
                pathItem.Operations[OperationType.Options] = operation;
                break;
            case "MERGE":
                // Note: OpenAPI doesn't have a native MERGE operation type
                // You might want to create a custom operation or use a different approach
                // For now, we'll use Head as it's less commonly used than Options
                pathItem.Operations[OperationType.Head] = operation;
                break;        
        }
    }
}

public class DynamicEndpointOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        if (context.ApiDescription.RelativePath == null || 
            context.ApiDescription.RelativePath.StartsWith("swagger", StringComparison.OrdinalIgnoreCase))
        {
            return; 
        }

        // Initialize security collection if null
        operation.Security ??= new List<OpenApiSecurityRequirement>();

        // Add security requirement
        operation.Security.Add(new OpenApiSecurityRequirement
        {
            {
                new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                new string[] { }
            }
        });
        
        // Initialize responses if null
        operation.Responses ??= new OpenApiResponses();
        
        // Add standard response codes
        operation.Responses.Add("401", new OpenApiResponse { Description = "Unauthorized" });
        operation.Responses.Add("403", new OpenApiResponse { Description = "Forbidden" });
        operation.Responses.Add("404", new OpenApiResponse { Description = "Not Found" });
        operation.Responses.Add("500", new OpenApiResponse { Description = "Server Error" });
    }
}

public class SwaggerSettings
{
    public bool Enabled { get; set; } = true;
    public string Title { get; set; } = "API";
    public string Version { get; set; } = "v1";
    public string Description { get; set; } = "Documentation";
    public ContactInfo Contact { get; set; } = new ContactInfo();
    public SecurityDefinitionInfo SecurityDefinition { get; set; } = new SecurityDefinitionInfo();
    public string RoutePrefix { get; set; } = "swagger";
    public string DocExpansion { get; set; } = "List";
    public int DefaultModelsExpandDepth { get; set; } = -1;
    public bool DisplayRequestDuration { get; set; } = true;
    public bool EnableFilter { get; set; } = true;
    public bool EnableDeepLinking { get; set; } = true;
    public bool EnableValidator { get; set; } = true;
}

public class ContactInfo
{
    public string Name { get; set; } = "Support";
    public string Email { get; set; } = "";
}

public class SecurityDefinitionInfo
{
    public string Name { get; set; } = "Bearer";
    public string Description { get; set; } = "JWT Authorization header using the Bearer scheme. Example: \"Bearer {token}\"";
    public string In { get; set; } = "Header";
    public string Type { get; set; } = "ApiKey";
    public string Scheme { get; set; } = "Bearer";
}

// Renamed from Settings to EnvironmentConfigs to avoid conflicts
public class EnvironmentConfigs
{
    public EnvironmentSettings? EnvironmentSettings { get; set; }
}


// Static class for Swagger configuration methods
public static class SwaggerConfiguration
{
    // Configure Swagger with fallbacks
    public static SwaggerSettings ConfigureSwagger(WebApplicationBuilder builder)
    {
        // Create default settings
        var swaggerSettings = new SwaggerSettings();
        
        try
        {
            // Attempt to bind from configuration
            var section = builder.Configuration.GetSection("Swagger");
            if (section.Exists())
            {
                section.Bind(swaggerSettings);
                Log.Information("✅ Swagger configuration loaded from appsettings.json");
            }
            else
            {
                Log.Warning("⚠️ No 'Swagger' section found in configuration. Using default settings.");
            }
        }
        catch (Exception ex)
        {
            // Log error but continue with defaults
            Log.Error(ex, "❌ Error loading Swagger configuration. Using default settings.");
        }
        
        // Ensure object references aren't null (defensive programming)
        swaggerSettings.Contact ??= new ContactInfo();
        swaggerSettings.SecurityDefinition ??= new SecurityDefinitionInfo();
        
        // Validate and fix critical values
        if (string.IsNullOrWhiteSpace(swaggerSettings.Title))
            swaggerSettings.Title = "MinimalProxy API";
            
        if (string.IsNullOrWhiteSpace(swaggerSettings.Version))
            swaggerSettings.Version = "v1";
            
        if (string.IsNullOrWhiteSpace(swaggerSettings.SecurityDefinition.Name))
            swaggerSettings.SecurityDefinition.Name = "Bearer";
            
        if (string.IsNullOrWhiteSpace(swaggerSettings.SecurityDefinition.Scheme))
            swaggerSettings.SecurityDefinition.Scheme = "Bearer";
            
        // Register Swagger services if enabled
        if (swaggerSettings.Enabled)
        {
            builder.Services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc(swaggerSettings.Version, new OpenApiInfo
                {
                    Title = swaggerSettings.Title,
                    Version = swaggerSettings.Version,
                    Description = swaggerSettings.Description ?? "API Documentation",
                    Contact = new OpenApiContact
                    {
                        Name = swaggerSettings.Contact.Name,
                        Email = swaggerSettings.Contact.Email
                    }
                });
                
                // Add security definition for Bearer token
                c.AddSecurityDefinition(swaggerSettings.SecurityDefinition.Name, new OpenApiSecurityScheme
                {
                    Description = swaggerSettings.SecurityDefinition.Description,
                    Name = "Authorization",
                    In = ParseEnum<ParameterLocation>(swaggerSettings.SecurityDefinition.In, ParameterLocation.Header),
                    Type = ParseEnum<SecuritySchemeType>(swaggerSettings.SecurityDefinition.Type, SecuritySchemeType.ApiKey),
                    Scheme = swaggerSettings.SecurityDefinition.Scheme
                });
                
                // Add security requirement
                c.AddSecurityRequirement(new OpenApiSecurityRequirement
                {
                    {
                        new OpenApiSecurityScheme
                        {
                            Reference = new OpenApiReference
                            {
                                Type = ReferenceType.SecurityScheme,
                                Id = swaggerSettings.SecurityDefinition.Name
                            }
                        },
                        new string[] { }
                    }
                });
                
                // Add filters
                c.DocumentFilter<DynamicEndpointDocumentFilter>();
                c.OperationFilter<DynamicEndpointOperationFilter>();
            });

            builder.Services.AddSingleton<DynamicEndpointDocumentFilter>();
            builder.Services.AddSingleton<DynamicEndpointOperationFilter>();
            
            Log.Information("✅ Swagger services registered successfully");
        }
        else
        {
            Log.Information("ℹ️ Swagger is disabled in configuration");
        }
        
        return swaggerSettings;
    }

    // Configure Swagger UI after app is built
    public static void ConfigureSwaggerUI(WebApplication app, SwaggerSettings swaggerSettings)
    {
        if (!swaggerSettings.Enabled)
            return;
            
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint($"/swagger/{swaggerSettings.Version}/swagger.json", $"{swaggerSettings.Title} {swaggerSettings.Version}");
            c.RoutePrefix = swaggerSettings.RoutePrefix ?? "swagger";
            
            try
            {
                // Set doc expansion with fallback
                var docExpansion = ParseEnum<Swashbuckle.AspNetCore.SwaggerUI.DocExpansion>(
                    swaggerSettings.DocExpansion, 
                    Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
                c.DocExpansion(docExpansion);
                
                // Apply other settings
                c.DefaultModelsExpandDepth(swaggerSettings.DefaultModelsExpandDepth);
                
                if (swaggerSettings.DisplayRequestDuration)
                    c.DisplayRequestDuration();
                    
                if (swaggerSettings.EnableFilter)
                    c.EnableFilter();
                    
                if (swaggerSettings.EnableDeepLinking)
                    c.EnableDeepLinking();
                    
                if (swaggerSettings.EnableValidator)
                    c.EnableValidator();
            }
            catch (Exception ex)
            {
                // Log but don't crash if there's an issue with UI configuration
                Log.Warning(ex, "⚠️ Error applying Swagger UI settings. Using defaults.");
                
                // Apply sensible defaults
                c.DocExpansion(Swashbuckle.AspNetCore.SwaggerUI.DocExpansion.List);
                c.DefaultModelsExpandDepth(-1);
                c.DisplayRequestDuration();
            }
        });
        
        Log.Information("✅ Swagger UI configured successfully");
    }

    // Helper method for safely parsing enums with fallback
    public static T ParseEnum<T>(string value, T defaultValue) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value) || !Enum.TryParse<T>(value, true, out var result))
        {
            return defaultValue;
        }
        return result;
    }
}