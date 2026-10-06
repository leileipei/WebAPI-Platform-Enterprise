namespace WebApi.Infrastructure.Contracts;
public sealed record SchemaValidationSettings(string RuntimeToolPath="/app/runtime-tool/WebApi.RuntimeTool.dll",string DotnetPath="dotnet");
