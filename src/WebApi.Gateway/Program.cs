using WebApi.Gateway;
if(args.FirstOrDefault()=="usage-rebind")
{
    System.Environment.ExitCode=await WebApi.Gateway.CredentialUsage.UsageCloneCommand.RunAsync(args[1..],Console.Out);return;
}
await GatewayApp.Build(args).RunAsync();
