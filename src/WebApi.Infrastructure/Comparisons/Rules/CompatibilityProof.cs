using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WebApi.Contracts.Catalog;
using WebApi.Contracts.Common;
using WebApi.Contracts.Comparisons;
using WebApi.Contracts.OpenApi;
using WebApi.Infrastructure.Catalog;
using WebApi.Infrastructure.Contracts;
namespace WebApi.Infrastructure.Comparisons.Rules;
public sealed class CompatibilityProof
{
    public CompatibilityProofResult Compare(JsonNode baseline,JsonNode target,ContractBundle baselineBundle,ContractBundle targetBundle,string direction,ContractLimits limits,CancellationToken ct,SchemaProofLocation? baselineLocation=null,SchemaProofLocation? targetLocation=null,string formatMode="Annotation")
    {
        var rules=new HashSet<string>(StringComparer.Ordinal);var coverage=new List<ComparisonCoverageIssue>();var findings=new List<ComparisonFinding>();
        CompatibilityProofResult Result(string risk)=>new(risk,findings,coverage,rules.Order(StringComparer.Ordinal).ToArray());
        try{
            ct.ThrowIfCancellationRequested();if(direction is not("request" or "response")||formatMode is not("Annotation" or "Strict"))throw new ApiException(422,"schema_proof_options","比较方向或模式不合法。");
            var a=ReferenceRules.Locate(baseline,baselineBundle,baselineLocation,limits,ct);var b=ReferenceRules.Locate(target,targetBundle,targetLocation,limits,ct);
            var context=new ProofContext(limits,ct,rules,coverage,formatMode);context.Budget(baselineBundle,targetBundle);context.InspectNumbers(baseline);context.InspectNumbers(target);
            var evaluator=new SchemaEvaluator();var ai=new SchemaValidationInput(baselineBundle,baseline,null,direction,formatMode,false,a.ResourceUri,a.Pointer);var bi=new SchemaValidationInput(targetBundle,target,null,direction,formatMode,false,b.ResourceUri,b.Pointer);
            foreach(var input in new[]{ai,bi}){context.Check();var check=evaluator.Evaluate(input,limits,ct);if(check.Status!="Valid"){
                foreach(var issue in check.CoverageIssues)coverage.Add(new(issue.Code,"schema",issue.Pointer,issue.Message));foreach(var issue in check.Issues)coverage.Add(new("invalid_schema","schema",issue.SchemaPointer,issue.Message));if(coverage.Count==0)coverage.Add(new("invalid_schema","schema","","Schema结构无法完成校验。"));return Result("Unknown");
            }}
            var ag=new ContractReferenceRegistry(baselineBundle,limits);var bg=new ContractReferenceRegistry(targetBundle,limits);
            var ar=ag.Resolve(a.ResourceUri,"#"+Uri.EscapeDataString(a.Pointer));var br=bg.Resolve(b.ResourceUri,"#"+Uri.EscapeDataString(b.Pointer));
            var an=ReferenceRules.Normalize(ar.Node,ag,context,baselineBundle.Documents.Single(x=>x.Source.LogicalUri==baselineBundle.RootUri).Dialect);var bn=ReferenceRules.Normalize(br.Node,bg,context,targetBundle.Documents.Single(x=>x.Source.LogicalUri==targetBundle.RootUri).Dialect);
            an=DialectAdapter.PrepareSchema(an,baselineBundle.Documents.Single(x=>x.Source.LogicalUri==baselineBundle.RootUri).Dialect,direction).Node;bn=DialectAdapter.PrepareSchema(bn,targetBundle.Documents.Single(x=>x.Source.LogicalUri==targetBundle.RootUri).Dialect,direction).Node;
            context.Add("schema.types");context.Register(an);context.Register(bn);
            if(coverage.Count>0)return Result("Unknown");
            var from=direction=="request"?an:bn;var to=direction=="request"?bn:an;var source=direction=="request"?ai:bi;var destination=direction=="request"?bi:ai;
            // Finite domains are proved with the same real evaluator, including every parent constraint.
            if(PrimitiveRules.FiniteValues(from) is IReadOnlyList<JsonNode?> values){
                if(values.Count>256)throw new ApiException(422,"schema_proof_enum_budget","有限枚举超过证明预算。");
                var all=true;foreach(var value in values){context.Check();var accepted=evaluator.Evaluate(source with{Example=value,HasExample=true},limits,ct);if(accepted.Status=="Incomplete")return Unknown("schema_proof_evaluation","枚举实例校验未完成。");if(accepted.Status=="Invalid")continue;
                    var next=evaluator.Evaluate(destination with{Example=value,HasExample=true},limits,ct);if(next.Status=="Incomplete")return Unknown("schema_proof_evaluation","枚举实例校验未完成。");if(next.Status=="Invalid"){Break(value,"schema.enum_const",next.Issues);all=false;break;}
                }if(all){rules.Add("schema.enum_const");return Result("Compatible");}return Result("Breaking");
            }
            if(context.Included(from,to))return Result("Compatible");
            foreach(var witness in ProofWitnesses.Generate(from,to,context)){
                context.Check();var sourceResult=evaluator.Evaluate(source with{Example=witness,HasExample=true},limits,ct);if(sourceResult.Status=="Incomplete")return Unknown("schema_proof_evaluation","反例来源校验未完成。");if(sourceResult.Status!="Valid")continue;
                var next=evaluator.Evaluate(destination with{Example=witness,HasExample=true},limits,ct);context.Check();if(next.Status=="Incomplete")return Unknown("schema_proof_evaluation","反例目标校验未完成。");if(next.Status=="Invalid"){Break(witness,"schema.types",next.Issues);return Result("Breaking");}
            }
            return Unknown("schema_inclusion_unproved","没有充分包含证明；未找到反例不能证明兼容。");
        }catch(OperationCanceledException){return Unknown("schema_proof_cancelled","比较已取消或执行期限耗尽。");}
        catch(ApiException error){return Unknown(error.Code,error.Message);}
        catch(Exception error)when(error is JsonException or ArgumentException or InvalidOperationException or RegexMatchTimeoutException or OverflowException){return Unknown("schema_proof_incomplete","Schema证明无法在预算内完成。");}
        CompatibilityProofResult Unknown(string code,string reason){coverage.Add(new(code,"schema","",reason));return Result("Unknown");}
        void Break(JsonNode? witness,string rule,IReadOnlyList<SchemaValidationIssue> issues){
            var failure=issues.LastOrDefault(x=>CompatibilityRuleCatalog.KeywordRules.ContainsKey(x.Keyword))??issues.LastOrDefault();var pointer=failure?.SchemaPointer??"";
            if(failure is not null){if(CompatibilityRuleCatalog.KeywordRules.TryGetValue(failure.Keyword,out var actual))rule=actual;if(failure.Keyword.Length>0&&!pointer.EndsWith("/"+ContractDocumentReader.Escape(failure.Keyword),StringComparison.Ordinal))pointer+="/"+ContractDocumentReader.Escape(failure.Keyword);}
            rules.Add(rule);findings.Add(new("schema|counterexample|"+pointer,"schema",null,pointer,"Changed","Breaking","存在经完整父约束校验器复核的接受域反例。",RuleId:rule,WitnessHash:CatalogService.Hash(witness is null?"null":ContractDocumentReader.Canonicalize(witness).ToJsonString())));
        }
    }
}
internal sealed class ProofContext(ContractLimits limits,CancellationToken ct,HashSet<string> rules,List<ComparisonCoverageIssue> coverage,string formatMode)
{
    private readonly Stopwatch watch=Stopwatch.StartNew();private int steps;private long normalizedSideBytes,normalizedPairBytes;private readonly Dictionary<string,bool?> pairs=new(StringComparer.Ordinal);private readonly Dictionary<string,Regex> propertyPatterns=new(StringComparer.Ordinal);
    internal string FormatMode=>formatMode;
    internal bool PropertyPatternMatches(string pattern,string name){
        Check();if(!propertyPatterns.TryGetValue(pattern,out var regex)){
            using var json=JsonDocument.Parse(new JsonObject{[pattern]=true}.ToJsonString());
            var patterns=(Dictionary<string,Regex>)Json.Schema.Keywords.PatternPropertiesKeyword.Instance.ValidateKeywordValue(json.RootElement)!;propertyPatterns[pattern]=regex=patterns[pattern];
        }var matched=regex.IsMatch(name);Check();return matched;
    }
    internal void Check(){ct.ThrowIfCancellationRequested();if(watch.Elapsed>TimeSpan.FromSeconds(10)||++steps>limits.MaxNodes)throw new ApiException(422,"schema_proof_budget","比较执行或节点预算耗尽。");}
    internal void Budget(ContractBundle a,ContractBundle b){foreach(var bundle in new[]{a,b}){ContractBundleCodec.Verify(bundle,limits);if(bundle.Documents.Sum(d=>(long)System.Text.Encoding.UTF8.GetByteCount(d.CanonicalJson))>limits.MaxComparisonSideBytes)throw new ApiException(422,"schema_proof_budget","比较单侧超过预算。");}if(a.Documents.Concat(b.Documents).Sum(d=>(long)System.Text.Encoding.UTF8.GetByteCount(d.CanonicalJson))>limits.MaxComparisonPairBytes)throw new ApiException(422,"schema_proof_budget","比较双侧超过预算。");}
    internal void BeginNormalization()=>normalizedSideBytes=0;
    internal void Reserve(long bytes){Check();normalizedSideBytes+=bytes;normalizedPairBytes+=bytes;if(normalizedSideBytes>limits.MaxComparisonSideBytes||normalizedPairBytes>limits.MaxComparisonPairBytes)throw new ApiException(422,"schema_proof_budget","引用归一化副本超过比较字节预算。");}
    internal JsonNode? Copy(JsonNode? node){if(node is null){Reserve(4);return null;}Reserve(System.Text.Encoding.UTF8.GetByteCount(node.ToJsonString()));return node.DeepClone();}
    internal void Add(string id)=>rules.Add(id);
    internal void Issue(string code,string pointer,string reason){if(coverage.Count>=limits.MaxDiagnostics)throw new ApiException(422,"schema_proof_budget","覆盖诊断超过预算。");coverage.Add(new(code,"schema",pointer,reason));}
    internal void Register(JsonNode node)
    {
        Check();if(node is not JsonObject obj)return;
        foreach(var(key,value)in obj){if(CompatibilityRuleCatalog.KeywordRules.TryGetValue(key,out var id))rules.Add(id);else Issue("unsupported_schema_keyword","/"+ContractDocumentReader.Escape(key),"未登记的关键字不能生成兼容证明。");
            if(key is "$vocabulary" or "discriminator" or "contentSchema")Issue("schema_behavior_unproved","/"+key,"声明的行为或词汇语义不能自动证明。");
            if(SchemaNavigation.MapKeywords.Contains(key)&&value is JsonObject map){foreach(var(_,mapChild)in map)if(mapChild is not null)Register(mapChild);}
            else if(SchemaNavigation.ArrayKeywords.Contains(key)&&value is JsonArray array){foreach(var arrayChild in array)if(arrayChild is not null)Register(arrayChild);}
            else if(SchemaNavigation.SingleKeywords.Contains(key)&&value is not null)Register(value);
        }
    }
    internal void InspectNumbers(JsonNode? node){Check();if(node is JsonObject obj)foreach(var(_,child)in obj)InspectNumbers(child);else if(node is JsonArray array)foreach(var child in array)InspectNumbers(child);else if(node is JsonValue value&&value.GetValueKind()==JsonValueKind.Number)_=ExactNumber.Parse(value.ToJsonString());}
    internal bool Included(JsonNode a,JsonNode b)
    {
        Check();if(PrimitiveRules.False(a)||PrimitiveRules.True(b))return true;
        var key=CatalogService.Hash(a.ToJsonString())+"|"+CatalogService.Hash(b.ToJsonString());if(pairs.TryGetValue(key,out var prior))return prior==true;pairs[key]=null;
        bool answer;if(JsonNode.DeepEquals(a,b))answer=true;
        else if(PrimitiveRules.FiniteValues(a) is IReadOnlyList<JsonNode?> finite)answer=FiniteIncluded(a,b,finite);
        else if(PrimitiveRules.TypeSubset(a,b)&&PrimitiveRules.TypeOnly(b,formatMode))answer=true;
        else if(a is JsonObject ao&&b is JsonObject bo&&PrimitiveRules.SimpleKeys(ao,bo))answer=PrimitiveRules.Prove(ao,bo,this)&&ObjectRules.Prove(ao,bo,this)&&ArrayRules.Prove(ao,bo,this);
        else answer=CompositionRules.Prove(a,b,this);
        pairs[key]=answer;return answer;
    }
    private bool FiniteIncluded(JsonNode from,JsonNode to,IReadOnlyList<JsonNode?> values)
    {
        if(values.Count>256)throw new ApiException(422,"schema_proof_enum_budget","有限枚举超过证明预算。");
        var source=Input(from);var destination=Input(to);var evaluator=new SchemaEvaluator();
        foreach(var value in values){Check();var accepted=evaluator.Evaluate(source with{Example=value,HasExample=true},limits,ct);Check();if(accepted.Status=="Incomplete")return false;if(accepted.Status=="Invalid")continue;
            var next=evaluator.Evaluate(destination with{Example=value,HasExample=true},limits,ct);Check();if(next.Status!="Valid")return false;
        }Add("schema.enum_const");return true;
        SchemaValidationInput Input(JsonNode schema){
            var uri=new Uri("https://proof.local.invalid/normalized.json");var root=new JsonObject{["openapi"]="3.1.0",["info"]=new JsonObject{["title"]="Finite proof",["version"]="1"},["paths"]=new JsonObject(),["components"]=new JsonObject{["schemas"]=new JsonObject{["Value"]=schema.DeepClone()}}};
            var document=new ContractDocumentReader().Read(new(uri,root.ToJsonString(),"json"),limits,ct);var bundle=ContractBundleCodec.Create(uri,[document]);return new(bundle,bundle.Documents[0].Root["components"]!["schemas"]!["Value"]!,null,"request",formatMode,false,uri,"/components/schemas/Value");
        }
    }

}
