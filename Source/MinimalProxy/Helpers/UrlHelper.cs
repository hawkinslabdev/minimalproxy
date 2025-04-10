namespace MinimalProxy.Helpers;

using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public static class UrlHelper
{
    // URL rewriting helper method
    public static string RewriteUrl(string content, string originalHost, string originalPath, string proxyHost, string proxyPath)
    {
        if (string.IsNullOrWhiteSpace(content)) return content;

        string originalBaseUrl = $"{originalHost}{originalPath}".TrimEnd('/');
        string proxyBaseUrl = $"{proxyHost}{proxyPath}".TrimEnd('/');

        try
        {
            var xml = XDocument.Parse(content);

            foreach (var element in xml.Descendants())
            {
                // Replace xml:base manually — it's namespaced
                var xmlBaseAttr = element.Attribute(XNamespace.Xml + "base");
                if (xmlBaseAttr != null && xmlBaseAttr.Value.StartsWith(originalBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    xmlBaseAttr.Value = xmlBaseAttr.Value.Replace(originalBaseUrl, proxyBaseUrl, StringComparison.OrdinalIgnoreCase);
                }

                // Rewrite regular attributes (like href, id, etc.)
                foreach (var attr in element.Attributes())
                {
                    if (attr.IsNamespaceDeclaration) continue;

                    // ✅ Handle xml:base
                    if (attr.Name.LocalName == "base" && attr.Name.Namespace == XNamespace.Xml &&
                        attr.Value.StartsWith(originalBaseUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        attr.Value = attr.Value.Replace(originalBaseUrl, proxyBaseUrl, StringComparison.OrdinalIgnoreCase);
                        continue;
                    }

                    // ✅ Rewrite only if value starts with the original base URL
                    if (attr.Value.StartsWith(originalBaseUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        attr.Value = attr.Value.Replace(originalBaseUrl, proxyBaseUrl, StringComparison.OrdinalIgnoreCase);
                    }

                    // ❌ DO NOT rewrite again if it's already rewritten!
                    else if (attr.Value.StartsWith(proxyBaseUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        // Already rewritten — skip
                    }

                    // ✅ Handle relative hrefs like href="Account(...)"
                    else if (attr.Name.LocalName == "href" &&
                            !attr.Value.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                            !attr.Value.StartsWith("/", StringComparison.OrdinalIgnoreCase))
                    {
                        // Only prefix once
                        attr.Value = $"{proxyPath}/{attr.Value}".TrimEnd('/');
                    }
                }

                // Rewrite element values like <id>
                if (!element.HasElements && element.Value.StartsWith(originalBaseUrl, StringComparison.OrdinalIgnoreCase))
                {
                    element.Value = element.Value
                        .Replace(originalBaseUrl, proxyBaseUrl, StringComparison.OrdinalIgnoreCase)
                        .Replace(proxyHost, proxyBaseUrl, StringComparison.OrdinalIgnoreCase);
                }
            }

            return xml.Declaration?.ToString() + Environment.NewLine + xml.ToString(SaveOptions.DisableFormatting);
        }
        catch
        {
            // Fallback to regex (for JSON)
            string escaped = Regex.Escape(originalBaseUrl);
            return Regex.Replace(content, @$"{escaped}(/[^""'\s]*)?", match =>
            {
                var suffix = match.Value.Substring(originalBaseUrl.Length);
                return proxyBaseUrl + suffix;
            }, RegexOptions.IgnoreCase);
        }
    }

    // Helper for content type
    public static string GetSafeContentType(HttpResponseMessage response, string fallback = "application/json")
    {
        return response.Content?.Headers?.ContentType?.ToString() ?? fallback;
    }
}