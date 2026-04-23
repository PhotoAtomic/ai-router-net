namespace AiRouter.Serialization;

record ProxyErrorDetail(string type, string message);
record ProxyErrorResponse(ProxyErrorDetail error);
