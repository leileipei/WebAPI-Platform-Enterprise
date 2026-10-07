using System.Security.Cryptography;
using System.Text.Json;
namespace WebApi.NotificationFixtureHost;
public sealed class FixtureRuntimeIdentity
{
 public string? SourceRevision {get;}
 public string? BinaryHash {get;}
 public bool BinaryVerified {get;}
 public FixtureRuntimeIdentity(IConfiguration configuration)
 {
  var file=configuration["Fixture:BuildIdentityFile"];if(file is null)return;
  if(!Path.IsPathFullyQualified(file)||new FileInfo(file).Length>65536)throw new InvalidOperationException("Invalid fixture build identity.");using var document=JsonDocument.Parse(File.ReadAllText(file));var root=document.RootElement;var revision=root.GetProperty("sourceRevision").GetString();if(revision is null||!System.Text.RegularExpressions.Regex.IsMatch(revision,"^[a-f0-9]{40}$"))throw new InvalidOperationException("Invalid fixture source identity.");var binary=typeof(FixtureRuntimeIdentity).Assembly.Location;var expected=root.GetProperty("applications").GetProperty("notification-fixture/WebApi.NotificationFixtureHost.dll").GetString();var actual=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(binary)));if(expected!=actual)throw new InvalidOperationException("Fixture binary identity mismatch.");SourceRevision=revision;BinaryHash=actual;BinaryVerified=true;
 }
}
