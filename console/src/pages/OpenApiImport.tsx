import {ActiveImportPreviews} from '../contracts/ActiveImportPreviews';
import {useEffect,useRef,useState} from 'react';import {useWorkspace,Link} from '../Shell';import {useSession} from '../auth/SessionProvider';import {apiRequest} from '../api/client';import {Editor,Table,useRemote,useUnsaved,type Field} from '../ui';import {ImportSourceInput} from '../contracts/ImportSourceInput';import {ImportOperationMapping} from '../contracts/ImportOperationMapping';import {ImportSourcePolicyEditor} from '../contracts/ImportSourcePolicyEditor';import {ContractIssues} from '../contracts/ExampleValidation';import {importIdentity,invalidatePreview,suspendImportPreview,canCommitPreview,mapImportTarget,previewRequest,acceptImportPreview,recoverImportFailure,importPolicyScope,selectImportPolicy,acceptImportPolicySave} from '../contracts/import-state.mjs';
export function OpenApiImport(){
 const ws=useWorkspace(),session=useSession(),[source,setSource]=useState<any>({mode:'text',text:'',url:'',files:[],format:'auto'}),[cluster,setCluster]=useState(''),[state,setState]=useState<any>({targets:[],authorized:true}),[edit,setEdit]=useState<any>(),[error,setError]=useState(''),[clock,setClock]=useState(Date.now()),[policyOverride,setPolicyOverride]=useState<any>();const mounted=useRef(true),key=useRef(crypto.randomUUID()),sequence=useRef(0),controller=useRef<AbortController|undefined>(undefined),rawRoot=useRef<HTMLDivElement>(null);
 const projectScope=ws.organizationId&&ws.projectId?{organizationId:ws.organizationId,projectId:ws.projectId,environmentId:null}:undefined,canReadPolicy=!!projectScope&&session.can('project.read',projectScope),canWritePolicy=canReadPolicy&&session.can('project.write',projectScope,true),policyRemote=useRemote<any>(canReadPolicy?`/projects/${ws.projectId}/import-source-policy`:null),clusters=useRemote<any>(ws.environmentId?`/environments/${ws.environmentId}/clusters`:null,0,true),activeClusters=(clusters.data?.items??[]).filter((c:any)=>c.status==='Active'),clusterId=activeClusters.some((c:any)=>c.id===cluster)?cluster:activeClusters[0]?.id??'';
 const [previewRevision,setPreviewRevision]=useState(0),[revoking,setRevoking]=useState(false),[revokeError,setRevokeError]=useState('');
 const canManagePreviews=!!projectScope&&session.can('api.create',projectScope,true);
 const refreshPreviews=()=>{if(mounted.current)setPreviewRevision(revision=>revision+1);};
 const policyScope=importPolicyScope(projectScope),policyVisit=useRef({scope:policyScope,generation:0});
 if(policyVisit.current.scope!==policyScope)policyVisit.current={scope:policyScope,generation:policyVisit.current.generation+1};
 const capturedPolicyVisit=policyVisit.current;
 const policy=selectImportPolicy(projectScope,policyRemote.data,policyOverride),authority=JSON.stringify([session.user?.id,session.user?.permissions,session.user?.scopes,session.error]),input={...source,projectId:ws.projectId,environmentId:ws.environmentId,clusterId,authority,policyRevision:policy?.revision},identity=importIdentity(input),currentInput=useRef(input);currentInput.current=input;
 const allowed=!!ws.environmentId&&session.can('api.create',ws.scope,true)&&session.can('api.create',projectScope,true)&&session.can('route.write',ws.scope,true),view={...invalidatePreview(state,input),authorized:allowed&&state.authorized!==false},busy=!!view.busy||revoking,preview=view.preview,result=view.result,policyBlocked=canReadPolicy&&(policyRemote.loading||!!policyRemote.error),blocked=!allowed||!clusterId||clusters.loading||!!clusters.error||policyBlocked||session.loading;
 useEffect(()=>{setPolicyOverride(undefined);},[policyScope]);
 useEffect(()=>{sequence.current++;controller.current?.abort();setState((old:any)=>({...invalidatePreview(old,currentInput.current),busy:false}));setEdit(undefined);key.current=crypto.randomUUID();},[identity]);
 useEffect(()=>{mounted.current=true;const timer=setInterval(()=>setClock(Date.now()),1000);return()=>{mounted.current=false;clearInterval(timer);sequence.current++;controller.current?.abort();};},[]);
 useUnsaved(!!(source.text||source.url||source.files.length||view.targets.length)&&!result,()=>setError('导入来源或映射尚未提交，输入已保留；请完成导入或清空草稿后离开。'));
 const isCurrent=(captured:any,token:number)=>sequence.current===token&&importIdentity(captured)===importIdentity(currentInput.current);
 function handleFailure(error:any){
  setState((old:any)=>recoverImportFailure(old,error));
  if(error?.code==='import_preview_changed'&&canReadPolicy)policyRemote.reload();
 }
 async function inspect(){
  if(blocked||busy)return;const captured=input;const token=++sequence.current,request=new AbortController();controller.current?.abort();controller.current=request;const deadline=setTimeout(()=>request.abort(),30000);setState((old:any)=>({...suspendImportPreview(invalidatePreview(old,captured)),input:captured,identity,result:undefined,issues:[],busy:true,requestSequence:token,authorized:true,recovery:''}));setError('');
  try{const body=previewRequest(captured,policy?.limits);const value=await apiRequest<any>('/openapi/import-previews',{method:'POST',body,signal:request.signal});if(isCurrent(captured,token)&&!request.signal.aborted){
    setState((old:any)=>acceptImportPreview({...old,input:currentInput.current,requestSequence:sequence.current},captured,value,token));
    key.current=crypto.randomUUID();
    if(canReadPolicy&&value.sourcePolicyRevision!==captured.policyRevision)policyRemote.reload();
   }}
  catch(e){if(isCurrent(captured,token)){setError(request.signal.aborted?'预览等待已取消或超过 30 秒；输入已保留。':(e as Error).message);handleFailure(e);}}
  finally{clearTimeout(deadline);refreshPreviews();if(isCurrent(captured,token))setState((old:any)=>({...old,busy:false}));}
 }
 async function commit(){
  const current={...invalidatePreview(state,currentInput.current),authorized:allowed&&state.authorized!==false};if(busy||blocked||!canCommitPreview(current,Date.now()))return;const captured=input,token=++sequence.current,request=new AbortController();controller.current=request;const deadline=setTimeout(()=>request.abort(),30000);setState((old:any)=>({...old,busy:true,issues:[],recovery:''}));setError('');
  try{const value=await apiRequest<any>(`/openapi/import-previews/${current.preview.previewId}/commit`,{method:'POST',body:{expectedBundleHash:current.preview.bundleHash,targets:current.targets},idempotencyKey:key.current,signal:request.signal});if(isCurrent(captured,token)&&!request.signal.aborted)setState((old:any)=>({...old,result:value,preview:undefined,targets:[],issues:[],busy:false}));}
  catch(e){if(isCurrent(captured,token)){setError(request.signal.aborted?'提交等待超过 30 秒，服务端可能已完成。输入与提交回执标识已保留；请原样重试核对结果。':(e as Error).message);handleFailure(e);}}
  finally{clearTimeout(deadline);refreshPreviews();if(isCurrent(captured,token))setState((old:any)=>({...old,busy:false}));}
 }
 async function revokePreview(id:string){
  if(busy||!canManagePreviews)return;
  setRevoking(true);setRevokeError('');
  try{
   await apiRequest(`/openapi/import-previews/${id}`,{method:'DELETE',isCurrentRead:()=>mounted.current});
   if(!mounted.current)return;
   setState((old:any)=>old.preview?.previewId===id?suspendImportPreview(old):old);
  }catch(e){
   if(!mounted.current)return;
   setRevokeError((e as Error).message);
   setState((old:any)=>old.preview?.previewId===id?recoverImportFailure(old,e):old);
  }finally{if(mounted.current){setRevoking(false);refreshPreviews();}}
 }
 function changeTargets(operationId:string,update:(targets:any[])=>any[]){
  setState((old:any)=>({...old,targets:update(old.targets),revisionReview:(old.revisionReview??[]).filter((id:string)=>id!==operationId)}));
  key.current=crypto.randomUUID();
 }
 function toggleMapping(operation:any,checked:boolean){
  const id=operation.operationId;
  if(checked){
   const target=mapImportTarget(operation,{newApiCode:operation.suggestedCode,newApiName:operation.summary||id,version:'1.0.0'});
   changeTargets(id,targets=>[...targets,target]);
  }else changeTargets(id,targets=>targets.filter(target=>target.operationId!==id));
 }
 function editMapping(operation:any){
  const target=view.targets.find((item:any)=>item.operationId===operation.operationId);
  const revisions=view.revisionReview?.includes(operation.operationId)?{expectedRevision:'',expectedRouteRevision:''}:{};
  setEdit({...target,...revisions,targetMode:target.existingVersionId?'version':target.apiId?'api':'new'});
 }
 async function saveMapping(value:Record<string,any>){
  const operation=preview.operations.find((item:any)=>item.operationId===edit.operationId);
  const modeFields=value.targetMode==='new'?{apiId:null,existingVersionId:null,existingRouteId:null}
   :value.targetMode==='api'?{existingVersionId:null,existingRouteId:null}:{apiId:null,newApiCode:null,newApiName:null};
  const target=mapImportTarget(operation,{...value,...modeFields});
  changeTargets(target.operationId,targets=>targets.map(item=>item.operationId===target.operationId?target:item));
 }
 const fields:Field[]=[{key:'targetMode',label:'目标模式',required:true,options:[{value:'new',label:'新 API / 新版本'},{value:'api',label:'已有 API / 新版本'},{value:'version',label:'覆盖已有草稿版本'}]},{key:'newApiCode',label:'新 API 编码'},{key:'newApiName',label:'新 API 名称'},{key:'apiId',label:'已有 API ID',help:'仅已有 API 新版本模式使用；覆盖草稿只填写版本 ID。'},{key:'existingVersionId',label:'覆盖草稿版本 ID',help:'服务端核对目标项目、版本归属、封存状态和权限。'},{key:'version',label:'新版本号',required:true},{key:'expectedRevision',label:'覆盖版本最新修订',type:'number'},{key:'existingRouteId',label:'覆盖路由 ID',help:'仅覆盖草稿模式可用；路由须属于该版本和当前环境。'},{key:'expectedRouteRevision',label:'覆盖路由最新修订',type:'number'}];
 const selectDiagnostic=(pointer:string)=>{const textarea=rawRoot.current?.querySelector<HTMLTextAreaElement>('textarea');textarea?.focus();const issue=view.issues?.find((item:any)=>item.pointer===pointer);if(textarea&&issue?.line){const lines=textarea.value.split('\n'),at=lines.slice(0,issue.line-1).reduce((n:number,line:string)=>n+line.length+1,0)+(issue.column??1)-1;textarea.setSelectionRange(at,at);}else if(pointer)setError('受影响位置：'+pointer+'；外部引用请在对应附带文件中修复，原文保留。');};
 return <><div className="breadcrumb">API 管理 / OpenAPI 导入</div><div className="page-head"><div><h1>OpenAPI 导入</h1><p>固定来源预览、Operation 映射、事务整批提交</p></div></div><div className="notice">支持 OpenAPI 3.0 / 3.1、JSON / YAML 和有界引用包。导入写入工作配置；每个 Operation 对应 API Version，网关仍需原审批发布流程。</div>{canReadPolicy&&<ImportSourcePolicyEditor key={policyScope} policy={policy} allowed={canWritePolicy} loading={policyRemote.loading} error={policyRemote.error} loadLatest={()=>apiRequest(`/projects/${ws.projectId}/import-source-policy`)} save={(body,key,revision)=>apiRequest(`/projects/${ws.projectId}/import-source-policy`,{method:'PUT',body,etag:`"${revision}"`,idempotencyKey:key})} onSaved={value=>{const saved=acceptImportPolicySave(policyVisit.current,capturedPolicyVisit,value);if(saved){setPolicyOverride(saved);policyRemote.reload();}}}/>}<div className="card details" ref={rawRoot}><label>目标后端集群<select aria-label="导入目标集群" disabled={busy||clusters.loading} value={clusterId} onChange={e=>setCluster(e.target.value)}><option value="">请选择有效集群</option>{activeClusters.map((c:any)=><option key={c.id} value={c.id}>{c.name}</option>)}</select></label>{clusters.error&&<p className="error">{clusters.error}</p>}<ImportSourceInput value={source} onChange={setSource} disabled={busy} onError={setError} budget={policy?.limits}/><div className="actions"><button type="button" className="btn primary" disabled={blocked||busy||view.authorized===false} onClick={()=>void inspect()}>{busy?'处理中…':'解析并固定预览'}</button>{view.busy&&!preview&&<button type="button" className="btn" onClick={()=>{sequence.current++;controller.current?.abort();setState((old:any)=>({...old,busy:false}));setError('已取消等待。可刷新本项目中我的有效预览，核对服务端是否已创建。');}}>取消预览等待</button>}<button type="button" className="btn" disabled={busy} onClick={()=>{setSource({mode:'text',text:'',url:'',files:[],format:'auto'});setState({targets:[],authorized:true});setError('');}}>清空导入草稿</button><Link to="/clusters">配置后端</Link></div>{!allowed&&<p className="notice">请选定有创建 API 与写入路由权限的项目和环境。</p>}{error&&<div role="alert" className="error">{error} 输入原文已保留。</div>}{view.recovery&&<p className="notice">{view.recovery}</p>}{view.authorized===false&&<button type="button" className="btn" onClick={async()=>{try{await session.refresh(true);clusters.reload();policyRemote.reload();setState((old:any)=>({...old,authorized:true}));}catch(e){setError((e as Error).message);}}}>刷新会话与目标范围</button>}{view.issues?.length>0&&<ContractIssues issues={view.issues} onSelect={selectDiagnostic}/>}</div>{preview&&<div className="card"><h3 className="section-title">固定预览 · {view.targets.length} / {preview.operations.length} 个 Operation 已选</h3><div className="import-preview-meta"><span>方言 <strong>{preview.dialect}</strong> · {preview.sourceFormat}</span><span>来源规则修订 {preview.sourcePolicyRevision}</span><span>有效至 {new Date(preview.expiresAt).toLocaleString()}</span><span>来源 SHA <code>{preview.sourceHash}</code></span><span>来源包 SHA <code>{preview.bundleHash}</code></span><span>Preview <code>{preview.previewId}</code></span></div>{Date.parse(preview.expiresAt)<=clock&&<p className="notice" role="alert">预览已过期；输入与映射已保留，请重新预览后核对。</p>}<ImportOperationMapping operations={preview.operations} targets={view.targets} revisionReview={view.revisionReview} disabled={busy} onToggle={toggleMapping} onEdit={editMapping}/><div className="actions details"><button type="button" className="btn primary" disabled={busy||blocked||!canCommitPreview(view,clock)} onClick={()=>void commit()}>确认整批导入</button><button type="button" className="btn" disabled={busy||!canManagePreviews} onClick={()=>void revokePreview(preview.previewId)}>撤销当前预览</button><small>只提交固定包哈希与映射，提交不重新获取 URL。</small></div></div>}{canManagePreviews&&<ActiveImportPreviews key={policyScope+authority} projectId={ws.projectId} revision={previewRevision} now={clock} busy={busy} revokeError={revokeError} revoke={revokePreview}/>}{result&&<div className="card"><h3 className="section-title">已写入工作配置 · {result.importId}</h3>{result.warnings?.map((warning:string,i:number)=><p className="notice" key={i}>{warning}</p>)}<Table rows={result.operations} columns={[['operationId','Operation'],['versionId','版本 ID'],['routeId','路由 ID']]} actions={r=><Link to={'/apis/'+r.apiId}>查看真实 API →</Link>}/></div>}{edit&&preview&&<Editor title={'导入映射 · '+edit.operationId} fields={fields} renderFields={(value,change)=>fields.filter(field=>field.key==='targetMode'||(value.targetMode==='new'?['newApiCode','newApiName','version']:value.targetMode==='api'?['apiId','version']:['existingVersionId','expectedRevision','existingRouteId','expectedRouteRevision']).includes(field.key)).map(field=><label key={field.key}>{field.label}{field.options?<select required value={value[field.key]??''} onChange={e=>change(field.key,e.target.value)}>{field.options.map(option=><option key={option.value} value={option.value}>{option.label}</option>)}</select>:<input type={field.type==='number'?'number':'text'} min={field.type==='number'?1:undefined} required={field.key!=='existingRouteId'&&(field.key!=='expectedRouteRevision'||!!value.existingRouteId)} value={value[field.key]??''} onChange={e=>change(field.key,e.target.value)}/>}<small>{field.help}</small></label>)} initial={edit} close={()=>setEdit(undefined)} help="映射只选择工作配置目标；服务端仍校验权限、最新修订与路由冲突。" save={saveMapping}/>}</>;
}
