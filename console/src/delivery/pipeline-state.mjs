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
