using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiRouter.Process;

namespace AiRouter.Dashboard;

/// <summary>
/// Extension methods for RoutingRuleConfig to support validation.
/// </summary>
public static class RoutingRuleConfigExtensions
{
    /// <summary>Validate that a regex pattern is valid</summary>
    public static bool IsValidPattern(this RoutingRuleConfig rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Pattern))
            return false;
        try
        {
            new System.Text.RegularExpressions.Regex(rule.Pattern, System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Validate that a process filename exists on disk</summary>
    public static bool IsValidFileName(this ProcessConfig process)
    {
        if (process is null)
            return true; // No process is valid

        var fileName = process.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
            return false;

        // Check if it's a full path or just a filename
        if (Path.IsPathRooted(fileName))
            return File.Exists(fileName);

        // Check in current directory and PATH
        if (File.Exists(fileName))
            return true;

        // Check PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var pathDir in pathEnv.Split(Path.PathSeparator))
        {
            if (Directory.Exists(pathDir))
            {
                var fullPath = Path.Combine(pathDir, fileName);
                if (File.Exists(fullPath))
                    return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Service to manage routing rules configuration with CRUD operations.
/// Changes are saved to appsettings.json and automatically trigger config reload.
/// </summary>
public sealed class RuleSetupService
{
    private readonly IConfigurationRoot _config;
    private readonly string _configPath;

    public RuleSetupService(IConfigurationRoot config)
    {
        _config = config;
        _configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
    }

    /// <summary>Get all current routing rules</summary>
    public List<RoutingRuleConfig> GetRules()
    {
        var rules = _config.GetSection("RoutingRules").Get<List<RoutingRuleConfig>>();
        return rules ?? new List<RoutingRuleConfig>();
    }

    /// <summary>Save all routing rules to config file</summary>
    public void SaveRules(List<RoutingRuleConfig> rules)
    {
        // Read the current appsettings.json content
        var currentContent = File.ReadAllText(_configPath);

        // Parse the JSON
        using var doc = JsonDocument.Parse(currentContent);
        var rootElement = doc.RootElement.Clone();

        // Serialize rules to JSON
        var rulesJson = JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true });

        // Build new JSON object with updated RoutingRules
        var newJson = BuildUpdatedConfig(rootElement, rulesJson);

        // Write the updated config back to file
        File.WriteAllText(_configPath, newJson);

        // Reload the configuration
        _config.Reload();
    }

    /// <summary>Add a new rule at the specified position (default: end)</summary>
    public void AddRule(RoutingRuleConfig rule, int position = -1)
    {
        var rules = GetRules();
        if (position < 0 || position >= rules.Count)
            rules.Add(rule);
        else
            rules.Insert(position, rule);
        SaveRules(rules);
    }

    /// <summary>Update an existing rule at the specified index</summary>
    public void UpdateRule(int index, RoutingRuleConfig rule)
    {
        var rules = GetRules();
        if (index >= 0 && index < rules.Count)
        {
            rules[index] = rule;
            SaveRules(rules);
        }
    }

    /// <summary>Delete a rule at the specified index</summary>
    public void DeleteRule(int index)
    {
        var rules = GetRules();
        if (index >= 0 && index < rules.Count)
        {
            rules.RemoveAt(index);
            SaveRules(rules);
        }
    }

    /// <summary>Move a rule up (decrease index)</summary>
    public void MoveUp(int index)
    {
        var rules = GetRules();
        if (index > 0 && index < rules.Count)
        {
            var rule = rules[index];
            rules.RemoveAt(index);
            rules.Insert(index - 1, rule);
            SaveRules(rules);
        }
    }

    /// <summary>Move a rule down (increase index)</summary>
    public void MoveDown(int index)
    {
        var rules = GetRules();
        if (index >= 0 && index < rules.Count - 1)
        {
            var rule = rules[index];
            rules.RemoveAt(index);
            rules.Insert(index + 1, rule);
            SaveRules(rules);
        }
    }

    // ── Private helpers ──────────────────────────────────────────────────────

    private string BuildUpdatedConfig(JsonElement originalRoot, string rulesJson)
    {
        // Use JsonDocument to parse and build new JSON
        var options = new JsonSerializerOptions { WriteIndented = true };

        // Create a new object and populate it
        var newRoot = new JsonObject
        {
            ["RoutingRules"] = JsonNode.Parse(rulesJson)
        };

        // Copy other properties from original config
        if (originalRoot.TryGetProperty("ApiKeys", out var apiKeys))
        {
            newRoot["ApiKeys"] = JsonNode.Parse(apiKeys.GetRawText());
        }
        if (originalRoot.TryGetProperty("DefaultApiKey", out var defaultApiKey))
        {
            newRoot["DefaultApiKey"] = JsonNode.Parse(defaultApiKey.GetRawText());
        }
        if (originalRoot.TryGetProperty("Host", out var host))
        {
            newRoot["Host"] = JsonNode.Parse(host.GetRawText());
        }
        if (originalRoot.TryGetProperty("Port", out var port))
        {
            newRoot["Port"] = JsonNode.Parse(port.GetRawText());
        }

        return newRoot.ToJsonString(options);
    }
}
