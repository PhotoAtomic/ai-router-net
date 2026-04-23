using AiRouter.Process;

namespace AiRouter;

// Plain data class bound by IConfiguration — only simple/supported property types.
// Keeping Regex and ProcessManager out of this class eliminates SYSLIB1100/1101.
class RoutingRuleConfig
{
    public string Pattern { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = string.Empty;
    public string? ForceModel { get; set; }
    public ProcessConfig? Process { get; set; }
}
