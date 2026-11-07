using System.Security.Cryptography;
using WebApi.Contracts.Security;
using WebApi.Domain.CredentialSecurity;
using WebApi.Gateway.Security;
using WebApi.Gateway.Storage;
using WebApi.Infrastructure.CredentialSecurity;
namespace WebApi.Gateway.Tests.Support;
internal sealed class SecurityFixture:IDisposable
{
 public readonly Guid Env=Guid.NewGuid(),Generation=Guid.NewGuid(),Node=Guid.NewGuid(),Instance=Guid.NewGuid();
 public readonly string DirectoryPath=Path.Combine(Path.GetTempPath(),"security-state-"+Guid.NewGuid().ToString("N"));
 public readonly SecurityTestClock Clock=new();public readonly SecurityCheckpointSigner Signer;public readonly TrustedSigningKeys Keys;public readonly SecurityLkgStore Disk;public readonly SecurityStateStore State;
 private readonly Dictionary<Guid,SecurityRevocationProof> proofs=[];
 public SecurityFixture(){using var key=ECDsa.Create(ECCurve.NamedCurves.nistP256);Signer=new("test",key.ExportPkcs8PrivateKeyPem());Keys=new(new Dictionary<string,string>{{"test",Convert.ToBase64String(key.ExportSubjectPublicKeyInfo())}});Disk=new(DirectoryPath);State=new(new(Env,()=>Node,Instance),Disk,Clock);}
 public (VerifiedCheckpoint Checkpoint,VerifiedNodeLease Lease) Pair(long epoch,Guid[] ids,string? head=null,bool full=true,long? baseEpoch=null,Guid? leaseNode=null,Guid? leaseInstance=null,Guid? generation=null,string minimumSchema="2.0",bool requireEnvironmentCredentials=false)
 {
  foreach(var id in ids)if(!proofs.ContainsKey(id))proofs.Add(id,new(id,Guid.NewGuid(),proofs.Count+1,Clock.GetUtcNow(),Convert.ToHexStringLower(SHA256.HashData(id.ToByteArray()))));
  var now=Clock.GetUtcNow();var payload=new SecurityCheckpointPayload("security-ledger-v1",Env,generation??Generation,epoch,head??Convert.ToHexStringLower(SHA256.HashData(BitConverter.GetBytes(epoch))),ids.Select(id=>proofs[id]).ToArray(),requireEnvironmentCredentials,minimumSchema,now,now.AddSeconds(30),"test",full,baseEpoch);
  var checkpoint=SecurityCheckpointVerifier.VerifyCheckpoint(new(Signer.SignCheckpoint(payload)),Keys,Env);
  var node=leaseNode??Node;var instance=leaseInstance??Instance;var lease=new SecurityNodeLeasePayload(Guid.NewGuid(),"security-ledger-v1",Env,payload.SecurityGeneration,epoch,checkpoint.Hash,node,instance,"webapi.gateway.security",now,now.AddSeconds(30),"test");return(checkpoint,SecurityCheckpointVerifier.VerifyNodeLease(new(Signer.SignNodeLease(lease)),Keys,Env,node,instance));
 }
 public void Dispose(){State.DisposeAsync().AsTask().GetAwaiter().GetResult();if(Directory.Exists(DirectoryPath)){if(!OperatingSystem.IsWindows())File.SetUnixFileMode(DirectoryPath,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);Directory.Delete(DirectoryPath,true);}}
}
internal sealed class SecurityTestClock:TimeProvider
{
 private DateTimeOffset utc=new(2026,10,9,0,0,0,TimeSpan.Zero);private long ticks;
 public override DateTimeOffset GetUtcNow()=>utc;public override long GetTimestamp()=>ticks;public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
 public void Advance(double seconds){utc=utc.AddSeconds(seconds);ticks+=(long)(seconds*TimeSpan.TicksPerSecond);}
 public void ShiftUtc(double seconds)=>utc=utc.AddSeconds(seconds);
 public void RewindTimestamp(double seconds)=>ticks-=(long)(seconds*TimeSpan.TicksPerSecond);
}
