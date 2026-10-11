using WebApi.Contracts.Releases;
namespace WebApi.Domain.Delivery.Pipelines;
public sealed record PipelineEnvironmentFact(Guid Id,Guid ProjectId,bool Active,bool IsProduction);
public static class PipelineDefinitionRules
{
    private static readonly string[] SourceTypes=["InterfaceFunction","Integration","ContractCompatibility"];
    private static readonly string[] ProductionTypes=["EntryConnectivity","AuthenticationAuthorization","CriticalBusinessCall"];
    public static void Validate(PipelineDefinition definition,IReadOnlyList<PipelineEnvironmentFact> environments)
    {
        if(definition is null||string.IsNullOrWhiteSpace(definition.Name)||definition.Name.Trim().Length>100||definition.Description is null||definition.Stages is null||definition.Stages.Count is <2 or >8)
            throw Invalid();
        var stages=definition.Stages;
        if(stages.Any(s=>s is null)||stages.Select(s=>s.EnvironmentId).Distinct().Count()!=stages.Count)throw Invalid();
        Guid? project=null;
        for(var i=0;i<stages.Count;i++)
        {
            var stage=stages[i];
            var matches=environments.Where(e=>e.Id==stage.EnvironmentId).ToArray();
            if(stage.EnvironmentId==Guid.Empty||matches.Length!=1||!matches[0].Active||matches[0].ProjectId==Guid.Empty||stage.Order!=i+1)throw Invalid();
            var environment=matches[0];project??=environment.ProjectId;
            if(project!=environment.ProjectId||environment.IsProduction!=(i==stages.Count-1))throw Invalid();
            if(stage.EvidenceValidityMinutes is <1 or >10080||stage.WaitTimeoutMinutes is <1 or >10080||stage.RequiredTestTypes is null)throw Invalid();
            var types=stage.RequiredTestTypes;
            if(types.Count is <1 or >3||types.Distinct(StringComparer.Ordinal).Count()!=types.Count)throw Invalid();
            if(environment.IsProduction)
            {
                if(stage.ApprovalFlowId is null||stage.ApprovalFlowId==Guid.Empty||types.Count!=3||!ProductionTypes.All(types.Contains))throw Invalid();
            }
            else if(types.Any(t=>!SourceTypes.Contains(t,StringComparer.Ordinal))||stage.ApprovalFlowId==Guid.Empty||(i==0&&stage.ApprovalFlowId is not null))throw Invalid();
        }
    }
    private static ArgumentException Invalid()=>new("标准流水线需要同项目2到8个启用环境、唯一末尾生产环境及合法阶段规则。", "definition");
}
