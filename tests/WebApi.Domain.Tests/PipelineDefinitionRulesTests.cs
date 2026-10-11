using WebApi.Contracts.Releases;
using WebApi.Domain.Delivery.Pipelines;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class PipelineDefinitionRulesTests
{
    private static (PipelineDefinition Definition, PipelineEnvironmentFact[] Environments) Definition(int count = 2)
    {
        var project = Guid.NewGuid();
        var environments = Enumerable.Range(0,count).Select(i => new PipelineEnvironmentFact(Guid.NewGuid(),project,true,i==count-1)).ToArray();
        var stages = environments.Select((e,i) => new PipelineStageDefinition(i+1,e.Id,e.IsProduction
            ? ["EntryConnectivity","AuthenticationAuthorization","CriticalBusinessCall"]
            : ["InterfaceFunction","Integration","ContractCompatibility"], ApprovalFlowId:e.IsProduction?Guid.NewGuid():null)).ToArray();
        return (new("标准交付","",stages),environments);
    }
    [Theory][InlineData(2)][InlineData(4)][InlineData(8)] public void ValidLinearChainsAreAccepted(int count)
    { var d=Definition(count); PipelineDefinitionRules.Validate(d.Definition,d.Environments); }
    [Theory][InlineData(1)][InlineData(9)] public void InvalidChainLengthRejected(int count)
    { var d=Definition(count); Assert.Throws<ArgumentException>(()=>PipelineDefinitionRules.Validate(d.Definition,d.Environments)); }
    [Fact] public void ProductionCannotDropMandatoryChecks()
    { var d=Definition(); var stages=d.Definition.Stages.ToArray();stages[^1]=stages[^1] with{RequiredTestTypes=["EntryConnectivity"]};Assert.Throws<ArgumentException>(()=>PipelineDefinitionRules.Validate(d.Definition with{Stages=stages},d.Environments)); }
    [Theory][InlineData("duplicate")][InlineData("foreign")][InlineData("disabled")][InlineData("missing")][InlineData("first_production")][InlineData("two_production")][InlineData("no_production")][InlineData("wrong_order")][InlineData("source_approval")][InlineData("production_without_approval")]
    public void InvalidEnvironmentFactsAndStageSequenceRejected(string reason)
    {
        var d=Definition(4);var stages=d.Definition.Stages.ToArray();var facts=d.Environments.ToArray();
        switch(reason){
            case "duplicate":stages[1]=stages[1] with{EnvironmentId=stages[0].EnvironmentId};break;
            case "foreign":facts[1]=facts[1] with{ProjectId=Guid.NewGuid()};break;
            case "disabled":facts[1]=facts[1] with{Active=false};break;
            case "missing":facts=facts.Skip(1).ToArray();break;
            case "first_production":facts[0]=facts[0] with{IsProduction=true};break;
            case "two_production":facts[1]=facts[1] with{IsProduction=true};break;
            case "no_production":facts[^1]=facts[^1] with{IsProduction=false};break;
            case "wrong_order":stages[1]=stages[1] with{Order=3};break;
            case "source_approval":stages[0]=stages[0] with{ApprovalFlowId=Guid.NewGuid()};break;
            case "production_without_approval":stages[^1]=stages[^1] with{ApprovalFlowId=null};break;
        }
        Assert.Throws<ArgumentException>(()=>PipelineDefinitionRules.Validate(d.Definition with{Stages=stages},facts));
    }
    [Theory][InlineData(1)][InlineData(1440)][InlineData(10080)] public void TimeBoundsAccepted(int minutes)
    {var d=Definition();PipelineDefinitionRules.Validate(d.Definition with{Stages=d.Definition.Stages.Select(s=>s with{EvidenceValidityMinutes=minutes,WaitTimeoutMinutes=minutes}).ToArray()},d.Environments);}
    [Theory][InlineData(0,true)][InlineData(10081,true)][InlineData(0,false)][InlineData(10081,false)] public void InvalidTimeBoundsRejected(int minutes,bool evidence)
    {var d=Definition();var stages=d.Definition.Stages.ToArray();stages[0]=evidence?stages[0] with{EvidenceValidityMinutes=minutes}:stages[0] with{WaitTimeoutMinutes=minutes};Assert.Throws<ArgumentException>(()=>PipelineDefinitionRules.Validate(d.Definition with{Stages=stages},d.Environments));}
    [Theory][InlineData("empty")][InlineData("duplicate")][InlineData("production_type")][InlineData("unknown")]
    public void InvalidNonProductionTypesRejected(string reason)
    {var d=Definition();string[] types=reason switch{"empty"=>[],"duplicate"=>["InterfaceFunction","InterfaceFunction"],"production_type"=>["EntryConnectivity"],_=>["Script"]};var stages=d.Definition.Stages.ToArray();stages[0]=stages[0] with{RequiredTestTypes=types};Assert.Throws<ArgumentException>(()=>PipelineDefinitionRules.Validate(d.Definition with{Stages=stages},d.Environments));}
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)] public void NonProductionCanChooseOneToThreeTypes(int count)
    {var d=Definition();var stages=d.Definition.Stages.ToArray();stages[0]=stages[0] with{RequiredTestTypes=stages[0].RequiredTestTypes.Take(count).ToArray()};PipelineDefinitionRules.Validate(d.Definition with{Stages=stages},d.Environments);}
    [Theory][InlineData(0)][InlineData(101)] public void InvalidNameRejected(int count)
    {var d=Definition();Assert.Throws<ArgumentException>(()=>PipelineDefinitionRules.Validate(d.Definition with{Name=new string('x',count)},d.Environments));}
    [Theory][InlineData(1)][InlineData(100)] public void ValidNameAccepted(int count)
    {var d=Definition();PipelineDefinitionRules.Validate(d.Definition with{Name=new string('x',count)},d.Environments);}
    [Fact] public void EmptyEnvironmentIdentifierRejected()
    {var d=Definition();var stages=d.Definition.Stages.ToArray();stages[0]=stages[0] with{EnvironmentId=Guid.Empty};Assert.Throws<ArgumentException>(()=>PipelineDefinitionRules.Validate(d.Definition with{Stages=stages},d.Environments));}
}
