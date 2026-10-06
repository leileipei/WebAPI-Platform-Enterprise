using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
namespace WebApi.Infrastructure.Catalog;
internal sealed class ContractExampleProjection(VersionContractState? state,CancellationToken ct)
{
    private readonly ContractReferenceRegistry? graph=state is null?null:new(state.Bundle,new());
    private long bytes;private int count;
    internal IReadOnlyList<ContractExampleOption>? Read(Guid id,string kind,string? raw)
    {
        var options=new List<ContractExampleOption>();
        if(raw is not null)try{var shape=new ContractDocumentReader().ReadResource(new(new Uri("https://examples.invalid/maintenance.json"),raw,"json"),new(),ContractDialect.Oas31,ct).Root;Inline(shape,"Schema");}catch(ApiException){Unavailable("维护 Schema 示例","维护原文无法解析，请查看 Schema 诊断。");}
        var source=state?.Metadata.Definitions.SingleOrDefault(x=>x.Id==id&&x.Kind==kind);
        if(source is not null&&state is not null&&graph is not null){
            try{
                var location=VersionContractSourceService.Physical(state.Bundle,graph,source);var schema=graph.Resolve(location.Uri,"#"+Uri.EscapeDataString(location.Pointer)).Node;Inline(schema,"来源 Schema");
                var slash=location.Pointer.LastIndexOf('/');if(slash>0&&location.Pointer[slash..]=="/schema"){
                    var parent=graph.Resolve(location.Uri,"#"+Uri.EscapeDataString(location.Pointer[..slash])).Node;
                    if(parent is JsonObject obj){if(obj.ContainsKey("example"))Value("OpenAPI example",obj["example"]);if(obj["examples"] is JsonObject examples)foreach(var(name,item)in examples){ct.ThrowIfCancellationRequested();Option(name,item);}}
                }
            }catch(ApiException)when(VersionContractSourceService.IsIndependentSource(state.Bundle,source)){}
            catch(ApiException){Unavailable("来源示例","固定示例引用无法解析，未自动获取。");}
        }
        var result=options.Distinct().ToArray();return result.Length==0?null:result;
        void Inline(JsonNode schema,string label){if(schema is not JsonObject obj)return;if(obj.ContainsKey("example"))Value(label+" example",obj["example"]);if(obj["examples"] is JsonArray list)for(var i=0;i<list.Count;i++)Value(label+" examples ["+i+"]",list[i]);}
        void Option(string name,JsonNode? item){
            if(item is not JsonObject option){Unavailable(name,"示例对象结构无法解析。");return;}var seen=new HashSet<JsonNode>(ReferenceEqualityComparer.Instance);
            while(option["$ref"] is JsonValue reference&&reference.TryGetValue<string>(out var text)){
                if(!seen.Add(option)||seen.Count>64){Unavailable(name,"示例引用循环或超过预算。");return;}
                option=graph!.Resolve(graph.Describe(option).ResourceUri,text).Node as JsonObject??throw new ApiException(422,"invalid_example","示例引用目标不是对象。");
            }
            if(option.ContainsKey("value"))Value(name,option["value"]);
            else if(option["externalValue"] is JsonValue external&&external.TryGetValue<string>(out var uri)){
                var length=Encoding.UTF8.GetByteCount(uri);if(length>4096||bytes+length>2*1024*1024){Unavailable(name,"外部示例地址超过显示预算，未验证；完整内容仍在原始来源中。");return;}bytes+=length;Add(new(Label(name),ExternalValue:uri,UnverifiedReason:"外部示例未验证，不自动获取。"));
            }
            else Unavailable(name,"示例未提供值，未验证。");
        }
        void Value(string name,JsonNode? value){ct.ThrowIfCancellationRequested();if(options.Count>=100||count>=500){Unavailable("其余示例","示例选项已截断，完整内容仍在原始来源中。");return;}if(bytes>=2*1024*1024){Unavailable(name,"示例选项总量已截断，完整内容仍在原始来源中。");return;}var json=value?.ToJsonString(new JsonSerializerOptions{MaxDepth=128})??"null";var length=Encoding.UTF8.GetByteCount(json);if(length>256*1024){Unavailable(name,"示例超过256KiB，未自动展开。");return;}if(bytes+length>2*1024*1024){Unavailable(name,"示例选项总量已截断，完整内容仍在原始来源中。");return;}bytes+=length;Add(new(Label(name),Json:json));}
        void Unavailable(string name,string reason)=>Add(new(Label(name),UnverifiedReason:reason));
        void Add(ContractExampleOption option){if(options.Count>=100||count>=500){if(options.Count==0||options[^1].Name!="其余示例")options.Add(new("其余示例",UnverifiedReason:"示例选项已截断，完整内容仍在原始来源中。"));return;}count++;options.Add(option);}
    }
    private static string Label(string name)=>name.Length>128?name[..128]+"…":name;
}
