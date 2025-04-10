using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using Serilog;

namespace MinimalProxy.Helpers;

public class UrlValidator
{
    private readonly List<string> _allowedHosts;
    private readonly List<string> _blockedRanges;
    private readonly ConcurrentDictionary<string, bool> _hostCache;
    private readonly ConcurrentDictionary<string, IPAddress[]> _dnsCache = new();

    public UrlValidator(string configPath)
    {
        _hostCache = new ConcurrentDictionary<string, bool>();

        // Ensure the configuration file exists
        EnsureConfigFileExists(configPath);

        // Load configuration
        var config = JsonSerializer.Deserialize<HostConfig>(
            File.ReadAllText(configPath), 
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }
        );

        if (config == null)
        {
            throw new InvalidOperationException("Configuration could not be loaded.");
        }

        // Start with default localhost hosts
        _allowedHosts = new List<string> 
        { 
            "localhost", 
            "127.0.0.1" 
        };
        
        // Add dynamically discovered hosts
        _allowedHosts.AddRange(DiscoverAllowedHosts());

        _blockedRanges = config.BlockedIpRanges?.Count > 0
            ? config.BlockedIpRanges
            : new List<string> 
            { 
                "10.0.0.0/8",
                "172.16.0.0/12",
                "192.168.0.0/16",
                "169.254.0.0/16"
            };

        Log.Information("🔒 URL Validator configured with allowed hosts: {Hosts}", 
            string.Join(", ", _allowedHosts));
    }

    private List<string> DiscoverAllowedHosts()
    {
        var discoveredHosts = new HashSet<string>();

        try
        {
            // Get local machine name
            discoveredHosts.Add(Environment.MachineName.ToLowerInvariant());

            // Get all network interfaces and their DNS addresses
            var networkInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var network in networkInterfaces)
            {
                // Skip loopback and non-operational interfaces
                if (network.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                // Get IP properties
                var ipProperties = network.GetIPProperties();

                // Add unicast addresses
                foreach (var unicast in ipProperties.UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        // Add IP address
                        discoveredHosts.Add(unicast.Address.ToString());

                        // Try to get hostname for the IP
                        try
                        {
                            var hostEntry = Dns.GetHostEntry(unicast.Address);
                            if (!string.IsNullOrEmpty(hostEntry.HostName))
                            {
                                discoveredHosts.Add(hostEntry.HostName.ToLowerInvariant());
                            }
                        }
                        catch
                        {
                            // Ignore DNS resolution errors
                        }
                    }
                }
            }

            // Add any configured domain if available
            var configuredDomain = GetConfiguredDomain();
            if (!string.IsNullOrEmpty(configuredDomain))
            {
                discoveredHosts.Add(configuredDomain.ToLowerInvariant());
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error discovering allowed hosts");
        }

        return discoveredHosts.ToList();
    }

    private string GetConfiguredDomain()
    {
        try
        {
            var envDomain = Environment.GetEnvironmentVariable("ASPNETCORE_DOMAIN");
            if (!string.IsNullOrEmpty(envDomain))
                return envDomain;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Error getting configured domain");
        }

        return string.Empty;
    }

    public bool IsUrlSafe(string url)
    {
        try
        {
            var uri = new Uri(url);
            string host = uri.Host;

            // Remove port if present
            host = host.Split(':')[0];

            Log.Debug("🕵️ Validating URL: {Url}", url);
            Log.Debug("🏠 Host to validate: {Host}", host);
            Log.Debug("✅ Allowed Hosts: {AllowedHosts}", string.Join(", ", _allowedHosts));

            // Check if host is in allowed hosts
            bool isHostAllowed = _allowedHosts.Any(allowed => 
                string.Equals(host, allowed, StringComparison.OrdinalIgnoreCase));

            if (!isHostAllowed)
            {
                Log.Error("❌ Host {Host} is NOT in allowed hosts", host);
                return false;
            }

            // Resolve and validate IP
            var addresses = Dns.GetHostAddresses(host);
            Log.Debug("🌐 Resolved Addresses: {Addresses}", 
                string.Join(", ", addresses.Select(a => a.ToString())));

            bool isIpAllowed = addresses.All(ip => 
                !_blockedRanges.Any(range => IsIpInRange(ip, range)));

            return isIpAllowed;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "❌ URL Validation Error for {Url}", url);
            return false;
        }
    }
    public bool IsHostAllowed(string host)
    {
        // Check cache first
        if (_hostCache.TryGetValue(host, out bool isAllowed))
            return isAllowed;

        // Validate host against allowed patterns
        if (IsHostPatternAllowed(host))
        {
            _hostCache[host] = true;
            return true;
        }

        // Validate host against DNS and IP ranges
        bool isValid = ValidateHost(host);
        _hostCache[host] = isValid;
        return isValid;
    }
    
    private void EnsureConfigFileExists(string configPath)
    {
        if (!File.Exists(configPath))
        {
            Log.Warning("Configuration file not found at {ConfigPath}. Creating default.", configPath);
            File.WriteAllText(configPath, JsonSerializer.Serialize(new HostConfig()));
        }
    }

    private bool ValidateHost(string host)
    {
         // Resolve DNS and check IP ranges
        var addresses = ResolveDnsWithCache(host);
        return addresses.All(IsIpAllowed);
    }

    private bool IsHostPatternAllowed(string host)
    {
        return _allowedHosts.Any(pattern => 
            MatchHostPattern(host, pattern));
    }

    private bool MatchHostPattern(string host, string pattern)
    {
        // Compiled regex for performance
        var regex = new Regex(
            "^" + Regex.Escape(pattern)
                .Replace(@"\*", "[^.]*") + "$", 
            RegexOptions.Compiled | RegexOptions.IgnoreCase
        );
        return regex.IsMatch(host);
    }

    private IPAddress[] ResolveDnsWithCache(string host)
    {
        // DNS resolution with caching
        return _dnsCache.GetOrAdd(host, key => 
        {
            try 
            {
                return Dns.GetHostAddresses(key) ?? Array.Empty<IPAddress>();
            }
            catch
            {
                return Array.Empty<IPAddress>();
            }
        });
    }

    private bool IsIpAllowed(IPAddress ip)
    {
        // Check against blocked ranges
        return !_blockedRanges.Any(range => IsIpInRange(ip, range));
    }

    private bool IsIpInRange(IPAddress ip, string cidrRange)
    {
        try
        {
            var parts = cidrRange.Split('/');
            var baseIp = IPAddress.Parse(parts[0]);
            var cidrBits = int.Parse(parts[1]);

            // Convert IPs to byte arrays for comparison
            byte[] ipBytes = ip.GetAddressBytes();
            byte[] baseIpBytes = baseIp.GetAddressBytes();

            // Ensure we're comparing the same IP type (IPv4)
            if (ipBytes.Length != baseIpBytes.Length)
                return false;

            // Create subnet mask
            byte[] maskBytes = new byte[ipBytes.Length];
            for (int i = 0; i < maskBytes.Length; i++)
            {
                int bitStart = i * 8;
                int bitEnd = Math.Min(bitStart + 8, cidrBits);
                int bits = bitEnd - bitStart;
                maskBytes[i] = (byte)(bits > 0 ? ((0xFF << (8 - bits)) & 0xFF) : 0);
            }

            // Compare masked IP addresses
            for (int i = 0; i < ipBytes.Length; i++)
            {
                if ((ipBytes[i] & maskBytes[i]) != (baseIpBytes[i] & maskBytes[i]))
                    return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private class HostConfig
    {
        public List<string>? AllowedHosts { get; set; }
        public List<string>? BlockedIpRanges { get; set; }
    }
}