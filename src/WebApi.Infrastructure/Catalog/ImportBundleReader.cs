using System.Text.Json.Nodes;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
namespace WebApi.Infrastructure.Catalog;
internal sealed record ImportBundleResult(ContractBundle Bundle,IReadOnlyList<ContractIssue> Issues);
internal sealed class ImportBundleReader(ImportSourceFetcher fetcher,ImportSourceSettings settings)
{
    public async Task<ImportBundleResult> ReadAsync(CreateImportPreviewRequest request,ImportSourcePolicyDto policy,CancellationToken ct)
    {
        if((request.SourceText is null)==(request.SourceUrl is null)||request.SourceUrl is not null&&request.Files is {Count:>0})throw Invalid("根来源须选择文本/文件或URL，不能混用。");
        var reader=new ContractDocumentReader();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(settings.NetworkTimeout);
        var docs=new List<ContractDocument>();var issues=new List<ContractIssue>();
        try{
            ContractSource rootSource;
            if(request.SourceUrl is string url){if(!Uri.TryCreate(url,UriKind.Absolute,out var uri))throw Invalid("根URL不合法。");rootSource=await fetcher.FetchAsync(uri,policy,deadline.Token);if(request.Format is not null)rootSource=rootSource with{Format=request.Format};}
            else rootSource=new(new("https://import.invalid/openapi"),request.SourceText!,request.Format??"auto");
            var root=reader.Read(rootSource,policy.Limits,ct);docs.Add(root);
            if(request.Files is {Count:>0} files){if(files.Count>=policy.Limits.MaxResources)throw Budget();var names=new HashSet<string>(StringComparer.Ordinal);foreach(var file in files){ValidateName(file.Name);var uri=new Uri(root.Source.LogicalUri,file.Name);if(uri==root.Source.LogicalUri||!names.Add(uri.AbsoluteUri))throw Invalid("附带文件的逻辑名称重复。");docs.Add(reader.ReadResource(new(uri,file.Content,file.Format??"auto"),policy.Limits,root.Dialect,ct));}}
            var attempted=new HashSet<string>(docs.Select(x=>x.Source.LogicalUri.AbsoluteUri),StringComparer.Ordinal);
            while(true){
                ct.ThrowIfCancellationRequested();var bundle=ContractBundleCodec.Create(root.Source.LogicalUri,docs,policy.Limits);var registry=new ContractReferenceRegistry(bundle,policy.Limits);var fetched=false;
                foreach(var reference in registry.References){
                    try{registry.Resolve(reference.ResourceUri,reference.Reference);continue;}catch(ApiException e)when(e.Code=="missing_contract_reference"){}
                    if(!Uri.TryCreate(reference.ResourceUri,reference.Reference,out var uri)){AddIssue(reference,"missing_contract_reference");continue;}var target=new Uri(uri.AbsoluteUri.Split('#')[0]);
                    if(attempted.Contains(target.AbsoluteUri)){AddIssue(reference,"missing_contract_reference");continue;}
                    if(request.SourceUrl is not null&&attempted.Count>=policy.Limits.MaxResources)throw Budget();
                    attempted.Add(target.AbsoluteUri);
                    if(request.SourceUrl is null){AddIssue(reference,"missing_contract_reference");continue;}
                    if(docs.Count>=policy.Limits.MaxResources)throw Budget();
                    try{var source=await fetcher.FetchAsync(target,policy,deadline.Token);docs.Add(reader.ReadResource(source,policy.Limits,root.Dialect,ct));fetched=true;}
                    catch(ApiException e){AddIssue(reference,e.Code);}
                }
                if(fetched)continue;
                return new(bundle,issues);
            }
        }catch(OperationCanceledException)when(!ct.IsCancellationRequested){throw new ApiException(504,"import_source_timeout","整次预览的来源获取超过期限。");}
        void AddIssue(ContractReference reference,string code){if(issues.Count>=policy.Limits.MaxDiagnostics)throw Budget();if(!issues.Any(x=>x.Pointer==reference.Pointer&&x.Code==code))issues.Add(new(code,reference.Pointer,"该引用未取得可用固定来源，相关Operation不可导入。"));}
    }
    private static void ValidateName(string name)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>2048||name!=name.Trim()||name.StartsWith('/')||name.Contains('\\')||name.Contains(':')||name.Contains('?')||name.Contains('#')||name.Contains('%')||name.Any(char.IsControl)||name.Split('/').Any(x=>x is "" or "." or ".."))throw Invalid("附带文件须使用安全的相对逻辑名称。");
    }
    private static ApiException Invalid(string message)=>new(422,"invalid_import_source",message);
    private static ApiException Budget()=>new(422,"contract_bundle_budget","来源包数量或诊断超过预算。");
}
