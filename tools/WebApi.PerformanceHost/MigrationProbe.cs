using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using WebApi.Contracts.Common;
using WebApi.Infrastructure.Notifications;
using WebApi.Infrastructure.Persistence;
namespace WebApi.PerformanceHost;
public sealed record DatabaseTableEvidence(string Name,IReadOnlyList<string> Columns,long Rows,long Bytes,string DataHash);
public sealed record DatabaseIndexEvidence(string Table,string Name,IReadOnlyList<string?> Columns,IReadOnlyList<bool> Descending,bool Unique,bool Valid,bool Ready,long Bytes,string DefinitionHash);
public sealed record DatabaseInspection(IReadOnlyList<string> AppliedMigrations,IReadOnlyList<string> PendingMigrations,bool PendingModelChanges,bool ModelMatched,IReadOnlyList<DatabaseTableEvidence> Tables,IReadOnlyList<DatabaseIndexEvidence> Indexes,string BusinessFingerprint);
public sealed record MigrationApplyEvidence(DatabaseInspection Before,DatabaseInspection After,bool IndexOnly,bool Complete);
public sealed record MigrationFailureEvidence(bool NoMigrationNeeded,bool FailureInjected,string? SqlState,bool DataUnchanged,bool IndexesUnchanged,bool Complete);
public static class MigrationProbe
{
    private const string RouteIndexName="ix_api_routes_environment_order_path_id";
    private const string RouteIndexAnnotation="WebApi:ManagedRouteOrderIndexDefinition";
    private const string RouteIndexDefinitionHash="a552f9d4edb81648f93a204b59d85e4e1db041439f35a7d4d9885949e18b89cd";
    // One reviewed, transactional expression index. No raw DDL parser or prefix allow-list.
    private const string RouteIndexSql="CREATE INDEX ix_api_routes_environment_order_path_id ON api_routes (environment_id, ((CASE WHEN normalized_path LIKE '%{*}%' THEN 2 WHEN normalized_path LIKE '%{}%' THEN 1 ELSE 0 END) * 2000000 - priority), path, id);";
    private static string Quote(string identifier){if(!Regex.IsMatch(identifier,"^[A-Za-z_][A-Za-z0-9_]{0,126}~?$",RegexOptions.CultureInvariant))throw new InvalidDataException("Invalid diagnostic identifier.");return "\""+identifier+"\"";}
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static IReadOnlyList<TableModel> Models(WebApiDbContext db)=>db.GetService<IDesignTimeModel>().Model.GetEntityTypes().Where(e=>e.GetTableName()!=null).GroupBy(e=>e.GetTableName()!,StringComparer.Ordinal).OrderBy(g=>g.Key,StringComparer.Ordinal).Select(group=>{
        var entities=group.ToArray();var table=StoreObjectIdentifier.Table(group.Key,entities[0].GetSchema());
        var columns=entities.SelectMany(e=>e.GetProperties().Select(p=>p.GetColumnName(table))).OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var key=entities[0].FindPrimaryKey()??throw new InvalidDataException("Diagnostic table primary key required.");
        var primary=key.Properties.Select(p=>p.GetColumnName(table)??throw new InvalidDataException()).ToArray();
        var indexes=entities.SelectMany(e=>e.GetIndexes()).Select(i=>new ExpectedIndex(i.GetDatabaseName(table)??throw new InvalidDataException(),i.Properties.Select(p=>p.GetColumnName(table)??throw new InvalidDataException()).ToArray(),i.IsDescending is null?new bool[i.Properties.Count]:i.IsDescending.Count==0?Enumerable.Repeat(true,i.Properties.Count).ToArray():i.IsDescending.ToArray(),i.IsUnique)).ToArray();
        var managed=entities.Select(e=>e.FindAnnotation(RouteIndexAnnotation)?.Value).Where(v=>v is not null).ToArray();
        if(managed.Any(v=>group.Key!="api_routes"||v is not string value||value!=RouteIndexDefinitionHash))throw new InvalidDataException("Unapproved managed expression index metadata.");
        return new TableModel(group.Key,columns,primary,indexes,managed.Length>0);
    }).ToArray();
    public static async Task<DatabaseInspection> InspectAsync(WebApiDbContext db,CancellationToken ct=default){
        if(db.Database.CurrentTransaction is not null)throw new InvalidDataException("Diagnostic inspection requires its own snapshot.");
        var models=Models(db);await db.Database.OpenConnectionAsync(ct);var connection=(NpgsqlConnection)db.Database.GetDbConnection();
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);var native=(NpgsqlTransaction)transaction.GetDbTransaction();
        await using(var readOnly=new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,native))await readOnly.ExecuteNonQueryAsync(ct);
        bool historyExists;await using(var history=new NpgsqlCommand("SELECT to_regclass('public.\"__EFMigrationsHistory\"') IS NOT NULL",connection,native))historyExists=(bool)(await history.ExecuteScalarAsync(ct))!;
        // Npgsql's absent-history handling catches 42P01, but that still aborts
        // an enclosing PostgreSQL transaction. Check presence before querying it.
        var applied=historyExists?(await db.Database.GetAppliedMigrationsAsync(ct)).ToArray():[];var pending=db.Database.GetMigrations().Except(applied,StringComparer.Ordinal).ToArray();
        var available=new Dictionary<string,(string[] Columns,long Bytes)>(StringComparer.Ordinal);
        await using(var catalog=new NpgsqlCommand("SELECT c.relname, ARRAY(SELECT a.attname::text FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attname), pg_total_relation_size(c.oid) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p') ORDER BY c.relname",connection,native))
        await using(var reader=await catalog.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct)){var name=reader.GetString(0);if(models.Any(m=>m.Name==name))available.Add(name,(reader.GetFieldValue<string[]>(1).Order(StringComparer.Ordinal).ToArray(),reader.GetInt64(2)));}
        var indexes=new List<DatabaseIndexEvidence>();
        const string indexQuery="SELECT t.relname, i.relname, x.indisunique, x.indisvalid, x.indisready, pg_relation_size(i.oid), pg_get_indexdef(i.oid), ARRAY(SELECT COALESCE(a.attname::text,'') FROM unnest(x.indkey) WITH ORDINALITY k(attnum,ord) LEFT JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum WHERE k.ord<=x.indnkeyatts ORDER BY k.ord), ARRAY(SELECT COALESCE(pg_index_column_has_property(i.oid,v,'desc'),false) FROM generate_series(1,x.indnkeyatts) v) FROM pg_index x JOIN pg_class i ON i.oid=x.indexrelid JOIN pg_class t ON t.oid=x.indrelid JOIN pg_namespace n ON n.oid=t.relnamespace WHERE n.nspname='public' ORDER BY t.relname,i.relname";
        await using(var catalog=new NpgsqlCommand(indexQuery,connection,native))await using(var reader=await catalog.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct)){
            var table=reader.GetString(0);if(!available.ContainsKey(table))continue;
            var name=reader.GetString(1);Quote(name);
            var columns=reader.GetFieldValue<string[]>(7).Select(c=>c.Length==0?null:c).ToArray();foreach(var column in columns.OfType<string>())Quote(column);
            indexes.Add(new(table,name,columns,reader.GetFieldValue<bool[]>(8),reader.GetBoolean(2),reader.GetBoolean(3),reader.GetBoolean(4),reader.GetInt64(5),Hash(Encoding.UTF8.GetBytes(reader.GetString(6)))));
        }
        var tables=new List<DatabaseTableEvidence>();var buffer=new byte[64*1024];
        foreach(var model in models.Where(m=>available.ContainsKey(m.Name))){
            long rows;await using(var count=new NpgsqlCommand("SELECT COUNT(*) FROM public."+Quote(model.Name),connection,native))rows=Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            // Raw binary COPY stays in the process and is hashed in bounded chunks.
            // No row value, credential, payload or SQL definition reaches evidence.
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var order=string.Join(',',model.Primary.Select(Quote));
            await using(var stream=await connection.BeginRawBinaryCopyAsync("COPY (SELECT * FROM public."+Quote(model.Name)+" ORDER BY "+order+") TO STDOUT (FORMAT BINARY)",ct)){
                int read;while((read=await stream.ReadAsync(buffer.AsMemory(),ct))!=0)hash.AppendData(buffer,0,read);
            }
            tables.Add(new(model.Name,available[model.Name].Columns,rows,available[model.Name].Bytes,Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        var fingerprint=Hash(CanonicalJson.Serialize(tables.Select(t=>new{t.Name,t.Columns,t.Rows,t.DataHash}).ToArray()));
        var matched=models.All(m=>available.TryGetValue(m.Name,out var actual)&&m.Columns.SequenceEqual(actual.Columns,StringComparer.Ordinal)&&m.Indexes.All(expected=>indexes.Any(i=>i.Table==m.Name&&i.Name==expected.Name&&i.Unique==expected.Unique&&i.Valid&&i.Ready&&i.Columns.SequenceEqual(expected.Columns)&&i.Descending.SequenceEqual(expected.Descending)))
            &&(!m.ManagedRouteIndex||indexes.Any(i=>i.Table=="api_routes"&&i.Name==RouteIndexName&&!i.Unique&&i.Valid&&i.Ready&&i.DefinitionHash==RouteIndexDefinitionHash&&i.Columns.SequenceEqual(new string?[]{"environment_id",null,"path","id"})&&i.Descending.SequenceEqual(new[]{false,false,false,false}))));
        await transaction.RollbackAsync(ct);
        return new(applied,pending,db.Database.HasPendingModelChanges(),matched,tables,indexes,fingerprint);
    }
    private static async Task<IReadOnlyList<MigrationOperation>> PendingIndexesAsync(WebApiDbContext db,CancellationToken ct){
        var assembly=db.GetService<IMigrationsAssembly>();var operations=new List<MigrationOperation>();
        foreach(var id in await db.Database.GetPendingMigrationsAsync(ct)){
            var migration=assembly.CreateMigration(assembly.Migrations[id],db.Database.ProviderName!);
            operations.AddRange(migration.UpOperations);
        }
        ValidateOperations(db,operations);return operations;
    }
    private static void ValidateOperations(WebApiDbContext db,IReadOnlyList<MigrationOperation> operations){
        var models=Models(db);
        foreach(var operation in operations){
            if(operation is CreateIndexOperation)continue;
            if(operation is not SqlOperation sql||sql.SuppressTransaction||sql.Sql!=RouteIndexSql
                ||!models.Any(m=>m.Name=="api_routes"&&new[]{"environment_id","normalized_path","priority","path","id"}.All(c=>m.Columns.Contains(c,StringComparer.Ordinal))))
                throw new InvalidDataException("Incremental diagnostic accepts only approved transactional index DDL.");
        }
        ValidateIndexes(db,operations.OfType<CreateIndexOperation>().ToArray());
        // Validate the whole batch, including later operations, before any mutation.
        var commands=db.GetService<IMigrationsSqlGenerator>().Generate(operations,db.GetService<IDesignTimeModel>().Model);
        if(commands.Any(c=>c.TransactionSuppressed))throw new InvalidDataException("Atomic diagnostic rejects nontransactional index DDL.");
    }
    private static void ValidateIndexes(WebApiDbContext db,IReadOnlyList<CreateIndexOperation> operations){
        var models=Models(db);foreach(var operation in operations){
            Quote(operation.Name);Quote(operation.Table);var model=models.SingleOrDefault(m=>m.Name==operation.Table)??throw new InvalidDataException("Index table outside model.");
            if(operation.Schema is not (null or "public")||operation.Columns.Length==0||operation.Columns.Any(c=>!model.Columns.Contains(c,StringComparer.Ordinal)))throw new InvalidDataException("Index columns outside model.");
            foreach(var column in operation.Columns)Quote(column);
        }
    }
    public static async Task<MigrationApplyEvidence> ApplyAsync(WebApiDbContext db,bool indexOnly,CancellationToken ct=default){
        if(indexOnly)await PendingIndexesAsync(db,ct);var before=await InspectAsync(db,ct);
        await NotificationMigrationPreflight.ValidateAsync(db,ct);await db.Database.MigrateAsync(ct);await db.Database.ExecuteSqlRawAsync("ANALYZE",ct);
        var after=await InspectAsync(db,ct);var complete=after.ModelMatched&&!after.PendingModelChanges&&after.PendingMigrations.Count==0&&(!indexOnly||before.BusinessFingerprint==after.BusinessFingerprint);
        return new(before,after,indexOnly,complete);
    }
    public static async Task<MigrationFailureEvidence> FailAfterFirstIndexAsync(WebApiDbContext db,IReadOnlyList<MigrationOperation> operations,CancellationToken ct=default){
        ValidateOperations(db,operations);var before=await InspectAsync(db,ct);
        if(operations.Count==0)return new(true,false,null,true,true,true);
        var commands=db.GetService<IMigrationsSqlGenerator>().Generate([operations[0]],db.GetService<IDesignTimeModel>().Model);
        if(commands.Any(c=>c.TransactionSuppressed))throw new InvalidDataException("Atomic failure diagnostic rejects nontransactional index DDL.");
        string? state=null;await using(var transaction=await db.Database.BeginTransactionAsync(ct)){
            // The SQL generator already rendered the DDL. Execute it verbatim;
            // ExecuteSqlRaw would interpret route tokens such as {*} as format items.
            foreach(var command in commands){
                await using var ddl=new NpgsqlCommand(command.CommandText,(NpgsqlConnection)db.Database.GetDbConnection(),(NpgsqlTransaction)transaction.GetDbTransaction());
                if(db.Database.GetCommandTimeout() is int timeout)ddl.CommandTimeout=timeout;
                await ddl.ExecuteNonQueryAsync(ct);
            }
            try{await db.Database.ExecuteSqlRawAsync("CREATE INDEX",ct);throw new InvalidDataException("Injected DDL did not fail.");}
            catch(PostgresException error)when(error.SqlState=="42601"){state=error.SqlState;}
            await transaction.RollbackAsync(ct);
        }
        var after=await InspectAsync(db,ct);var data=before.BusinessFingerprint==after.BusinessFingerprint;
        var indexes=Hash(CanonicalJson.Serialize(before.Indexes.Select(i=>i with {Bytes=0}).ToArray()))==Hash(CanonicalJson.Serialize(after.Indexes.Select(i=>i with {Bytes=0}).ToArray()));
        return new(false,true,state,data,indexes,data&&indexes&&state=="42601");
    }
    public static async Task<MigrationFailureEvidence> FailPendingIndexesAsync(WebApiDbContext db,CancellationToken ct=default)=>await FailAfterFirstIndexAsync(db,await PendingIndexesAsync(db,ct),ct);
    private sealed record ExpectedIndex(string Name,string[] Columns,bool[] Descending,bool Unique);
    private sealed record TableModel(string Name,string[] Columns,string[] Primary,ExpectedIndex[] Indexes,bool ManagedRouteIndex);
}
