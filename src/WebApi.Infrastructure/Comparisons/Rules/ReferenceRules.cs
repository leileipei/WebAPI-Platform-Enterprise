using System.Text.Json.Nodes;
using WebApi.Contracts.Common;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Contracts;
namespace WebApi.Infrastructure.Comparisons.Rules;
internal static class ReferenceRules
{
    internal static SchemaProofLocation Locate(JsonNode node,ContractBundle bundle,SchemaProofLocation? specified,ContractLimits limits,CancellationToken ct)
    {
        ContractBundleCodec.Verify(bundle,limits);if(specified is not null){var found=new ContractReferenceRegistry(bundle,limits).Resolve(specified.ResourceUri,"#"+Uri.EscapeDataString(specified.Pointer));if(!JsonNode.DeepEquals(found.Node,node))throw new ApiException(422,"schema_proof_location","Schema与固定来源位置不一致。");return specified;}
        foreach(var doc in bundle.Documents){var pointer=Find(doc.Root,node,"");if(pointer is not null)return new(doc.Source.LogicalUri,pointer);}
        var candidates=new List<SchemaProofLocation>();foreach(var doc in bundle.Documents)Scan(doc.Root,"",doc.Source.LogicalUri);if(candidates.Count!=1)throw new ApiException(422,"schema_proof_location","所选Schema来源不唯一或不在固定包中。");return candidates[0];
        string? Find(JsonNode? current,JsonNode selected,string pointer){ct.ThrowIfCancellationRequested();if(ReferenceEquals(current,selected))return pointer;if(current is JsonObject obj)foreach(var(key,child)in obj){var result=Find(child,selected,pointer+"/"+ContractDocumentReader.Escape(key));if(result is not null)return result;}else if(current is JsonArray array)for(var i=0;i<array.Count;i++){var result=Find(array[i],selected,pointer+"/"+i);if(result is not null)return result;}return null;}
        void Scan(JsonNode? current,string pointer,Uri uri){ct.ThrowIfCancellationRequested();if(current is null)return;if(JsonNode.DeepEquals(current,node))candidates.Add(new(uri,pointer));if(current is JsonObject obj)foreach(var(key,child)in obj)Scan(child,pointer+"/"+ContractDocumentReader.Escape(key),uri);else if(current is JsonArray array)for(var i=0;i<array.Count;i++)Scan(array[i],pointer+"/"+i,uri);}
    }
    internal static JsonNode Normalize(JsonNode selected,ContractReferenceRegistry graph,ProofContext context,ContractDialect dialect=ContractDialect.Oas31)
    {
        context.BeginNormalization();return Walk(selected,new(ReferenceEqualityComparer.Instance),0);
        JsonNode Walk(JsonNode node,HashSet<JsonNode> path,int depth)
        {
            context.Check();if(depth>context.MaxDepth)throw new ApiException(422,"schema_proof_budget","引用证明超过深度预算。");if(!path.Add(node)){context.Issue("recursive_schema_reference","","递归包含关系无法在有限规则中证明。");return context.Copy(node)!;}
            try{
                if(node is not JsonObject original)return context.Copy(node)!;
                var obj = (JsonObject)DialectAdapter.PrepareSchema(original, graph.DialectOf(node), "request", applyDirection:false, maxDepth:context.MaxDepth).Node;
                if(obj.ContainsKey("$dynamicRef")||obj.ContainsKey("$dynamicAnchor")){context.Add("schema.dynamic_references");context.Issue("dynamic_schema_reference","","动态作用域变化无法自动证明。");}
                JsonNode? referenced=null;
                if(obj["$ref"] is JsonValue value&&value.TryGetValue<string>(out var text)){
                    context.Add("schema.references");var target=graph.Resolve(graph.Describe(node).ResourceUri,text);referenced=Walk(target.Node,path,depth+1);
                    if(graph.DialectOf(node)==ContractDialect.Oas30)return referenced;
                }
                context.Reserve(2);var result=new JsonObject();
                foreach(var(key,child)in obj){
                    if(key is "$ref" or "$id" or "$anchor" or "$defs" or "definitions"){if(key!="$ref")context.Add("schema.references");continue;}
                    context.Reserve(System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(key))+2);
                    if(SchemaNavigation.MapKeywords.Contains(key)&&child is JsonObject map){var next=new JsonObject();foreach(var(name,item)in map){context.Reserve(System.Text.Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(name))+2);next[name]=item is null?context.Copy(null):Walk(original[key]![name]!,path,depth+1);}result[key]=next;}
                    else if(SchemaNavigation.ArrayKeywords.Contains(key)&&child is JsonArray list){var next=new JsonArray();for(var i=0;i<list.Count;i++)next.Add(list[i] is null?null:Walk(original[key]![i]!,path,depth+1));result[key]=next;}
                    else if(SchemaNavigation.SingleKeywords.Contains(key)&&child is not null)result[key]=Walk(original[key]!,path,depth+1);
                    else result[key]=context.Copy(child);
                }
                if(referenced is null)return result;
                if(result.All(x=>PrimitiveRules.Metadata.Contains(x.Key))){if(referenced is JsonObject referenceObject){foreach(var(key,child)in result) {
                        // Direction annotations collect true from either the reference or its sibling.
                        if (key is "readOnly" or "writeOnly" && referenceObject[key] is JsonValue annotation && annotation.TryGetValue<bool>(out var enabled) && enabled) continue;
                        referenceObject[key]=context.Copy(child);
                    }return referenceObject;}if(result.Count==0)return referenced;}
                return new JsonObject{["allOf"]=new JsonArray(referenced,result)};
            }finally{path.Remove(node);}
        }
    }
}
