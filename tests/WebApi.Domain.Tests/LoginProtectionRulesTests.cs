using Xunit;
using WebApi.Contracts.Security;
using WebApi.Domain.Security;
using WebApi.Infrastructure.Security;
using System.Net;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
namespace WebApi.Domain.Tests;
public sealed class LoginProtectionRulesTests
{
    [Fact] public void DedicatedSecretRejectsLengthPermissionsAndSymlinks()
    {
        var directory=Path.Combine(Path.GetTempPath(),"login-secret-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
        try
        {
            var file=Path.Combine(directory,"secret");File.WriteAllText(file,Convert.ToBase64String(new byte[32])+"\n");
            if(!OperatingSystem.IsWindows())File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>{{"Redis:Connection","redis:6379"},{"Authentication:LoginProtection:RedisPrefix","test"},{"Authentication:LoginProtection:HmacSecretFile",file}}).Build();
            Assert.Equal(32,LoginProtectionDeploymentSettings.Read(config).HmacSecret.Length);
            File.WriteAllText(file,Convert.ToBase64String(new byte[31]));Assert.Throws<InvalidOperationException>(()=>LoginProtectionDeploymentSettings.Read(config));
            File.WriteAllText(file,Convert.ToBase64String(new byte[32]));
            if(!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.OtherRead);Assert.Throws<InvalidOperationException>(()=>LoginProtectionDeploymentSettings.Read(config));
                File.SetUnixFileMode(file,UnixFileMode.UserRead|UnixFileMode.UserWrite);
                var link=Path.Combine(directory,"link");File.CreateSymbolicLink(link,file);config["Authentication:LoginProtection:HmacSecretFile"]=link;Assert.Throws<InvalidOperationException>(()=>LoginProtectionDeploymentSettings.Read(config));
            }
        }
        finally{Directory.Delete(directory,true);}
    }
    [Fact] public void FingerprintOnlyUsesBudgetValues()
    {
        var limits=new LoginRateLimits(60,60,10,300);
        Assert.Equal(LoginProtectionRules.Fingerprint(limits),LoginProtectionRules.Fingerprint(new(60,60,10,300)));
        Assert.NotEqual(LoginProtectionRules.Fingerprint(limits),LoginProtectionRules.Fingerprint(new(61,60,10,300)));
    }
    [Theory][InlineData(0,60,10,300)][InlineData(10001,60,10,300)][InlineData(60,3601,10,300)][InlineData(60,60,0,300)][InlineData(60,60,10,0)]
    public void InvalidLimitsRejected(int a,int b,int c,int d)=>Assert.Throws<ArgumentOutOfRangeException>(()=>LoginProtectionRules.Validate(new(a,b,c,d)));
    [Fact] public void HmacNormalizesMappedIpButPreservesAccountCase()
    {
        var hasher=new LoginKeyHasher(RandomNumberGenerator.GetBytes(32));
        var first=hasher.Keys(IPAddress.Parse("192.0.2.12"),"CanaryUser");
        Assert.Equal(first.Ip,hasher.Keys(IPAddress.Parse("::ffff:192.0.2.12"),"CanaryUser").Ip);
        Assert.NotEqual(first.Account,hasher.Keys(IPAddress.Parse("192.0.2.12"),"canaryuser").Account);
        Assert.NotEqual(first.Account,first.AuditAccount);
        Assert.DoesNotContain("CanaryUser",first.Account);Assert.DoesNotContain("192.0.2.12",first.Ip);
        Assert.Throws<ArgumentException>(()=>new LoginKeyHasher(new byte[31]));
    }
}
