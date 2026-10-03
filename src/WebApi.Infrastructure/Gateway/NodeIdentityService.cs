using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Persistence;
using WebApi.Infrastructure.Persistence.Entities;
namespace WebApi.Infrastructure.Gateway;
public sealed record NodeIdentity(Guid NodeId,Guid EnvironmentId,string Hash);
public sealed record NodeEnrollment(Guid EnvironmentId,string NodeName,string Hash);
public sealed record NodeEnrollmentSettings(IReadOnlyList<NodeEnrollment> Enrollments)
{
    public static NodeEnrollmentSettings Read(IConfiguration config)
    {
        var entries=new List<NodeEnrollment>();foreach(var section in config.GetSection("Nodes:Enrollments").GetChildren())
        {
            if(!Guid.TryParse(section["EnvironmentId"],out var env)||env==Guid.Empty||string.IsNullOrWhiteSpace(section["NodeName"])||string.IsNullOrWhiteSpace(section["SecretFile"])) throw new InvalidOperationException("Node enrollment requires explicit environment, name and secret file.");
            var secret=File.ReadAllText(section["SecretFile"]!).Trim();if(secret.Length is <32 or >1024||secret.Any(char.IsWhiteSpace)) throw new InvalidOperationException("Invalid node secret file.");entries.Add(new(env,section["NodeName"]!,NodeIdentityService.Hash(secret)));
        }
        if(entries.Select(e=>(e.EnvironmentId,e.NodeName)).Distinct().Count()!=entries.Count||entries.Select(e=>e.Hash).Distinct().Count()!=entries.Count) throw new InvalidOperationException("Node enrollments and secrets must be unique.");return new(entries);
    }
}
public sealed class NodeIdentityService(WebApiDbContext db,NodeEnrollmentSettings settings)
{
    public static string Hash(string secret)=>Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(secret)));
    public static bool Matches(string supplied,string hash)=>hash.Length==64&&supplied.Length is >=32 and <=1024&&CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(supplied)),Encoding.ASCII.GetBytes(hash));
    public string EnrollmentHash(Guid env,string name,string secret)
    {var enrollment=settings.Enrollments.SingleOrDefault(e=>e.EnvironmentId==env&&e.NodeName==name);if(enrollment is null||!Matches(secret,enrollment.Hash)) throw Denied();return enrollment.Hash;}
    public async Task<NodeIdentity> AuthenticateAsync(Guid id,string secret,CancellationToken ct=default)
    {var node=await db.Set<GatewayNode>().AsNoTracking().SingleOrDefaultAsync(n=>n.Id==id&&n.Enabled,ct);if(node is null||!Matches(secret,node.IdentityHash)) throw Denied();return new(node.Id,node.EnvironmentId,node.IdentityHash);}
    public async Task<GatewayNode> RevalidateAsync(NodeIdentity identity,Guid? instance,CancellationToken ct)
    {
        var valid=await db.Set<GatewayNode>().AsNoTracking().SingleOrDefaultAsync(n=>n.Id==identity.NodeId&&n.EnvironmentId==identity.EnvironmentId&&n.Enabled&&n.IdentityHash==identity.Hash,ct)??throw Denied();
        if(instance is Guid id&&valid.InstanceId!=id.ToString()) throw new ApiException(409,"node_instance_changed","节点实例已改变。");return await db.Set<GatewayNode>().SingleAsync(n=>n.Id==identity.NodeId,ct);
    }
    public static ApiException Denied()=>new(401,"node_authentication_required","节点身份无效或已停用。");
}
