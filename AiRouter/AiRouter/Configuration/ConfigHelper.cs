namespace AiRouter;

class ConfigHelper
{
    // Resolves ${VAR_NAME} placeholders against IConfiguration (env vars / appsettings)
    public static string Resolve(string value, IConfiguration config)
    {
        if (value.StartsWith("${") && value.EndsWith("}"))
        {
            var name = value[2..^1];
            return config[name] ?? value;
        }
        return value;
    }

    public static Dictionary<string, string> ResolveAll(Dictionary<string, string> dict, IConfiguration config)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in dict)
            result[k] = Resolve(v, config);
        return result;
    }
}
