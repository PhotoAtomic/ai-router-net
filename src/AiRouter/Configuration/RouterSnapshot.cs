using AiRouter.Routing;

namespace AiRouter;

// Router configuration snapshot (immutable, swapped atomically on reload)
record RouterSnapshot(
    List<RoutingRule> Rules,
    Dictionary<string, string> ApiKeys,
    string DefaultApiKey);
