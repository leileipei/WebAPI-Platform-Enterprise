using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Catalog;
namespace WebApi.Infrastructure.Contracts;
public sealed class SchemaEvaluator
{
    public SchemaValidationResult Evaluate(SchemaValidationInput input,ContractLimits limits,CancellationToken ct)
    {
        var dialect=input.Bundle.Documents.FirstOrDefault(x=>x.Source.LogicalUri==input.Bundle.RootUri)?.Dialect??ContractDialect.Oas31;
        var issues=new List<SchemaValidationIssue>();var coverage=new List<ContractIssue>();
        var schemaHash="";var exampleHash="";var nodes=0;
        ContractReferenceRegistry? graph=null;
        SchemaValidationResult Result(string status)=>new(status,dialect==ContractDialect.Oas30?"oas-3.0":"oas-3.1",schemaHash,exampleHash,input.FormatMode,issues,coverage);
        try {
            ct.ThrowIfCancellationRequested();
            if(input.Direction is not("request" or "response")||input.FormatMode is not("Annotation" or "Strict"))throw new ApiException(422,"invalid_schema_options","Schema校验选项不合法。");
            ContractBundleCodec.Verify(input.Bundle,limits);
            var schemaJson=input.Schema.ToJsonString();var exampleJson=input.Example?.ToJsonString()??"null";
            if(Encoding.UTF8.GetByteCount(schemaJson)+input.Bundle.Documents.Sum(x=>Encoding.UTF8.GetByteCount(x.CanonicalJson))>limits.MaxBundleBytes||input.HasExample&&Encoding.UTF8.GetByteCount(exampleJson)>limits.MaxExampleBytes)throw new ApiException(422,"contract_budget","校验输入超过预算。");
            schemaHash=CatalogService.Hash(schemaJson);if(input.HasExample)exampleHash=CatalogService.Hash(exampleJson);
            foreach(var document in input.Bundle.Documents)Count(document.Root,0);
            if(input.HasExample)Count(input.Example,0);
            graph=new ContractReferenceRegistry(input.Bundle,limits);
            var selected=graph.Resolve(input.ResourceUri??input.Bundle.RootUri,"#"+input.Pointer);
            if(!JsonNode.DeepEquals(selected.Node,input.Schema))throw new ApiException(422,"invalid_schema_location","所选Schema与固定来源位置不一致。");
            var engine=new OfflineContractSchemas(graph,dialect,input.Direction,input.FormatMode,coverage,ct);
            var schema=engine.Build(selected);
            if(!input.HasExample)return Result("Valid");
            using var example=JsonDocument.Parse(exampleJson,new(){MaxDepth=limits.MaxDepth});
            ct.ThrowIfCancellationRequested();
            var evaluated=schema.Evaluate(example.RootElement,new(){OutputFormat=OutputFormat.Hierarchical,RequireFormatValidation=input.FormatMode=="Strict",FormatRegistry=ContractFormats.Create()});
            ct.ThrowIfCancellationRequested();
            if(!evaluated.IsValid)Collect(evaluated);
            return Result(evaluated.IsValid?"Valid":"Invalid");
        }catch(InvalidSchemaShapeException){issues.Add(new("",input.Pointer,"$schema","invalid_schema","Schema结构不符合登记方言。"));return Result("Invalid");}
        catch(ApiException error){if(coverage.Count==0)coverage.Add(new(error.Code,input.Pointer,error.Message));return Result("Incomplete");}
        catch(OperationCanceledException){coverage.Add(new("schema_cancelled",input.Pointer,"Schema校验已取消。"));return Result("Incomplete");}
        catch(Exception error)when(error is JsonSchemaException or JsonException or ArgumentException or InvalidOperationException){coverage.Add(new("schema_evaluation_incomplete",input.Pointer,"Schema语义无法在固定来源包中完成校验。"));return Result("Incomplete");}
        void Count(JsonNode? node,int depth)
        {
            ct.ThrowIfCancellationRequested();
            if(depth>limits.MaxDepth||++nodes>limits.MaxNodes)throw new ApiException(422,"contract_budget","Schema和实例的深度或总节点超过预算。");
            if(node is JsonObject obj)foreach(var property in obj){if(++nodes>limits.MaxNodes)throw new ApiException(422,"contract_budget","Schema和实例的总节点超过预算。");Count(property.Value,depth+1);}
            else if(node is JsonArray array)foreach(var item in array)Count(item,depth+1);
        }
        void Collect(EvaluationResults result)
        {
            if(result.IsValid)return;
            if(result.Errors is not null)foreach(var(keyword,_)in result.Errors){
                if(issues.Count>=limits.MaxDiagnostics)throw new ApiException(422,"contract_budget","Schema诊断超过预算。");
                var pointer=Uri.UnescapeDataString(result.SchemaLocation.Fragment.TrimStart('#'));
                int? line=null,column=null;var uri=new Uri(result.SchemaLocation.AbsoluteUri.Split('#')[0]);
                var doc=input.Bundle.Documents.FirstOrDefault(x=>x.Source.LogicalUri==uri);
                if(doc?.Locations.TryGetValue(pointer+"/"+ContractDocumentReader.Escape(keyword),out var position)==true){line=position.Line;column=position.Column;}
                else if(graph is not null)try {
                    var identity=graph.Describe(graph.Resolve(uri,"#"+Uri.EscapeDataString(pointer)).Node);
                    foreach(var source in input.Bundle.Documents)try {
                        var physical=graph.Describe(graph.Resolve(source.Source.LogicalUri,"#"+Uri.EscapeDataString(identity.Pointer)).Node);
                        if(physical.ResourceUri!=identity.ResourceUri)continue;
                        if(source.Locations.TryGetValue(identity.Pointer+"/"+ContractDocumentReader.Escape(keyword),out var located)){line=located.Line;column=located.Column;break;}
                    }catch(ApiException){}
                }catch(ApiException){}
                issues.Add(new(result.InstanceLocation.ToString(),pointer,keyword,"schema_"+(keyword.Length==0?"false":keyword),"实例不满足该Schema约束。",line,column));
            }
            if(result.Details is not null)foreach(var detail in result.Details)Collect(detail);
        }
    }
}
