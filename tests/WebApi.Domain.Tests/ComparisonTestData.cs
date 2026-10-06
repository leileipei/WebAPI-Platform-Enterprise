using WebApi.Contracts.Catalog;
using WebApi.Infrastructure.Comparisons;
namespace WebApi.Domain.Tests;
internal static class ComparisonTestData
{
    internal const string EmptyDocument="{\"openapi\":\"3.0.3\",\"paths\":{}}";
    internal static readonly Guid ApiId=Guid.Parse("00000000-0000-0000-0000-000000000001");
    internal static ContractVersionInput Version(string document=EmptyDocument,string status="Draft",long revision=1)=>new(new VersionDto(Guid.NewGuid(),ApiId,"1.0",status,"compatible",document,null,"json",null,Guid.Empty,DateTimeOffset.UnixEpoch,null,revision),[],[]);
    internal static ComparisonInput Schemas(string before,string after,string direction="request")
    {
        var a=Version();var b=Version();
        return new(a with {Schemas=[new(Guid.NewGuid(),a.Version.Id,direction,"Body",null,"application/json",before,null,null)]},b with {Schemas=[new(Guid.NewGuid(),b.Version.Id,direction,"Body",null,"application/json",after,null,null)]});
    }
    internal static ComparisonInput Parameters(bool requiredBefore,bool requiredAfter,string location="query",string fromName="name",string toName="name")
    {
        var a=Version();var b=Version();return new(a with {Parameters=[new(Guid.NewGuid(),a.Version.Id,location,fromName,"string",requiredBefore,"{\"type\":\"string\"}",null,null)]},b with {Parameters=[new(Guid.NewGuid(),b.Version.Id,location,toName,"string",requiredAfter,"{\"type\":\"string\"}",null,null)]});
    }
}
