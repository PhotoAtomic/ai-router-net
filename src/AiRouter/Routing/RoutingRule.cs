using System.Text.RegularExpressions;
using AiRouter.Process;
using AiRouter.Protocol;

namespace AiRouter.Routing;

// Runtime wrapper: holds the compiled/resolved fields alongside the raw config.
class RoutingRule
{
    public RoutingRule(RoutingRuleConfig cfg, Regex regex, ProcessManager? mgr)
    {
        Config        = cfg;
        CompiledRegex = regex;
        ProcessManager = mgr;
    }

    public RoutingRuleConfig Config   { get; }
    public string Pattern             => Config.Pattern;
    public string BaseUrl             => Config.BaseUrl;
    public string? ForceModel         => Config.ForceModel;
    public ProcessConfig? Process     => Config.Process;
    public bool EnableLLamaCppModelRecover => Config.EnableLLamaCppModelRecover;
    public bool IsLLamaCpp             => Config.IsLLamaCpp;
    public Regex CompiledRegex        { get; }
    public ProcessManager? ProcessManager { get; }

    public ApiFormat TargetFormat =>
        Config.EndpointType.Equals("OpenAI", StringComparison.OrdinalIgnoreCase)
            ? ApiFormat.OpenAI
            : ApiFormat.Anthropic;
}
