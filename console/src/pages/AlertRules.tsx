import {useEffect,useReducer,useState} from 'react';
import {IconRefresh,IconPlus,IconArrowUpRight} from '@tabler/icons-react';
import {allowNavigation,Link,navigate,useWorkspace} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {usePaged,useRemote,Feedback,Pagination,Editor} from '../ui';
import {loadRule,toggleRule,type AlertRuleDto,type SaveAlertRuleRequest} from '../api/alerts';
import {observationQuery} from '../api/observability';
import {isRequestedRule,RuleEditor,ruleMetricNames} from '../observability/RuleEditor';
import {FreshObservation,AlertBadge} from './AlertCenter';
import {notificationLabel} from '../observability/alert-state.mjs';

type RuleDialog = {kind:'edit';id:string} | {kind:'create';initial:SaveAlertRuleRequest} | {kind:'toggle';rule:AlertRuleDto};

function ExistingRuleEditor({id,onClose,onSaved}:{id:string;onClose:()=>void;onSaved:(rule:AlertRuleDto)=>void}) {
 const detail=useRemote<AlertRuleDto>('/observability/alert-rules/'+id);
 if(detail.error)return <div className="error" role="alert">{detail.errorStatus===403||detail.errorStatus===404?'规则或权限已失效，请刷新后重试。':detail.error}</div>;
 if(!detail.data)return null;
 if(!isRequestedRule(id,detail.data))return <div className="error" role="alert">无法核实所选规则的身份和修订，请重新打开。</div>;
 return <RuleEditor rule={detail.data} onClose={onClose} onSaved={onSaved}/>;
}

