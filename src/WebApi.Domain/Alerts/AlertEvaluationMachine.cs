using WebApi.Contracts.Alerts;
using WebApi.Contracts.Observability;
namespace WebApi.Domain.Alerts;
public static class AlertEvaluationMachine
{
    public static EvaluationDecision Decide(EvaluationStateSnapshot state,EvaluationInput input,int forSeconds,int intervalSeconds)
    {
        if(forSeconds is <0 or >86400||intervalSeconds is <1 or >60)throw new ArgumentException("Invalid evaluation interval or duration.");
        var valid=input.SourceState==SourceState.Available&&input.ObservedAt is not null&&input.ObservedAt<=input.Slot&&input.Value is double value&&double.IsFinite(value)&&input.Condition is not null;
        if(!valid)return new(state.Phase==EvaluationPhase.Pending?EvaluationPhase.Inactive:state.Phase,null,null,false,null);
        if(state.LastSuccessAt is not null&&input.ObservedAt<=state.LastSuccessAt)
            return new(state.Phase,state.PendingSince,state.LastCondition,false,null);
        if(state.Phase==EvaluationPhase.SuppressedUntilRecovery)
        {
            var recovered=input.Condition==false&&state.SuppressedAt is not null&&input.ObservedAt>state.SuppressedAt;
            return new(recovered?EvaluationPhase.Inactive:state.Phase,null,input.Condition,false,null);
        }
        if(input.Condition==false)return new(EvaluationPhase.Inactive,null,false,false,state.Phase==EvaluationPhase.Firing?"Recovered":null);
        if(state.Phase==EvaluationPhase.Firing)return new(EvaluationPhase.Firing,state.PendingSince,true,false,null);
        if(state.Phase==EvaluationPhase.Pending&&(state.LastSuccessAt is null||input.ObservedAt-state.LastSuccessAt>TimeSpan.FromSeconds(intervalSeconds*2)))
            return new(EvaluationPhase.Inactive,null,true,false,null);
        var since=state.Phase==EvaluationPhase.Pending&&state.PendingSince is not null?state.PendingSince:input.ObservedAt;
        var fire=(input.ObservedAt-since)!.Value.TotalSeconds>=forSeconds;
        return new(fire?EvaluationPhase.Firing:EvaluationPhase.Pending,since,true,fire,null);
    }
}
