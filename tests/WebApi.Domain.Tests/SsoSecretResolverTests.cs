using Microsoft.Extensions.Options;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Sso;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class SsoSecretResolverTests
{
    [Fact] public async Task SecretReferenceNeverSelectsArbitraryPath()
    {
        var dir=Directory.CreateTempSubdirectory("sso-secret-");var path=Path.Combine(dir.FullName,"client");await File.WriteAllTextAsync(path,"fixture-secret-value\n");
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
        var resolver=new SsoSecretResolver(Options.Create(new SsoOptions{SecretFiles=new(){{"enterprise",path}}}));
        try {
            Assert.Equal("fixture-secret-value",await resolver.ResolveAsync("file://sso/enterprise"));
            foreach(var reference in new[]{"file:///etc/passwd","file://sso/../client","file://sso/missing","https://secrets.invalid/value"})
            {
                var error=await Assert.ThrowsAsync<ApiException>(()=>resolver.ResolveAsync(reference));
                Assert.DoesNotContain(path,error.Message);Assert.DoesNotContain("fixture-secret-value",error.Message);
            }
        } finally{dir.Delete(true);}
    }
    [Fact] public async Task MissingEmptySymbolicAndPublicFilesFailSafely()
    {
        var dir=Directory.CreateTempSubdirectory("sso-secret-");var path=Path.Combine(dir.FullName,"client");var link=Path.Combine(dir.FullName,"link");
        try {
            var resolver=new SsoSecretResolver(Options.Create(new SsoOptions{SecretFiles=new(){{"client",path},{"link",link}}}));
            await Assert.ThrowsAsync<ApiException>(()=>resolver.ResolveAsync("file://sso/client"));
            await File.WriteAllTextAsync(path,"");if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);
            await Assert.ThrowsAsync<ApiException>(()=>resolver.ResolveAsync("file://sso/client"));
            await File.WriteAllTextAsync(path,"fixture-secret");File.CreateSymbolicLink(link,path);
            await Assert.ThrowsAsync<ApiException>(()=>resolver.ResolveAsync("file://sso/link"));
            if(!OperatingSystem.IsWindows()){File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.OtherRead);await Assert.ThrowsAsync<ApiException>(()=>resolver.ResolveAsync("file://sso/client"));}
        } finally{dir.Delete(true);}
    }
}
