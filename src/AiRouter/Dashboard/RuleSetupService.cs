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

    /// <summary>Validate that a process filename is non-empty</summary>
    public static bool IsValidFileName(this ProcessConfig process)
    {
        if (process is null)
            return true; // No process is valid

        return !string.IsNullOrWhiteSpace(process.FileName);
    }
}

/// <summary>
/// Event args for rule changes.
/// </summary>
public class RuleChangedEventArgs : EventArgs
{
    public List<RoutingRuleConfig> Rules { get; init; }
    public RuleChangedEventArgs(List<RoutingRuleConfig> rules)
    {
        Rules = rules;
    }
}

/// <summary>
/// Service to manage routing rules configuration with CRUD operations.
/// Changes are saved to appsettings.json and automatically trigger config reload.
/// </summary>
public sealed class RuleSetupService : IDisposable
{
    private readonly IConfigurationRoot _config;
    private readonly string _configPath;
    private bool _disposed = false;

    public RuleSetupService(IConfigurationRoot config)
    {
        _config = config;
        _configPath = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
    }

    /// <summary>Event fired when rules are saved (after config reload completes)</summary>
    public event EventHandler<RuleChangedEventArgs>? RulesChanged;

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

        // Serialize rules to JSON
        var rulesJson = JsonSerializer.Serialize(rules, new JsonSerializerOptions { WriteIndented = true });

        // Build new JSON object with updated RoutingRules
        var newJson = BuildUpdatedConfig(currentContent, rulesJson);

        // Write the updated config back to file
        File.WriteAllText(_configPath, newJson);

        // Reload the configuration
        _config.Reload();

        // Notify subscribers that rules have changed
        RulesChanged?.Invoke(this, new RuleChangedEventArgs(rules));
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

    private string BuildUpdatedConfig(string originalJson, string rulesJson)
    {
        // Use JsonDocument to parse and build new JSON
        var options = new JsonSerializerOptions { WriteIndented = true };

        // Sanitize input to remove comments before parsing
        var sanitizedJson = RemoveJsonComments(originalJson);

        // Parse the original JSON
        using var doc = JsonDocument.Parse(sanitizedJson);
        var rootElement = doc.RootElement.Clone();

        // Create a new object and populate it
        var newRoot = new JsonObject();
        newRoot["RoutingRules"] = JsonNode.Parse(rulesJson);

        // Copy other properties from original config
        if (rootElement.TryGetProperty("ApiKeys", out var apiKeys))
        {
            newRoot["ApiKeys"] = JsonNode.Parse(apiKeys.GetRawText());
        }
        if (rootElement.TryGetProperty("DefaultApiKey", out var defaultApiKey))
        {
            newRoot["DefaultApiKey"] = JsonNode.Parse(defaultApiKey.GetRawText());
        }
        if (rootElement.TryGetProperty("Host", out var host))
        {
            newRoot["Host"] = JsonNode.Parse(host.GetRawText());
        }
        if (rootElement.TryGetProperty("Port", out var port))
        {
            newRoot["Port"] = JsonNode.Parse(port.GetRawText());
        }

        return newRoot.ToJsonString(options);
    }

    // ── Comment removal helpers ──────────────────────────────────────────────

    /// <summary>Remove single-line (//) and multi-line (/* */) comments from JSON</summary>
    private static string RemoveJsonComments(string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var result = new System.Text.StringBuilder(input.Length);
        int i = 0;
        int length = input.Length;

        while (i < length)
        {
            // Check for single-line comment
            if (i + 1 < length && input[i] == '/' && input[i + 1] == '/')
            {
                // Skip until end of line
                while (i < length && input[i] != '\n')
                    i++;
                // Keep the newline
                if (i < length)
                    result.Append(input[i++]);
            }
            // Check for multi-line comment
            else if (i + 1 < length && input[i] == '/' && input[i + 1] == '*')
            {
                // Skip until */
                i += 2;
                while (i + 1 < length && !(input[i] == '*' && input[i + 1] == '/'))
                    i++;
                i += 2; // Skip */
            }
            // Check for string literal - preserve content but watch for escaped quotes
            else if (input[i] == '"')
            {
                result.Append(input[i++]);
                while (i < length && input[i] != '"')
                {
                    if (input[i] == '\\' && i + 1 < length)
                    {
                        result.Append(input[i++]);
                        if (i < length)
                            result.Append(input[i++]);
                    }
                    else
                    {
                        result.Append(input[i++]);
                    }
                }
                if (i < length)
                    result.Append(input[i++]); // Closing quote
            }
            else
            {
                result.Append(input[i++]);
            }
        }

        return result.ToString();
    }

    public void Dispose()
    {
        _disposed = true;
        RulesChanged = null;
    }
}
