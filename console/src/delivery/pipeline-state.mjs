import {deliveryAuthorityKey,sourceTestTypes,productionTestTypes} from './delivery-state.mjs';
export function validatePipelineDefinition(d,environments){
 const errors=[];if(!d?.name?.trim()||d.name.trim().length>100)errors.push('名称需为 1 至 100 个字符。');
 const stages=d?.stages;if(!Array.isArray(stages)||stages.length<2||stages.length>8)return [...errors,'请选择 2 至 8 个阶段。'];
 const ids=new Set(),projects=new Set();
 stages.forEach((s,i)=>{const e=environments.find(e=>e.id===s.environmentId);if(!e?.active)errors.push(`阶段 ${i+1} 环境不存在或未启用。`);else{projects.add(e.projectId);if(e.isProduction!==(i===stages.length-1))errors.push('仅最后一个阶段可为生产环境，且必须是生产环境。');}
 if(ids.has(s.environmentId))errors.push('环境不能重复。');ids.add(s.environmentId);if(s.order!==i+1)errors.push('阶段顺序必须连续。');
 for(const field of ['evidenceValidityMinutes','waitTimeoutMinutes'])if(!Number.isInteger(s[field])||s[field]<1||s[field]>10080)errors.push(`阶段 ${i+1} 时限需为 1 至 10080 分钟。`);
 const types=s.requiredTestTypes||[],allowed=(e?.isProduction?productionTestTypes:sourceTestTypes).map(t=>t.value);
 if(types.length<1||types.length>3||new Set(types).size!==types.length||types.some(t=>!allowed.includes(t))||e?.isProduction&&allowed.some(t=>!types.includes(t)))errors.push(`阶段 ${i+1} 必需验证项不完整或类型不合法。`);
 if(e?.isProduction&&!s.approvalFlowId||i===0&&s.approvalFlowId)errors.push('生产须选择当前审批模板，来源首阶段无需审批。');
 });if(projects.size!==1)errors.push('所有环境须属于同一项目。');return [...new Set(errors)];
}
export function pipelineAuthorityKey(actor,resourceId,attemptId,profileHash,epoch){return JSON.stringify([deliveryAuthorityKey(actor),resourceId,attemptId,profileHash,epoch]);}
export function reconcilePipelineDraft(draft,latest,status){return [401,403,404].includes(status)?{draft:undefined,latest:undefined,conflict:false}:{draft,latest,conflict:[409,412].includes(status)};}
export function pipelineCanManage(actor,scope){return !!scope&&['pipeline.manage','project.write'].every(p=>actor?.permissions?.includes(p))&&actor?.scopes?.some(g=>g.accessMode==='read_write'&&g.scope.organizationId===scope.organizationId&&(!g.scope.projectId||g.scope.projectId===scope.projectId)&&!g.scope.environmentId)===true;}
export function newPipelineStage(environmentId,order,environments){const e=environments.find(e=>e.id===environmentId);return {order,environmentId,requiredTestTypes:(e?.isProduction?productionTestTypes:sourceTestTypes).map(t=>t.value),evidenceValidityMinutes:1440,waitTimeoutMinutes:1440,approvalFlowId:null};}
export function pipelineStageActions(stage,actor){
 if(!stage||['Completed','Cancelled','Invalidated'].includes(stage.runStatus))return [];
 const e=stage.eligibility||{},a=[],has=code=>actor?.permissions?.includes(code),active=stage.runStatus==='Active',pipeline=has('pipeline.run');
 if(active){
 if(e.canPrepare&&pipeline&&has('release.create'))a.push('prepare-promotion');
 if(e.canMaterialize&&!stage.profile?.isProduction&&pipeline&&has('release.create'))a.push('materialize-artifact');
 if(!stage.profile?.isProduction&&has('release.test.record')){if(e.canRecord)a.push('record-verification');if(e.canRequestAcceptance)a.push('request-acceptance');}
 if(e.canVerifyProduction&&stage.profile?.isProduction&&has('release.verify'))a.push('verify-production');
 const p=stage.promotion;if(p&&pipeline){if(p.eligibility?.canEditMapping&&has('release.create'))a.push('mapping','precheck');if(p.eligibility?.canSubmit&&p.precheck?.canSubmit&&has('release.create'))a.push('submit');if(p.eligibility?.canPublish&&has('release.publish'))a.push('publish');}
 if(stage.formal?.approvalEligibility?.canAct&&has('approval.act'))a.push('approve');
 if(stage.runEligibility?.canPause&&pipeline)a.push('pause-run');
 }
 if(stage.runEligibility?.canResume&&pipeline)a.push('resume-run');
 if(e.canReopen&&stage.actualReleaseStatus!=='Publishing'&&pipeline&&has('release.create'))a.push('reopen-stage');
 if((e.canCancelRun||stage.runEligibility?.canCancel)&&pipeline)a.push('cancel-run');return a;
}
export function acceptPipelineResponse(expectedKey,currentKey,response){return !!expectedKey&&expectedKey===currentKey&&response!=null;}
export function pipelineStatusLabel(status){return ({Active:'运行中',Paused:'已暂停 · 人工处置',TimedOut:'阶段等待超时 · 需重开',Invalidated:'条件失效',Cancelled:'已取消（已下发事实保留）',Completed:'历史运行完成 · 当前节点另行核对',Pending:'等待前序阶段',AwaitingMapping:'待目标映射',AwaitingPrecheck:'待发布预检',AwaitingApproval:'待独立审批',ReadyToDeploy:'待人工发布',Deploying:'正在下发 · 等待节点确认',AwaitingEvidence:'待登记本阶段测试',AwaitingVerification:'待独立生产验证',AwaitingAcceptance:'待独立测试验收',Passed:'阶段已通过',DeployFailed:'下发失败',VerificationFailed:'验证失败'})[status]||status||'状态不可用';}
export function pipelineCountLabel(counts,key){return counts&&Number.isFinite(counts[key])&&counts[key]>=0?counts[key]:'受限';}
export function pipelineReturnTo(search){const path=new URLSearchParams(search||'').get('returnTo');return path&&!/[\\\x00-\x1f#]/.test(path)&&(/\/\//.test(path)===false)&&/^\/(?:approvals|releases\/[a-zA-Z0-9-]+|delivery\/(?:pipeline-runs|pipeline-stages|pipelines|artifacts)(?:\/[a-zA-Z0-9-]+)?)(?:\?[^#]*)?$/.test(path)?path:undefined;}
export function pipelineTraceUrl(kind,id,returnTo){return '/delivery/'+kind+'/'+encodeURIComponent(id)+(returnTo?'?returnTo='+encodeURIComponent(returnTo):'');}
export function pipelineStageKey(stage,actor,epoch=0){return pipelineAuthorityKey(actor,stage.id,stage.currentAttemptId,stage.profileHash,epoch);}
export function pipelineVerificationCommand(owner,evidence){return owner.stageId?{path:`/release-pipeline-run-stages/${owner.stageId}/verifications`,body:{expectedContextHash:owner.contextHash,evidence}}:{path:owner.promotionId?`/release-promotions/${owner.promotionId}/verifications`:`/release-artifacts/${owner.artifactId}/verifications`,body:{...evidence,...(owner.promotionId?{expectedContextHash:owner.contextHash}:{})}};}
export function pipelineSummaryKey(s){return s?JSON.stringify([s.id,s.currentAttemptId,s.profileHash,s.revision,s.runStatus,s.actualReleaseId,s.actualConfigVersion,s.actualDeploymentSequence,s.actualReleaseStatus]):'';}
