using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Settings;
public sealed class SettingsPreviewProtector(IDataProtectionProvider provider,TimeProvider clock)
{
    private sealed record Claim(Guid Actor,string Group,long Revision,string ValueHash,DateTimeOffset ExpiresAt);
    private readonly IDataProtector protector=provider.CreateProtector("WebApi.SystemSettings.Preview.v1");
    public string Issue(Guid actorId,string group,long revision,string valueHash)=>protector.Protect(JsonSerializer.Serialize(new Claim(actorId,group,revision,valueHash,clock.GetUtcNow().AddMinutes(5))));
    public void Require(string token,Guid actorId,string group,long revision,string valueHash)
    {
        try {var claim=JsonSerializer.Deserialize<Claim>(protector.Unprotect(token));if(claim is null||claim.Actor!=actorId||claim.Group!=group||claim.Revision!=revision||claim.ValueHash!=valueHash||claim.ExpiresAt<=clock.GetUtcNow())throw new InvalidOperationException();}
        catch(Exception e) when(e is not OutOfMemoryException){throw new ApiException(422,"settings_confirmation_required","确认凭证已失效或与当前修改不匹配，请重新预览。");}
    }
}
