using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
namespace WebApi.PerformanceHost;
public sealed record ExplainPlanNode(string NodeType,string? Relation,string? Index,IReadOnlyDictionary<string,double> Metrics,IReadOnlyList<ExplainPlanNode> Children);
public sealed record ExplainPlanEvidence(double PlanningMs,double ExecutionMs,ExplainPlanNode Plan);
public sealed record ExplainCommandEvidence(string Hash,ExplainPlanEvidence Plan);
public sealed record ExplainRequestEvidence(int Explained,int Repeated,IReadOnlyList<ExplainCommandEvidence> Queries);
public sealed class ExplainQueryProbe:DbCommandInterceptor
{
    private readonly string connection;
    private readonly HashSet<string> relations,indexes;
    private readonly AsyncLocal<string?> current=new();
    private readonly ConcurrentDictionary<string,RequestPlans> requests=new();
    public ExplainQueryProbe(string connection,IReadOnlySet<string> relations,IReadOnlySet<string> indexes){
        this.connection=connection;this.relations=new(relations,StringComparer.Ordinal);this.indexes=new(indexes,StringComparer.Ordinal);
    }
    public IDisposable Begin(string id){
        if(string.IsNullOrWhiteSpace(id)||id.Length>128)throw new ArgumentException("Invalid EXPLAIN request ID.");
        if(!requests.TryAdd(id,new()))throw new InvalidOperationException("EXPLAIN request ID already exists.");
        var prior=current.Value;current.Value=id;return new Scope(()=>current.Value=prior);
    }
    public ExplainRequestEvidence Read(string id)=>requests.TryGetValue(id,out var request)
        ?new(request.Plans.Count,Volatile.Read(ref request.Repeated),request.Plans.Values.OrderBy(p=>p.Hash,StringComparer.Ordinal).ToArray()):new(0,0,[]);
    private (RequestPlans Request,string Hash)? Claim(DbCommand command,CommandSource source){
        if(current.Value is not { } id||!requests.TryGetValue(id,out var request)||source is not (CommandSource.LinqQuery or CommandSource.FromSqlQuery))return null;
        // Only real EF SELECT readers are diagnosed. Raw commands and write-result
        // readers retain native provider behavior. The separate transaction also
        // rejects volatile functions that try to modify permanent tables.
        if(command is not NpgsqlCommand||command.CommandType!=CommandType.Text||!command.CommandText.TrimStart().StartsWith("SELECT ",StringComparison.OrdinalIgnoreCase)||command.CommandText.Contains(';'))throw new InvalidDataException("Unsupported EXPLAIN query.");
        var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command.CommandText)));
        if(!request.Seen.TryAdd(hash,0)){Interlocked.Increment(ref request.Repeated);return null;}
        return(request,hash);
    }
    private static NpgsqlCommand DiagnosticCommand(DbCommand original,NpgsqlConnection diagnostic,NpgsqlTransaction transaction){
        var command=new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) "+original.CommandText,diagnostic,transaction){CommandTimeout=30};
        foreach(NpgsqlParameter parameter in original.Parameters)command.Parameters.Add(parameter.Clone());
        return command;
    }
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result){
        if(Claim(command,eventData.CommandSource) is not { } claim)return result;
        try{
            using var diagnostic=new NpgsqlConnection(connection);diagnostic.Open();using var transaction=diagnostic.BeginTransaction(IsolationLevel.ReadCommitted);
            using(var setup=new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '30s'",diagnostic,transaction))setup.ExecuteNonQuery();
            using var query=DiagnosticCommand(command,diagnostic,transaction);
            var plan=Parse(query.ExecuteScalar() as string??throw new InvalidDataException(),relations,indexes);
            claim.Request.Plans.TryAdd(claim.Hash,new(claim.Hash,plan));transaction.Rollback();return result;
        }catch(Exception error)when(error is not OperationCanceledException){throw new InvalidDataException("EXPLAIN diagnostic failed.");}
    }
    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default){
        if(Claim(command,eventData.CommandSource) is not { } claim)return result;
        try{
            await using var diagnostic=new NpgsqlConnection(connection);await diagnostic.OpenAsync(cancellationToken);await using var transaction=await diagnostic.BeginTransactionAsync(IsolationLevel.ReadCommitted,cancellationToken);
            await using(var setup=new NpgsqlCommand("SET TRANSACTION READ ONLY; SET LOCAL statement_timeout = '30s'",diagnostic,transaction))await setup.ExecuteNonQueryAsync(cancellationToken);
            await using var query=DiagnosticCommand(command,diagnostic,transaction);
            var plan=Parse(await query.ExecuteScalarAsync(cancellationToken) as string??throw new InvalidDataException(),relations,indexes);
            claim.Request.Plans.TryAdd(claim.Hash,new(claim.Hash,plan));await transaction.RollbackAsync(cancellationToken);return result;
        }catch(Exception error)when(error is not OperationCanceledException){throw new InvalidDataException("EXPLAIN diagnostic failed.");}
    }
    private static readonly HashSet<string> NodeTypes=new(StringComparer.Ordinal){"Result","ProjectSet","ModifyTable","Append","Merge Append","Recursive Union","BitmapAnd","BitmapOr","Seq Scan","Sample Scan","Index Scan","Index Only Scan","Bitmap Index Scan","Bitmap Heap Scan","Tid Scan","Tid Range Scan","Subquery Scan","Function Scan","Table Function Scan","Values Scan","CTE Scan","Named Tuplestore Scan","WorkTable Scan","Foreign Scan","Custom Scan","Nested Loop","Merge Join","Hash Join","Materialize","Memoize","Sort","Incremental Sort","Group","Aggregate","WindowAgg","Unique","Gather","Gather Merge","Hash","SetOp","LockRows","Limit"};
    private static readonly string[] MetricNames=["Startup Cost","Total Cost","Plan Rows","Plan Width","Actual Startup Time","Actual Total Time","Actual Rows","Actual Loops","Rows Removed by Filter","Rows Removed by Index Recheck","Rows Removed by Join Filter","Shared Hit Blocks","Shared Read Blocks","Shared Dirtied Blocks","Shared Written Blocks","Local Hit Blocks","Local Read Blocks","Local Dirtied Blocks","Local Written Blocks","Temp Read Blocks","Temp Written Blocks","I/O Read Time","I/O Write Time","Temp I/O Read Time","Temp I/O Write Time","Sort Space Used","Peak Memory Usage","Hash Buckets","Original Hash Buckets","Hash Batches","Original Hash Batches","Heap Fetches","Workers Planned","Workers Launched"];
    public static ExplainPlanEvidence Parse(string json,IReadOnlySet<string> relations,IReadOnlySet<string> indexes){
        try{
            if(json.Length>8*1024*1024)throw new InvalidDataException();
            using var document=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=64});var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Array||root.GetArrayLength()!=1)throw new InvalidDataException();
            var entry=root[0];var count=0;
            double Number(JsonElement element,string name){if(!element.TryGetProperty(name,out var value)||!value.TryGetDouble(out var number)||!double.IsFinite(number)||number<0)throw new InvalidDataException();return number;}
            string? Identifier(JsonElement node,string name,IReadOnlySet<string> allowed)=>node.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String&&value.GetString() is { } text&&allowed.Contains(text)?text:null;
            ExplainPlanNode Node(JsonElement node){
                if(++count>512||node.ValueKind!=JsonValueKind.Object||!node.TryGetProperty("Node Type",out var type)||type.ValueKind!=JsonValueKind.String||type.GetString() is not { } nodeType||!NodeTypes.Contains(nodeType))throw new InvalidDataException();
                var metrics=new SortedDictionary<string,double>(StringComparer.Ordinal);foreach(var name in MetricNames)if(node.TryGetProperty(name,out _))metrics.Add(name,Number(node,name));
                IReadOnlyList<ExplainPlanNode> children=[];if(node.TryGetProperty("Plans",out var plans)){if(plans.ValueKind!=JsonValueKind.Array)throw new InvalidDataException();children=plans.EnumerateArray().Select(Node).ToArray();}
                return new(nodeType,Identifier(node,"Relation Name",relations),Identifier(node,"Index Name",indexes),metrics,children);
            }
            return new(Number(entry,"Planning Time"),Number(entry,"Execution Time"),Node(entry.GetProperty("Plan")));
        }catch(Exception error)when(error is JsonException or InvalidOperationException or KeyNotFoundException or InvalidDataException){throw new InvalidDataException("Invalid EXPLAIN evidence.");}
    }
    private sealed class RequestPlans{
        public ConcurrentDictionary<string,byte> Seen{get;}=new();
        public ConcurrentDictionary<string,ExplainCommandEvidence> Plans{get;}=new();
        public int Repeated;
    }
    private sealed class Scope(Action restore):IDisposable{private int disposed;public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)restore();}}
}