export function AlertRules({search}:{search:string}){
 const ws=useWorkspace(),session=useSession(),params=new URLSearchParams(search),all=params.get('all')==='true';
 const allowed=!!ws.projectId&&session.user?.permissions.includes('alert.rule.manage'),[saved,setSaved]=useState('');
 // A fresh key also distinguishes two explicit opens of the same rule. List refreshes keep it.
 const [{dialog,sequence},select]=useReducer(
  (previous:{dialog?:RuleDialog;sequence:number},dialog:RuleDialog|undefined)=>({dialog,sequence:previous.sequence+1}),
  {sequence:0}
 );
 const scope={organizationId:ws.organizationId,projectId:ws.projectId,environmentId:all?null:ws.environmentId,allAccessibleEnvironments:all},remote=usePaged(allowed?'/observability/alert-rules?'+observationQuery(scope,{start:'',end:''},{}):null,15_000);
 const create=():SaveAlertRuleRequest=>({organizationId:ws.organizationId,projectId:ws.projectId,environmentId:ws.environmentId||null,name:'',metric:'latency_p95_ms',expression:'latency_p95_ms > 800',severity:'Warning',enabled:true,forSeconds:300,targetType:'Environment',targetId:null,windowSeconds:60,notification:{inConsole:true,requestedChannels:[],externalEnabled:false,emailRecipients:[],notifyRecovery:true,retryPolicy:{maxAttempts:5,baseDelaySeconds:30,maxDelaySeconds:900,expiresAfterMinutes:1440}}});
 useEffect(()=>{
  const id=params.get('edit'),api=params.get('api');
  if(id&&/^[0-9a-f-]{36}$/i.test(id)&&allowed)select({kind:'edit',id});
  else if(params.get('create')==='true'&&allowed){
   const initial=create();
   if(api&&/^[0-9a-f-]{36}$/i.test(api))Object.assign(initial,{targetType:'Api',targetId:api,name:'API 延迟告警'});
   select({kind:'create',initial});
  }else select(undefined);
 },[search,allowed,ws.organizationId,ws.projectId,ws.environmentId]);
 function choose(next:RuleDialog){
  if(!allowNavigation())return;
  if(next.kind==='create')setSaved('');
  select(next);
 }
 function close(){select(undefined);if(params.has('create')||params.has('edit'))navigate('/observability/alert-rules'+(all?'?all=true':''),true);}
 function onSaved(r:AlertRuleDto){setSaved(`规则“${r.definition.name}”已保存 · 修订 ${r.revision}，逻辑 ${r.logicRevision}。`);remote.reload();close();}
 const toggle=dialog?.kind==='toggle'?dialog.rule:undefined;
 return <><div className="breadcrumb"><Link to="/observability/metrics">运行监控</Link> / 告警规则</div><div className="page-head"><div><div className="ob-eyebrow">OBSERVABILITY · RULE GOVERNANCE</div><h1>告警规则</h1><p>配置有限指标比较、持续时间和动态环境范围</p></div><div className="actions">{session.user?.permissions.includes('alert.read')&&<Link to="/observability/alerts">告警中心 <IconArrowUpRight size={14}/></Link>}<button className="btn" disabled={!allowed||remote.loading} onClick={remote.reload}><IconRefresh size={15}/>刷新</button><button className="btn primary" disabled={!session.can('alert.rule.manage',ws.scope,true)||!ws.environmentId} onClick={()=>choose({kind:'create',initial:create()})}><IconPlus size={15}/>新建规则</button></div></div>
 <div className="ob-toolbar"><label>显示范围<select aria-label="规则显示范围" value={all?'all':'environment'} onChange={e=>navigate('/observability/alert-rules'+(e.target.value==='all'?'?all=true':''))}><option value="environment">当前环境及覆盖当前环境的规则</option><option value="all">全部可访问环境</option></select></label><small>仅显示具有完整规则管理权限的定义 · 每15秒更新</small></div><div className="notice">规则采用自己的查询窗口。未知或过期数据不会被当成恢复；修改逻辑或禁用会结束旧逻辑事件。邮件与 Webhook 需平台渠道及规则显式开启；原渠道意向默认不发送。企业 IM 保留意向，待接入。</div>{allowed&&dialog?.kind==='edit'&&<ExistingRuleEditor key={sequence} id={dialog.id} onClose={close} onSaved={onSaved}/>}{saved&&<div className="notice" role="status">{saved}</div>}
 {!allowed?<div className="error">缺少 alert.rule.manage 权限，规则定义已清除。</div>:<section className="card"><Feedback {...remote}/><div className="table-wrap"><table><thead><tr><th>规则 / 级别</th><th>比较条件</th><th>范围 / 目标</th><th>窗口 / 持续</th><th>评估与最近有效观测</th><th>状态 / 修订</th><th>操作</th></tr></thead><tbody>{remote.data?.items.map((r:AlertRuleDto)=>{const d=r.definition,can=session.can('alert.rule.manage',{organizationId:d.organizationId,projectId:d.projectId,environmentId:d.environmentId},true);return <tr key={r.id}><td><button className="text-btn alert-rule-link" onClick={()=>choose({kind:'edit',id:r.id})}>{d.name}</button><small className="block"><AlertBadge value={d.severity} severity/></small></td><td><span className="mono">{d.expression}</span><small className="block muted">{ruleMetricNames[d.metric]}</small></td><td>{d.environmentId?ws.tree.environments.find(e=>e.id===d.environmentId)?.name||'指定环境':d.projectId?'项目活动环境':'组织活动环境'}<small className="block muted">{d.targetType==='Environment'?'环境聚合':d.targetType==='Api'?'指定 API':'指定后端'}</small></td><td>{d.windowSeconds}s / {d.forSeconds}s<small className="block muted">{d.notification.requestedChannels.length?d.notification.requestedChannels.map(notificationLabel).join('、'):'控制台'}</small></td><td><small className={'block '+(d.notification.externalEnabled?'':'muted')}>{d.notification.externalEnabled?'外部策略已开启':'外部意向保留 · 未启用'}</small><FreshObservation value={r.lastSuccessAt} state={r.evaluationState}/></td><td><span className={'badge '+(d.enabled?'green':'red')}>{d.enabled?'启用':'禁用'}</span><small className="block muted">修订 {r.revision} · 逻辑 {r.logicRevision}</small></td><td><div className="actions"><button className="text-btn" disabled={!can} onClick={()=>choose({kind:'edit',id:r.id})}>编辑</button><button className="text-btn" disabled={!can} onClick={()=>choose({kind:'toggle',rule:r})}>{d.enabled?'禁用':'启用'}</button></div></td></tr>;})}</tbody></table>{!remote.loading&&!remote.error&&!remote.data?.items.length&&<div className="empty">当前范围暂无可管理规则</div>}</div><Pagination remote={remote}/></section>}
 {allowed&&dialog?.kind==='create'&&<RuleEditor key={sequence} initial={dialog.initial} onClose={close} onSaved={onSaved}/>}
 {toggle&&<Editor key={sequence} title={toggle.definition.enabled?'禁用告警规则':'启用告警规则'} fields={[]} initial={{}} help={toggle.definition.enabled?'禁用将结束当前逻辑的活动事件并撤销评估租约。':'启用会创建新的逻辑修订，重新开始有效观测。'} close={()=>select(undefined)} loadLatest={()=>loadRule(toggle.id)} save={async(_,key,fresh)=>{const r=await toggleRule({...toggle,revision:fresh?.revision??toggle.revision},key);setSaved(`规则“${r.definition.name}”已${r.definition.enabled?'启用':'禁用'}。`);remote.reload();}}/>}</>;
}
