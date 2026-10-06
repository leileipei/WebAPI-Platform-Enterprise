using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Comparisons.Rules;
namespace WebApi.Infrastructure.Comparisons;
public sealed class ContractComparisonEngine
{
    public const string EngineVersion="compatibility-v2";
    public ComparisonReport Compare(ComparisonInput input,ComparisonLimits limits,CancellationToken ct)
    {
        var findings=new List<ComparisonFinding>();var issues=new List<ComparisonCoverageIssue>();var invalid=false;
        var applied=new HashSet<string>(StringComparer.Ordinal);var sourceEvidence=new List<ComparisonSourceEvidence>();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(10));var token=deadline.Token;
        var budget=new ContractLimits(MaxDepth:limits.MaxDepth,MaxNodes:limits.MaxNodes,MaxFindings:limits.MaxFindings,MaxComparisonSideBytes:limits.MaxSideBytes,MaxComparisonPairBytes:limits.MaxPairBytes);
        // A generated HTTP envelope adds at most 16 representation levels. Raw
        // documents and standalone maintained schemas still use the original cap.
        var modelBudget=budget with{MaxDepth=budget.MaxDepth+16,MaxDocumentBytes=budget.MaxBundleBytes};
        string fingerprint;
        void Issue(string code,string source,string pointer,string reason){token.ThrowIfCancellationRequested();if(issues.Count>=limits.MaxFindings)throw new ComparisonBudgetException();applied.Add("openapi.coverage");issues.Add(new(code,source,pointer,reason));}
        void Add(ComparisonFinding finding){token.ThrowIfCancellationRequested();if(findings.Count>=limits.MaxFindings)throw new ComparisonBudgetException();if(finding.RuleId is not null)applied.Add(finding.RuleId);findings.Add(finding);}
        try{
            token.ThrowIfCancellationRequested();
            if(input.FormatMode is not(null or "Annotation" or "Strict")||input.AdapterVersion is not(null or "oas-http-model-v2"))throw new ApiException(422,"comparison_options","比较模式或适配器未登记。");
            var left=JsonSerializer.SerializeToUtf8Bytes(input.From,ContractNormalizer.JsonOptions);var right=JsonSerializer.SerializeToUtf8Bytes(input.To,ContractNormalizer.JsonOptions);
            if(left.Length>limits.MaxSideBytes||right.Length>limits.MaxSideBytes||(long)left.Length+right.Length>limits.MaxPairBytes)throw new ComparisonBudgetException();
            var a=Read(input.From,"from");var b=Read(input.To,"to");
            foreach(var key in a.Operations.Keys.Union(b.Operations.Keys,StringComparer.Ordinal).Order(StringComparer.Ordinal)){
                token.ThrowIfCancellationRequested();
                if(!a.Operations.TryGetValue(key,out var before)){Add(new("openapi|"+key+"|added","openapi",key,b.Operations[key].Pointer,"Added","Compatible","新增 Operation。",RuleId:"openapi.operations"));continue;}
                if(!b.Operations.TryGetValue(key,out var after)){Add(new("openapi|"+key+"|removed","openapi",key,before.Pointer,"Removed","Breaking","原 Operation 已删除。",RuleId:"openapi.operations"));continue;}
                foreach(var field in new[]{"summary","description","operationId","tags","externalDocs","deprecated"})if(!JsonNode.DeepEquals(before.Value[field],after.Value[field]))Add(new("openapi|"+key+"|metadata|"+field,"openapi",key,before.Pointer+"/"+field,"Changed","Compatible","Operation 元数据发生变化，接受域规则另行复核。",RuleId:"openapi.operations"));
                applied.Add("openapi.parameters");applied.Add("openapi.security");
                if(a.RequestWireCompatible(before,b,after))Prove(a.Request(before),b.Request(after),"request",key,before.Pointer+"/request","openapi.request");
                foreach(var group in ResponseCoverageRules.Groups(a,before,b,after)){
                    token.ThrowIfCancellationRequested();if(!a.ResponseWireCompatible(before,group.Before,b,group.After))continue;
                    Prove(a.Response(before,group.Before,group.Status),b.Response(after,group.After,group.Status),"response",key,before.Pointer+"/responses/"+group.Status,"openapi.responses");
                }
            }
            if(a.Operations.Count==0&&b.Operations.Count==0&&(input.From.Parameters.Count>0||input.To.Parameters.Count>0||input.From.Schemas.Count>0||input.To.Schemas.Count>0)){
                if(a.ManagedWireCompatible(b))Prove(a.ManagedRequest(),b.ManagedRequest(),"request","definitions","/definitions/request","openapi.request");
                var seen=new HashSet<(string,string)>();
                for(var status=100;status<=599;status++){var fromKey=a.ManagedResponseKey(status);var toKey=b.ManagedResponseKey(status);if(toKey.Length>0&&seen.Add((fromKey,toKey)))Prove(a.ManagedResponse(status),b.ManagedResponse(status),"response","definitions","/definitions/responses/"+status,"openapi.responses");}
                var fromComponents=input.From.Schemas.Where(x=>x.SchemaType=="component").ToDictionary(x=>x.Name,StringComparer.Ordinal);var toComponents=input.To.Schemas.Where(x=>x.SchemaType=="component").ToDictionary(x=>x.Name,StringComparer.Ordinal);
                foreach(var name in fromComponents.Keys.Union(toComponents.Keys,StringComparer.Ordinal))if(!fromComponents.TryGetValue(name,out var old)||!toComponents.TryGetValue(name,out var next)||ContractNormalizer.Canonical(JsonNode.Parse(old.SchemaJson))!=ContractNormalizer.Canonical(JsonNode.Parse(next.SchemaJson)))Issue("component_usage_unknown","definitions","/definitions/components/"+ContractNormalizer.Pointer(name),"组件使用方向未明确，不能独立宣称接受域兼容。");
            }
            fingerprint=ContractNormalizer.Fingerprint(input,EngineVersion);
        }catch(Exception error)when(error is ComparisonBudgetException or OperationCanceledException or ApiException or JsonException or ArgumentException or InvalidOperationException){
            invalid=true;findings.Clear();issues.Clear();issues.Add(new(error is OperationCanceledException?"comparison_deadline":"invalid_comparison","input","/","契约输入、输出或执行超过预算，未生成可接受的部分结果。"));
            fingerprint=ContractNormalizer.Hash(JsonSerializer.SerializeToUtf8Bytes(input,ContractNormalizer.JsonOptions));
        }
        var ordered=findings.OrderBy(x=>x.Key,StringComparer.Ordinal).ToArray();var coverage=issues.Distinct().OrderBy(x=>x.Source,StringComparer.Ordinal).ThenBy(x=>x.Pointer,StringComparer.Ordinal).ThenBy(x=>x.Code,StringComparer.Ordinal).ToArray();
        return new(EngineVersion,fingerprint,invalid?"Invalid":coverage.Length>0?"Limited":"Complete",new(ordered.Count(x=>x.ChangeKind=="Added"),ordered.Count(x=>x.ChangeKind=="Changed"),ordered.Count(x=>x.ChangeKind=="Removed"),ordered.Count(x=>x.Risk=="Compatible"),ordered.Count(x=>x.Risk=="Breaking"),ordered.Count(x=>x.Risk=="Unknown")+coverage.Length),ordered,coverage,new(ContractNormalizer.Hash(CanonicalJson.Serialize(input)),input.AdapterVersion??"oas-http-model-v2",input.FormatMode??"Annotation",sourceEvidence,applied.Order(StringComparer.Ordinal).ToArray()));
        OpenApiContractRules Read(ContractVersionInput version,string side){
            var bundle=ComparisonContractReader.Read(version,budget,token,(code,pointer,reason)=>Issue(code,side,pointer,reason));
            sourceEvidence.Add(new(side,version.Sources is null?"legacy_document":"fixed_bundle",version.Sources?.BundleHash??ContractNormalizer.Hash(System.Text.Encoding.UTF8.GetBytes(version.Version.OpenapiDocument??"null"))));
            var definitions=version.Schemas.Select(x=>new MaintainedDefinition(x.Id,"schema",x.Name,x.SchemaJson,x.SchemaType=="component")).Concat(version.Parameters.Select(x=>new MaintainedDefinition(x.Id,"parameter",x.Name,x.Schema??new JsonObject{["type"]=x.DataType}.ToJsonString(),false))).ToArray();
            if(version.Parameters.GroupBy(x=>ContractNormalizer.ParameterKey(x.Location,x.Name),StringComparer.Ordinal).Any(x=>x.Count()>1)||version.Schemas.GroupBy(x=>ContractNormalizer.SchemaKey(x.SchemaType,x.Name,x.StatusCode,x.ContentType),StringComparer.Ordinal).Any(x=>x.Count()>1))throw new ApiException(422,"duplicate_definition","维护定义标识重复。");
            var metadata=version.Sources?.Metadata??new ContractSourceMetadata(bundle.Documents.Select(x=>new ContractResourceSource(x.Source.LogicalUri,x.Source.Format)).ToArray(),[]);
            new OpenApiContractRules(bundle,modelBudget,token,(code,pointer,reason)=>Issue(code,side,pointer,reason),selectedOperations:ComparisonContractReader.SelectedOperations(version),formatMode:input.FormatMode??"Annotation").CheckMaintenanceSources(version,metadata);
            var maintained=VersionContractSourceService.BuildMaintenanceGraph(new(bundle,metadata,0),definitions,token,modelBudget);
            return new(maintained.Bundle,modelBudget,token,(code,pointer,reason)=>Issue(code,side,pointer,reason),version,maintained.Definitions,ComparisonContractReader.SelectedOperations(version),input.FormatMode??"Annotation");
        }
        void Prove(OpenApiContractRules.Model baseline,OpenApiContractRules.Model target,string direction,string key,string pointer,string family){
            token.ThrowIfCancellationRequested();var proof=new CompatibilityProof().Compare(baseline.Schema,target.Schema,baseline.Bundle,target.Bundle,direction,modelBudget,token,baseline.Location,target.Location,input.FormatMode??"Annotation");
            applied.UnionWith(proof.AppliedRuleIds);applied.Add(family);
            foreach(var item in proof.CoverageIssues){if(item.Code.Contains("budget",StringComparison.Ordinal)||item.Code.Contains("cancelled",StringComparison.Ordinal))throw new ComparisonBudgetException();if(item.Code=="invalid_schema")throw new ApiException(422,item.Code,item.Reason);Issue(item.Code,"openapi",pointer+item.Pointer,item.Reason);}
            if(proof.Risk=="Compatible"){if(proof.Changed==true)Add(new("openapi|"+key+"|"+pointer+"|compatible","openapi",key,pointer,"Changed","Compatible","完整接受域变化已有包含证明。",RuleId:family));return;}
            if(proof.Risk=="Breaking")foreach(var finding in proof.Findings){var location=finding.Pointer.Contains("/components/schemas/__webapi_http_contract",StringComparison.Ordinal)?pointer:finding.Pointer.StartsWith("/paths/",StringComparison.Ordinal)||finding.Pointer.StartsWith("/components/",StringComparison.Ordinal)?finding.Pointer:pointer;Add(finding with{Key="openapi|"+key+"|"+pointer+"|"+finding.Key,Source="openapi",Operation=key,Pointer=location,RuleId=family});}
            else if(proof.CoverageIssues.Count==0)Issue("openapi_inclusion_unproved","openapi",pointer,"完整 HTTP 接受域的包含关系未证明。");
        }
    }
}
internal static class ComparisonSetExtensions
{
    internal static IEnumerable<string> SymmetricExcept(this IEnumerable<string> left,IEnumerable<string> right)=>left.Except(right,StringComparer.Ordinal).Concat(right.Except(left,StringComparer.Ordinal)).Order(StringComparer.Ordinal);
}
