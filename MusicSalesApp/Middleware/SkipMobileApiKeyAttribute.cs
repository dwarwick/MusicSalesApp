namespace MusicSalesApp.Middleware;

[AttributeUsage(AttributeTargets.Method)]
public sealed class SkipMobileApiKeyAttribute : Attribute
{
}