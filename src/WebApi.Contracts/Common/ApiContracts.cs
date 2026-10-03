namespace WebApi.Contracts.Common;
public sealed record PageResult<T>(IReadOnlyList<T> Items, int Total, int Page, int PageSize);
public sealed record CommandResult<T>(T Value, string ETag);
public sealed class ApiException(int status, string code, string message) : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public static class RevisionTag
{
    public static string Format(long revision) => $"\"{revision}\"";
    public static void Require(string? supplied, long current)
    {
        if(supplied != Format(current)) throw new ApiException(412,"stale_revision","数据已更新，请刷新后重试。");
    }
}

public static class Pagination
{
    public static PageResult<T> Slice<T>(IReadOnlyList<T> items,int? page,int? pageSize)
    {
        var p=Math.Clamp(page??1,1,1000000);var size=Math.Clamp(pageSize??50,1,100);
        return new(items.Skip((p-1)*size).Take(size).ToArray(),items.Count,p,size);
    }
}
