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

    // When true, on a 500 response from the upstream the router will try to
    // recover by calling llama.cpp's /models endpoint to unload + reload the
    // target model (the one the request would actually be sent with, i.e.
    // ForceModel if set, else the requested model), then replay the request.
    public bool EnableLLamaCppModelRecover { get; set; }

    // When true, the dashboard will treat BaseUrl as a llama.cpp server and
    // display currently-loaded models with the ability to unload them.
    public bool IsLLamaCpp { get; set; }
}
