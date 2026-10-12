using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApi.Contracts.Common;
using WebApi.Domain.Runtime;
using WebApi.Infrastructure.Persistence;
using WebApi.PerformanceHost;

try {
    if(args.Length<1)throw new ArgumentException("Command required.");
    var command=args[0];var options=new Dictionary<string,string>(StringComparer.Ordinal);
    for(var i=1;i<args.Length;i+=2) {
        if(i+1>=args.Length||!args[i].StartsWith("--",StringComparison.Ordinal)||!options.TryAdd(args[i][2..],args[i+1]))throw new ArgumentException("Invalid command arguments.");
    }
    string Required(string name)=>options.TryGetValue(name,out var value)?value:throw new ArgumentException("Required command option missing.");
    async Task<T> PrivateJson<T>(string path) {
        var info=new FileInfo(path);if(!info.Exists||info.LinkTarget is not null||info.Length>16*1024*1024)throw new InvalidDataException("Invalid private input.");
        if(!OperatingSystem.IsWindows()&&(File.GetUnixFileMode(path)&~(UnixFileMode.UserRead|UnixFileMode.UserWrite))!=0)throw new InvalidDataException("Input is not private.");
        return JsonSerializer.Deserialize<T>(await File.ReadAllBytesAsync(path),CanonicalJson.Options)??throw new InvalidDataException("Invalid private JSON input.");
    }
    async Task WriteEvidence(object result) {
        var output=Required("output");Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllBytesAsync(output,CanonicalJson.Serialize(result));
    }
    if(command=="serve") {
        var config=await PrivateJson<Dictionary<string,string?>>(Required("config"));await using var host=PerformanceHosts.Build(Required("mode"),config);await host.RunAsync();
    }else if(command=="prepare") {
        var spec=await PrivateJson<DatasetSpec>(Required("spec"));var result=await PerformanceFixtureFiles.PrepareAsync(spec,Required("directory"));await WriteEvidence(result);
    }else if(command is "seed" or "scope-seed" or "reference-seed") {
        var spec=await PrivateJson<DatasetSpec>(Required("spec"));var config=await PrivateJson<Dictionary<string,string?>>(Required("config"));
        var connection=config.GetValueOrDefault("ConnectionStrings:WebApi")??DatabaseSettings.ConnectionString();
        await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connection).Options);await db.Database.MigrateAsync();
        object result=command switch {
            "seed"=>await PerformanceDataset.CreateAsync(db,spec,Required("directory"),CancellationToken.None),
            "scope-seed"=>await PerformanceDataset.CreateScopeSceneAsync(db,spec,Required("directory"),CancellationToken.None),
            _=>await PerformanceDataset.CreateReferenceSceneAsync(db,spec,Required("directory"),CancellationToken.None)
        };await WriteEvidence(result);
    }else if(command is "migration-inspect" or "migration-install" or "migration-upgrade" or "migration-failure" or "migration-legacy" or "migration-current-contract" or "migration-compatible"){
        var config=await PrivateJson<Dictionary<string,string?>>(Required("config"));
        var connection=config.GetValueOrDefault("ConnectionStrings:WebApi")??throw new InvalidDataException("Isolated database configuration required.");
        var address=new Npgsql.NpgsqlConnectionStringBuilder(connection);
        if(address.Host!="postgres"||address.Database!="webapi_performance"||address.Username!="webapi")throw new InvalidDataException("Migration CLI requires the owner-isolated database.");
        await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(connection).Options);
        object result=command switch{
            "migration-inspect"=>await MigrationProbe.InspectAsync(db),
            "migration-install"=>await MigrationProbe.ApplyAsync(db,false),
            "migration-upgrade"=>await MigrationProbe.ApplyAsync(db,true),
            "migration-current-contract"=>await MigrationProbe.CaptureCurrentContractAsync(db),
            "migration-legacy"=>await MigrationProbe.CaptureLegacyAsync(db),
            "migration-compatible"=>await MigrationProbe.ApplyCompatibleAsync(db,await PrivateJson<LegacyContract>(Required("baseline-contract"))),
            _=>await MigrationProbe.FailPendingIndexesAsync(db)
        };await WriteEvidence(result);
    }else if(command is "generation" or "micro") {
        var bytes=await File.ReadAllBytesAsync(Required("snapshot"));var env=Guid.Parse(Required("environment"));var snapshot=SnapshotValidator.ParsePayload(bytes,env);
        await WriteEvidence(command=="generation"?(object)GenerationProbe.MeasureLifecycle(snapshot,bytes):LookupMicroProbe.Measure(snapshot,bytes));
    }else throw new ArgumentException("Unknown performance command.");
    return 0;
}catch(Exception error)when(error is not OperationCanceledException) {
    // Configuration and database errors can contain secret material in their messages.
    Console.Error.WriteLine("Performance command failed: "+error.GetType().Name);return 3;
}
