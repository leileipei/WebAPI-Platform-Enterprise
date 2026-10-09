using System.Collections.Concurrent;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace WebApi.PerformanceHost;
public sealed record SqlRequestStats(int Total,int Authorization,int Data,int Unclassified,long Rows=0,long BinaryColumnBytes=0);
public sealed record SqlCommandShape(string Hash,string Kind,int Count,string Classification);
public sealed class RequestSqlProbe : DbCommandInterceptor
{
    private readonly AsyncLocal<string?> current=new();
    private readonly ConcurrentDictionary<string,ConcurrentDictionary<(string Hash,string Kind,string Classification),int>> requests=new();
    private readonly ConcurrentDictionary<string,long> rows=new();
    private readonly ConcurrentDictionary<string,long> binaryBytes=new();
    private readonly IReadOnlyDictionary<string,string> classifications;
    private readonly PrivateSqlShapeAudit? audit;
    public RequestSqlProbe(IReadOnlyDictionary<string,string>? classifications=null,PrivateSqlShapeAudit? audit=null)
    {
        this.audit=audit;
        this.classifications=new Dictionary<string,string>(classifications??new Dictionary<string,string>(),StringComparer.Ordinal);
        if(this.classifications.Values.Any(x=>x is not ("authorization" or "data")))throw new ArgumentException("Invalid SQL classification.");
    }
    public IDisposable Begin(string requestId)
    {
        if(string.IsNullOrWhiteSpace(requestId)||requestId.Length>128)throw new ArgumentException("Invalid SQL probe request ID.");
        if(!requests.TryAdd(requestId,new()))throw new InvalidOperationException("SQL probe request ID already exists.");
        rows.TryAdd(requestId,0);
        binaryBytes.TryAdd(requestId,0);
        var prior=current.Value;current.Value=requestId;return new Scope(()=>current.Value=prior);
    }
    public SqlRequestStats Read(string requestId)
    {
        var shapes=ReadShapes(requestId);
        return new(shapes.Sum(x=>x.Count),shapes.Where(x=>x.Classification=="authorization").Sum(x=>x.Count),
            shapes.Where(x=>x.Classification=="data").Sum(x=>x.Count),shapes.Where(x=>x.Classification=="unclassified").Sum(x=>x.Count),rows.GetValueOrDefault(requestId),binaryBytes.GetValueOrDefault(requestId));
    }
    public IReadOnlyList<SqlCommandShape> ReadShapes(string requestId)=>requests.TryGetValue(requestId,out var shapes)
        ?shapes.Select(x=>new SqlCommandShape(x.Key.Hash,x.Key.Kind,x.Value,x.Key.Classification)).OrderBy(x=>x.Hash,StringComparer.Ordinal).ThenBy(x=>x.Kind).ToArray():[];
    private void Record(DbCommand command,string kind)
    {
        var id=current.Value;if(id is null||!requests.TryGetValue(id,out var shapes))return;
        var hash=Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command.CommandText)));
        audit?.Record(hash,command.CommandText);
        var classification=classifications.TryGetValue(hash,out var mapped)?mapped:"unclassified";
        shapes.AddOrUpdate((hash,kind,classification),1,(_,n)=>checked(n+1));
    }
    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result)
    {Record(command,"reader");return result;}
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken=default)
    {Record(command,"reader");return ValueTask.FromResult(result);}
    // Npgsql SaveChanges requires its native reader to consume affected-row metadata.
    // Count all command attempts, but measure consumed rows only for EF query readers.
    private DbDataReader CountRows(DbDataReader reader,CommandSource source)=>
        source is (CommandSource.LinqQuery or CommandSource.FromSqlQuery)&&current.Value is { } id&&rows.ContainsKey(id)
        ?new PerformanceCountingReader(reader,()=>rows.AddOrUpdate(id,1,(_,n)=>checked(n+1)),size=>binaryBytes.AddOrUpdate(id,size,(_,n)=>checked(n+size))):reader;
    public override DbDataReader ReaderExecuted(DbCommand command,CommandExecutedEventData eventData,DbDataReader result)=>CountRows(result,eventData.CommandSource);
    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command,CommandExecutedEventData eventData,DbDataReader result,CancellationToken cancellationToken=default)=>ValueTask.FromResult(CountRows(result,eventData.CommandSource));
    public override InterceptionResult<object> ScalarExecuting(DbCommand command,CommandEventData eventData,InterceptionResult<object> result)
    {Record(command,"scalar");return result;}
    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<object> result,CancellationToken cancellationToken=default)
    {Record(command,"scalar");return ValueTask.FromResult(result);}
    public override InterceptionResult<int> NonQueryExecuting(DbCommand command,CommandEventData eventData,InterceptionResult<int> result)
    {Record(command,"nonquery");return result;}
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData eventData,InterceptionResult<int> result,CancellationToken cancellationToken=default)
    {Record(command,"nonquery");return ValueTask.FromResult(result);}
    private sealed class Scope(Action restore) : IDisposable
    {
        private int disposed;
        public void Dispose(){if(Interlocked.Exchange(ref disposed,1)==0)restore();}
    }
}
