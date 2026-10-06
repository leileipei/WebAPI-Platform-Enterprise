using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using WebApi.Contracts.Common;
namespace WebApi.Infrastructure.Comparisons;
public sealed record ComparisonCursor(Guid ApiId,Guid? ReviewId,DateTimeOffset CreatedAt,Guid Id);
public sealed class ComparisonCursorCodec(IDataProtectionProvider provider)
{
    private readonly IDataProtector protector=provider.CreateProtector("WebApi.Comparisons.Cursor.v1");
    public string Encode(Guid apiId,Guid? reviewId,DateTimeOffset time,Guid id)=>protector.Protect(JsonSerializer.Serialize(new ComparisonCursor(apiId,reviewId,time,id)));
    public ComparisonCursor Decode(string token,Guid apiId,Guid? reviewId)
    {
        try{if(token.Length>2048)throw new CryptographicException();var value=JsonSerializer.Deserialize<ComparisonCursor>(protector.Unprotect(token));if(value is null||value.ApiId!=apiId||value.ReviewId!=reviewId)throw new CryptographicException();return value;}
        catch(Exception e) when(e is CryptographicException or JsonException or FormatException){throw new ApiException(400,"invalid_comparison_cursor","分页游标无效或查询条件已改变。");}
    }
}
