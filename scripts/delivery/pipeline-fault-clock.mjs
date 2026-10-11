import assert from 'node:assert/strict';
export function pipelineAttemptTimeoutSql({attemptId,stageId,projectId}){
 for(const id of [attemptId,stageId,projectId])assert(/^[a-f0-9]{8}(?:-[a-f0-9]{4}){3}-[a-f0-9]{12}$/i.test(id??''),'Controlled timeout requires exact row identities');
 // Only Attempt owns the clock; its deadline must remain after activation.
 return `UPDATE release_pipeline_stage_attempts a SET deadline_at=a.activated_at + interval '1 microsecond' FROM release_pipeline_run_stages s WHERE a.id='${attemptId}' AND a.run_stage_id='${stageId}' AND a.project_id='${projectId}' AND s.id=a.run_stage_id AND s.project_id=a.project_id AND s.current_attempt_id=a.id AND a.status='Active' AND a.activated_at + interval '1 microsecond'<clock_timestamp() RETURNING a.id`;
}
