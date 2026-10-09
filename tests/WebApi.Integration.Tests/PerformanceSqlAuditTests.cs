using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.Json;
using WebApi.Infrastructure.Persistence;
using WebApi.Integration.Tests.Support;
using WebApi.PerformanceHost;
using Xunit;

namespace WebApi.Integration.Tests;
public sealed class PerformanceSqlAuditTests
{
    [Fact]
    public async Task AuditStoresActualTemplatesPrivatelyWithoutParameterValuesOrPublicSql()
    {
        if(OperatingSystem.IsWindows())return;
        await using var fixture=new PostgresDatabase();await fixture.InitializeAsync();
        var directory=Path.Combine(Path.GetTempPath(),"performance-sql-audit-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try {
            var probe=new RequestSqlProbe(audit:new PrivateSqlShapeAudit(directory));using var scope=probe.Begin("audit");
            await using var db=new WebApiDbContext(new DbContextOptionsBuilder<WebApiDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(probe).Options);
            const string canary="private-sql-parameter-canary";
            for(var i=0;i<2;i++)Assert.Equal(canary,await db.Database.SqlQueryRaw<string>("SELECT @secret AS \"Value\"",new NpgsqlParameter("secret",canary)).SingleAsync());
            var file=Assert.Single(Directory.GetFiles(directory));var text=await File.ReadAllTextAsync(file);
            Assert.Contains("@secret",text);Assert.DoesNotContain(canary,text);
            Assert.Equal(UnixFileMode.UserRead|UnixFileMode.UserWrite,File.GetUnixFileMode(file));
            Assert.Equal(2,probe.Read("audit").Unclassified);
            var exported=JsonSerializer.Serialize(probe.ReadShapes("audit"));Assert.DoesNotContain("SELECT",exported);Assert.DoesNotContain(canary,exported);
        }finally{Directory.Delete(directory,true);}
    }
    [Fact]
    public void AuditRejectsSharedDirectoryAndNeverFollowsExistingSymbolicFile()
    {
        if(OperatingSystem.IsWindows())return;
        var root=Path.Combine(Path.GetTempPath(),"performance-audit-mode-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
        try {
            File.SetUnixFileMode(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute|UnixFileMode.GroupRead);
            Assert.Throws<InvalidDataException>(()=>new PrivateSqlShapeAudit(root));
            File.SetUnixFileMode(root,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
            const string hash="e004ebd5b5532a4b85984a62f8ad48a81aa3460c1ca07701f386135d72cdecf5";
            var target=Path.Combine(root,"protected");File.WriteAllText(target,"keep-original");
            File.CreateSymbolicLink(Path.Combine(root,hash+".sql"),target);
            var audit=new PrivateSqlShapeAudit(root);Assert.Throws<InvalidDataException>(()=>audit.Record(hash,"SELECT 1"));
            Assert.Equal("keep-original",File.ReadAllText(target));
        }finally{Directory.Delete(root,true);}
    }
}
