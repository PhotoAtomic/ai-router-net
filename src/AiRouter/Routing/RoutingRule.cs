using System.Text.RegularExpressions;
using AiRouter.Process;

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
    public Regex CompiledRegex        { get; }
    public ProcessManager? ProcessManager { get; }
}
