using WebApi.Contracts.Alerts;
using WebApi.Contracts.Observability;
using WebApi.Domain.Alerts;
using Xunit;
namespace WebApi.Domain.Tests;
public sealed class AlertEvaluationMachineTests
{
    private static readonly DateTimeOffset Start=new(2026,10,4,8,0,0,TimeSpan.Zero);
    private static EvaluationInput Input(int seconds,bool? condition=true,SourceState source=SourceState.Available)=>new(Start.AddSeconds(seconds),source==SourceState.Available?Start.AddSeconds(seconds):null,condition is null?null:100,condition,source);
    private static EvaluationStateSnapshot State(EvaluationPhase phase=EvaluationPhase.Inactive,DateTimeOffset? pending=null,DateTimeOffset? success=null,DateTimeOffset? suppressed=null)=>new(phase,pending,success,true,suppressed);
    [Theory][InlineData(300)][InlineData(600)][InlineData(120)] public void ContinuousExampleSecondsFiresOnlyAfterWindow(int seconds)
    {
        var first=AlertEvaluationMachine.Decide(State(),Input(0),seconds,15);Assert.Equal(EvaluationPhase.Pending,first.NewPhase);Assert.False(first.CreateEvent);
        var before=AlertEvaluationMachine.Decide(State(EvaluationPhase.Pending,Start,Start.AddSeconds(seconds-15)),Input(seconds-1),seconds,15);Assert.False(before.CreateEvent);
        var after=AlertEvaluationMachine.Decide(State(EvaluationPhase.Pending,Start,Start.AddSeconds(seconds-1)),Input(seconds),seconds,15);Assert.True(after.CreateEvent);Assert.Equal(EvaluationPhase.Firing,after.NewPhase);
    }
    [Fact] public void UnknownAnd31SecondGapResetPending()
    {
        var pending=State(EvaluationPhase.Pending,Start,Start);
        foreach(var source in new[]{SourceState.NoData,SourceState.Partial,SourceState.Stale,SourceState.Unavailable})Assert.Null(AlertEvaluationMachine.Decide(pending,Input(15,null,source),300,15).PendingSince);
        var gap=AlertEvaluationMachine.Decide(pending,Input(31),300,15);Assert.Null(gap.PendingSince);Assert.Equal(EvaluationPhase.Inactive,gap.NewPhase);
        Assert.Equal(EvaluationPhase.Pending,AlertEvaluationMachine.Decide(pending,Input(30),300,15).NewPhase);
    }
    [Fact] public void ManualSuppressionSurvivesRenameUntilFalse()
    {
        var state=State(EvaluationPhase.SuppressedUntilRecovery,success:Start,suppressed:Start);
        Assert.Equal(EvaluationPhase.SuppressedUntilRecovery,AlertEvaluationMachine.Decide(state,Input(15),0,15).NewPhase);
        Assert.Equal(EvaluationPhase.SuppressedUntilRecovery,AlertEvaluationMachine.Decide(state,Input(30,null,SourceState.Unavailable),0,15).NewPhase);
        Assert.Equal(EvaluationPhase.Inactive,AlertEvaluationMachine.Decide(state,Input(45,false),0,15).NewPhase);
    }
    [Fact] public void RecoverySampleBeforeManualResolveCannotRearm()
    {
        var state=State(EvaluationPhase.SuppressedUntilRecovery,success:Start,suppressed:Start.AddSeconds(30));
        var early=AlertEvaluationMachine.Decide(state,Input(15,false),0,15);Assert.Equal(EvaluationPhase.SuppressedUntilRecovery,early.NewPhase);
        Assert.Equal(EvaluationPhase.SuppressedUntilRecovery,AlertEvaluationMachine.Decide(state,Input(30,false),0,15).NewPhase);
    }
    [Fact] public void FiringUnknownDoesNotRecoverAndZeroWindowFiresImmediately()
    {
        var active=State(EvaluationPhase.Firing,Start,Start);var unknown=AlertEvaluationMachine.Decide(active,Input(15,null,SourceState.Partial),0,15);Assert.Equal(EvaluationPhase.Firing,unknown.NewPhase);Assert.Null(unknown.ResolveReason);Assert.False(unknown.CreateEvent);
        Assert.Equal("Recovered",AlertEvaluationMachine.Decide(active,Input(15,false),0,15).ResolveReason);
        Assert.True(AlertEvaluationMachine.Decide(State(),Input(0),0,15).CreateEvent);
    }
    [Fact] public void BackwardOrRepeatedObservationsCannotAdvancePending()
    {
        var state=State(EvaluationPhase.Pending,Start,Start.AddSeconds(15));
        var older=AlertEvaluationMachine.Decide(state,Input(10),0,15);Assert.False(older.CreateEvent);
        var repeated=AlertEvaluationMachine.Decide(state,Input(15),0,15);Assert.False(repeated.CreateEvent);
    }
    [Fact] public void RecoveryOlderThanLatestSuccessfulObservationCannotRearm()
    {
        var suppressed=State(EvaluationPhase.SuppressedUntilRecovery,success:Start.AddSeconds(30),suppressed:Start);
        var delayed=AlertEvaluationMachine.Decide(suppressed,Input(15,false),0,15);Assert.Equal(EvaluationPhase.SuppressedUntilRecovery,delayed.NewPhase);
    }
}
