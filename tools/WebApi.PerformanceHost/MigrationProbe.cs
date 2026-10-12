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
public sealed record LegacyTableEvidence(string Name,IReadOnlyList<string> Columns,IReadOnlyList<string> ColumnTypes,IReadOnlyList<bool> NotNull,IReadOnlyList<string> Primary,long Rows,string DataHash);
public sealed record LegacyContract(int SchemaVersion,IReadOnlyList<LegacyTableEvidence> Tables,IReadOnlyList<string> AppliedMigrations,IReadOnlyList<DatabaseIndexEvidence> Indexes,string SchemaHash,string Fingerprint);
public sealed record CompatibleMigrationEvidence(DatabaseInspection Before,DatabaseInspection After,LegacyContract LegacyBefore,LegacyContract LegacyAfter,bool Complete);
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
    private static string LegacySchemaHash(IReadOnlyList<LegacyTableEvidence> tables)=>Hash(CanonicalJson.Serialize(tables.Select(t=>new{t.Name,t.Columns,t.ColumnTypes,t.NotNull,t.Primary}).ToArray()));
    private static void ValidateLegacy(LegacyContract contract)
    {
        bool Sha(string value)=>value is not null&&Regex.IsMatch(value,"^[a-f0-9]{64}$",RegexOptions.CultureInvariant);
        if(contract.SchemaVersion!=1||contract.Tables is null||contract.Tables.Count is 0 or >256||contract.AppliedMigrations is null||contract.Indexes is null||!Sha(contract.SchemaHash)||!Sha(contract.Fingerprint))throw new InvalidDataException("Invalid baseline contract.");
        var names=new HashSet<string>(StringComparer.Ordinal);
        foreach(var table in contract.Tables){
            Quote(table.Name);if(!names.Add(table.Name)||table.Columns is null||table.Columns.Count is 0 or >256||table.ColumnTypes is null||table.NotNull is null||table.ColumnTypes.Count!=table.Columns.Count||table.NotNull.Count!=table.Columns.Count||table.Primary is null||table.Primary.Count==0||table.Rows<0||!Sha(table.DataHash))throw new InvalidDataException("Invalid baseline table.");
            foreach(var column in table.Columns)Quote(column);
            if(table.Columns.Distinct(StringComparer.Ordinal).Count()!=table.Columns.Count||table.Primary.Distinct(StringComparer.Ordinal).Count()!=table.Primary.Count||table.Primary.Any(p=>!table.Columns.Contains(p,StringComparer.Ordinal))||table.ColumnTypes.Any(t=>string.IsNullOrWhiteSpace(t)||t.Length>256))throw new InvalidDataException("Invalid baseline columns.");
        }
        if(!contract.Tables.Select(t=>t.Name).SequenceEqual(contract.Tables.Select(t=>t.Name).Order(StringComparer.Ordinal))||contract.AppliedMigrations.Count==0||contract.AppliedMigrations.Any(string.IsNullOrWhiteSpace)||contract.AppliedMigrations.Distinct(StringComparer.Ordinal).Count()!=contract.AppliedMigrations.Count||contract.Indexes.Count>4096)throw new InvalidDataException("Invalid baseline coverage.");
        var indexes=new HashSet<string>(StringComparer.Ordinal);
        foreach(var index in contract.Indexes){Quote(index.Name);if(!names.Contains(index.Table)||!indexes.Add(index.Table+":"+index.Name)||!index.Valid||!index.Ready||!Sha(index.DefinitionHash))throw new InvalidDataException("Invalid baseline indexes.");}
        if(LegacySchemaHash(contract.Tables)!=contract.SchemaHash||Hash(CanonicalJson.Serialize(contract.Tables))!=contract.Fingerprint)throw new InvalidDataException("Baseline contract checksum mismatch.");
    }
    private static bool SameIndexes(IReadOnlyList<DatabaseIndexEvidence> expected,IReadOnlyList<DatabaseIndexEvidence> actual,bool additions)=>
        (additions||expected.Count==actual.Count)&&expected.All(e=>actual.Any(a=>a.Table==e.Table&&a.Name==e.Name&&a.DefinitionHash==e.DefinitionHash&&a.Valid&&a.Ready&&a.Unique==e.Unique&&a.Columns.SequenceEqual(e.Columns)&&a.Descending.SequenceEqual(e.Descending)));
    private static string CanonicalStoreType(string type)
    {
        var value=Regex.Replace(type.Trim().ToLowerInvariant(),@"\s+"," ",RegexOptions.CultureInvariant);
        value=Regex.Replace(value,@"^varchar(?=\(|\[|$)","character varying",RegexOptions.CultureInvariant);
        return value switch{"timestamptz"=>"timestamp with time zone","timestamp"=>"timestamp without time zone","int" or "int4"=>"integer","int8"=>"bigint","bool"=>"boolean",_=>value};
    }
    private static async Task<LegacyContract> ReadLegacyAsync(WebApiDbContext db,LegacyContract? baseline,bool additions,CancellationToken ct)
    {
        if(db.Database.CurrentTransaction is not null)throw new InvalidDataException("Legacy inspection requires its own snapshot.");
        if(baseline is not null)ValidateLegacy(baseline);
        var models=Models(db);await db.Database.OpenConnectionAsync(ct);var connection=(NpgsqlConnection)db.Database.GetDbConnection();
        await using var transaction=await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead,ct);var native=(NpgsqlTransaction)transaction.GetDbTransaction();
        await using(var readOnly=new NpgsqlCommand("SET TRANSACTION READ ONLY",connection,native))await readOnly.ExecuteNonQueryAsync(ct);
        var catalog=new Dictionary<string,(string[] Columns,string[] Types,bool[] NotNull,string[] Primary)>(StringComparer.Ordinal);
        const string sql="SELECT c.relname, ARRAY(SELECT a.attname::text FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attname), ARRAY(SELECT format_type(a.atttypid,a.atttypmod) FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attname), ARRAY(SELECT a.attnotnull FROM pg_attribute a WHERE a.attrelid=c.oid AND a.attnum>0 AND NOT a.attisdropped ORDER BY a.attname), ARRAY(SELECT a.attname::text FROM pg_index i CROSS JOIN LATERAL unnest(i.indkey) WITH ORDINALITY k(attnum,ord) JOIN pg_attribute a ON a.attrelid=i.indrelid AND a.attnum=k.attnum WHERE i.indrelid=c.oid AND i.indisprimary ORDER BY k.ord) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace WHERE n.nspname='public' AND c.relkind IN ('r','p') AND c.relname <> '__EFMigrationsHistory' ORDER BY c.relname";
        await using(var command=new NpgsqlCommand(sql,connection,native))await using(var reader=await command.ExecuteReaderAsync(ct))while(await reader.ReadAsync(ct)){
            var name=reader.GetString(0);var columns=reader.GetFieldValue<string[]>(1);var primary=reader.GetFieldValue<string[]>(4);var model=models.SingleOrDefault(m=>m.Name==name);
            if(model is null||columns.Any(c=>!model.Columns.Contains(c,StringComparer.Ordinal))||!primary.SequenceEqual(model.Primary,StringComparer.Ordinal))throw new InvalidDataException("Database schema outside diagnostic model.");
            catalog.Add(name,(columns,reader.GetFieldValue<string[]>(2),reader.GetFieldValue<bool[]>(3),primary));
        }
        if(catalog.Count==0||baseline is not null&&(!additions&&catalog.Count!=baseline.Tables.Count||baseline.Tables.Any(t=>!catalog.ContainsKey(t.Name))))throw new InvalidDataException("Baseline table coverage mismatch.");
        if(additions){
            var target=db.GetService<IDesignTimeModel>().Model.GetRelationalModel().Tables.Where(t=>t.Schema is null or "public").ToDictionary(t=>t.Name,StringComparer.Ordinal);
            if(catalog.Count!=target.Count)throw new InvalidDataException("Target table coverage mismatch.");
            foreach(var pair in catalog){
                if(!target.TryGetValue(pair.Key,out var table)||table.Columns.Count()!=pair.Value.Columns.Length)throw new InvalidDataException("Target column coverage mismatch.");
                for(var index=0;index<pair.Value.Columns.Length;index++){
                    var name=pair.Value.Columns[index];var column=table.FindColumn(name);
                    if(column is null||CanonicalStoreType(column.StoreType)!=CanonicalStoreType(pair.Value.Types[index])||column.IsNullable==pair.Value.NotNull[index])throw new InvalidDataException($"Target schema mismatch: {pair.Key}.{name}, actual type {pair.Value.Types[index]}, not-null {pair.Value.NotNull[index]}, expected type {column?.StoreType}, not-null {!column?.IsNullable}.");
                }
            }
        }
        var result=new List<LegacyTableEvidence>();var buffer=new byte[64*1024];
        foreach(var name in (baseline?.Tables.Select(t=>t.Name)??catalog.Keys).Order(StringComparer.Ordinal)){
            var actual=catalog[name];var expected=baseline?.Tables.Single(t=>t.Name==name);var columns=expected?.Columns.ToArray()??actual.Columns;
            var positions=columns.Select(c=>Array.IndexOf(actual.Columns,c)).ToArray();
            if(positions.Any(p=>p<0)||expected is not null&&(!additions&&!actual.Columns.SequenceEqual(columns,StringComparer.Ordinal)||!actual.Primary.SequenceEqual(expected.Primary,StringComparer.Ordinal)||positions.Where(p=>p>=0).Select(p=>actual.Types[p]).SequenceEqual(expected.ColumnTypes,StringComparer.Ordinal)==false||positions.Where(p=>p>=0).Select(p=>actual.NotNull[p]).SequenceEqual(expected.NotNull)==false))throw new InvalidDataException("Baseline column or primary-key mismatch.");
            long rows;await using(var count=new NpgsqlCommand("SELECT COUNT(*) FROM public."+Quote(name),connection,native))rows=Convert.ToInt64(await count.ExecuteScalarAsync(ct));
            using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var selected=string.Join(',',columns.Select(Quote));var order=string.Join(',',actual.Primary.Select(Quote));
            await using(var stream=await connection.BeginRawBinaryCopyAsync("COPY (SELECT "+selected+" FROM public."+Quote(name)+" ORDER BY "+order+") TO STDOUT (FORMAT BINARY)",ct)){
                int read;while((read=await stream.ReadAsync(buffer.AsMemory(),ct))!=0)hash.AppendData(buffer,0,read);
            }
            result.Add(new(name,columns,positions.Select(p=>actual.Types[p]).ToArray(),positions.Select(p=>actual.NotNull[p]).ToArray(),actual.Primary,rows,Convert.ToHexStringLower(hash.GetHashAndReset())));
        }
        var applied=(await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();await transaction.RollbackAsync(ct);
        var inspection=await InspectAsync(db,ct);
        return new(1,result,applied,inspection.Indexes,LegacySchemaHash(result),Hash(CanonicalJson.Serialize(result)));
    }
    public static async Task<LegacyContract> CaptureLegacyAsync(WebApiDbContext db,CancellationToken ct=default)=>await ReadLegacyAsync(db,null,false,ct);
    public static async Task<CompatibleMigrationEvidence> ApplyCompatibleAsync(WebApiDbContext db,LegacyContract baseline,CancellationToken ct=default)
    {
        ValidateLegacy(baseline);var before=await InspectAsync(db,ct);
        var target=(db.Database.GetMigrations()).ToArray();
        bool SetEquals(IEnumerable<string> a,IEnumerable<string> b)=>a.Order(StringComparer.Ordinal).SequenceEqual(b.Order(StringComparer.Ordinal),StringComparer.Ordinal);
        if(baseline.AppliedMigrations.Any(m=>!target.Contains(m,StringComparer.Ordinal)))throw new InvalidDataException("Baseline history outside target source.");
        var repeated=SetEquals(before.AppliedMigrations,target)&&before.ModelMatched&&!before.PendingModelChanges;
        if(!repeated&&!SetEquals(before.AppliedMigrations,baseline.AppliedMigrations))throw new InvalidDataException("Partial or different baseline history.");
        var legacyBefore=await ReadLegacyAsync(db,baseline,repeated,ct);
        if(legacyBefore.Fingerprint!=baseline.Fingerprint||!SameIndexes(baseline.Indexes,before.Indexes,repeated))throw new InvalidDataException("Baseline data or indexes changed.");
        await db.Database.MigrateAsync(ct);db.ChangeTracker.Clear();var after=await InspectAsync(db,ct);var legacyAfter=await ReadLegacyAsync(db,baseline,true,ct);
        var complete=after.ModelMatched&&!after.PendingModelChanges&&after.PendingMigrations.Count==0&&SetEquals(after.AppliedMigrations,target)&&legacyAfter.Fingerprint==baseline.Fingerprint&&SameIndexes(baseline.Indexes,after.Indexes,true);
        if(!complete)throw new InvalidDataException("Compatibility upgrade changed baseline data, schema or indexes.");
        return new(before,after,legacyBefore,legacyAfter,true);
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
