namespace WebApi.Domain.Routing;

public static class RouteHttpMethods
{
    public static IReadOnlyList<string> Supported { get; } =
        ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"];
}
