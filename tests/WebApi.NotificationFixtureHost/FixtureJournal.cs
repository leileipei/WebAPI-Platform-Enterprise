using System.Text.Json;
namespace WebApi.NotificationFixtureHost;
// One fixture process owns this append-only journal. Receipts are flushed before a wire success.
public sealed class FixtureJournal
{
 private readonly object gate=new();private readonly string? directory;private readonly List<SmtpFixtureReceipt> smtp=[];private readonly List<WebhookFixtureReceipt> webhook=[];private long bytes;
 public IReadOnlyList<SmtpFixtureReceipt> Smtp{get{lock(gate)return smtp.ToArray();}}
 public IReadOnlyList<WebhookFixtureReceipt> Webhook{get{lock(gate)return webhook.ToArray();}}
 public FixtureJournal(string? directory=null,string? ownerId=null,string? projectName=null)
 {
  this.directory=directory;if(directory is null)return;
  if(!Path.IsPathFullyQualified(directory)||!Guid.TryParseExact(ownerId,"D",out var owner)||owner==Guid.Empty||projectName is null||!System.Text.RegularExpressions.Regex.IsMatch(projectName,"^webapi-enterprise-local(?:-test-[a-f0-9-]{36})?$"))throw new InvalidOperationException("Invalid fixture journal owner.");
  for(var parent=new DirectoryInfo(directory);parent is not null;parent=parent.Parent)if(parent.LinkTarget is not null)throw new InvalidOperationException("Fixture journal symlink rejected.");
  Directory.CreateDirectory(directory);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
  for(var parent=new DirectoryInfo(directory);parent is not null;parent=parent.Parent)if(parent.LinkTarget is not null)throw new InvalidOperationException("Fixture journal symlink rejected.");
  var identityFile=Path.Combine(directory,"owner.json");var identity=JsonSerializer.Serialize(new{ownerId,projectName});
  if(File.Exists(identityFile)){PrivateFile(identityFile);if(File.ReadAllText(identityFile)!=identity)throw new InvalidOperationException("Foreign fixture journal owner.");}
  else{if(Directory.EnumerateFileSystemEntries(directory).Any())throw new InvalidOperationException("Incomplete fixture journal ownership.");using var file=new FileStream(identityFile,FileMode.CreateNew,FileAccess.Write,FileShare.None);var body=System.Text.Encoding.UTF8.GetBytes(identity);file.Write(body);file.Flush(true);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(identityFile,UnixFileMode.UserRead|UnixFileMode.UserWrite);}
  foreach(var name in new[]{"smtp","webhook"}){var path=Path.Combine(directory,name+".jsonl");if(!File.Exists(path))continue;PrivateFile(path);var info=new FileInfo(path);if(info.Length>16*1024*1024)throw new InvalidOperationException("Fixture journal full.");bytes+=info.Length;foreach(var line in File.ReadLines(path)){if(string.IsNullOrWhiteSpace(line)||line.Length>8192)throw new InvalidOperationException("Fixture journal damaged.");try{if(name=="smtp")smtp.Add(JsonSerializer.Deserialize<SmtpFixtureReceipt>(line)??throw new JsonException());else webhook.Add(JsonSerializer.Deserialize<WebhookFixtureReceipt>(line)??throw new JsonException());}catch(JsonException){throw new InvalidOperationException("Fixture journal damaged.");}}}
  if(smtp.Count+webhook.Count>10000||bytes>16*1024*1024)throw new InvalidOperationException("Fixture journal full.");
 }
 private static void PrivateFile(string path){var info=new FileInfo(path);if(info.LinkTarget is not null||!OperatingSystem.IsWindows()&&(File.GetUnixFileMode(path)&(UnixFileMode.GroupRead|UnixFileMode.GroupWrite|UnixFileMode.GroupExecute|UnixFileMode.OtherRead|UnixFileMode.OtherWrite|UnixFileMode.OtherExecute))!=0)throw new InvalidOperationException("Fixture journal must be private.");}
 private void Persist<T>(string name,T receipt)
 {
  if(directory is null)return;var body=System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(receipt)+"\n");if(smtp.Count+webhook.Count>=10000||bytes+body.Length>16*1024*1024)throw new IOException("Fixture journal full.");var path=Path.Combine(directory,name+".jsonl");if(File.Exists(path))PrivateFile(path);using var file=new FileStream(path,FileMode.Append,FileAccess.Write,FileShare.Read);if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite);file.Write(body);file.Flush(true);bytes+=body.Length;
 }
 public static void Rebind(string directory,string oldOwner,string oldProject,string newOwner,string newProject)
 {
  _=new FixtureJournal(directory,oldOwner,oldProject);if(!Guid.TryParseExact(newOwner,"D",out var owner)||owner==Guid.Empty||!System.Text.RegularExpressions.Regex.IsMatch(newProject,"^webapi-enterprise-local-test-[a-f0-9-]{36}$"))throw new InvalidOperationException("Rebind requires a fresh disposable owner.");var path=Path.Combine(directory,"owner.json");var temp=path+"."+Guid.NewGuid()+".tmp";try{using(var file=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None)){if(!OperatingSystem.IsWindows())File.SetUnixFileMode(temp,UnixFileMode.UserRead|UnixFileMode.UserWrite);file.Write(System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{ownerId=newOwner,projectName=newProject})));file.Flush(true);}File.Move(temp,path,true);}finally{if(File.Exists(temp))File.Delete(temp);}
 }
 public void Append(SmtpFixtureReceipt receipt){lock(gate){Persist("smtp",receipt);smtp.Add(receipt);}}
 public void Append(WebhookFixtureReceipt receipt){lock(gate){Persist("webhook",receipt);webhook.Add(receipt);}}
}
