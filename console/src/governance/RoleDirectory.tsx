import {useState} from 'react';
import {allowNavigation, useWorkspace} from '../Shell';
import {ApiError, apiRequest} from '../api/client';
import {useSession} from '../auth/SessionProvider';
import {Editor, Feedback, Table, useRemote, type Field} from '../ui';
import {PermissionMatrix} from './PermissionMatrix';
import {describePermission, mayConfigurePermissions, type Permission} from './permission-display.mjs';

type Organization = {id:string; name:string};
type Directories = {canManagePlatformRoles:boolean; organizations:Organization[]};
type Role = {id:string; code:string; name:string; organizationId:string|null; isSystem:boolean; revision:number; permissions:string[]};
type PageKind = 'roles'|'permissions';
type Draft = {kind:'role'; role?:Role}|{kind:'permissions'; role:Role};
const roleFields:Field[] = [{key:'code',label:'编码',required:true},{key:'name',label:'名称',required:true}];
const roleColumns:[string,string][] = [['code','角色编码'],['name','角色名称'],['isSystem','系统角色'],['revision','修订']];
const dictionaryColumns:[string,string][] = [['code','权限编码'],['name','权限名称'],['module','模块'],['description','说明']];

function directoryPath(kind:PageKind, organizationId:string|null) {
  return '/' + kind + (organizationId ? '?' + new URLSearchParams({organizationId}) : '');
}

export function RoleDirectory({kind}:{kind:PageKind}) {
  const ws=useWorkspace(), session=useSession();
  const discovery=useRemote<Directories>(session.can('role.manage') ? '/role-directories' : null,0,false,true);
  const [choice,setChoice]=useState<string>();
  const active=!discovery.loading && !discovery.error && !session.error ? discovery.data : undefined;
  const organizations=active?.organizations ?? [];
  const platform=active?.canManagePlatformRoles === true;
  const fallback=organizations.some(o=>o.id===ws.organizationId) ? ws.organizationId
    : platform ? 'platform' : !ws.organizationId && organizations.length===1 ? organizations[0].id : '';
  const requested=choice ?? fallback;
  const scope=(requested==='platform' ? platform : organizations.some(o=>o.id===requested)) ? requested : '';
  return <>
    <div className="breadcrumb">管理控制台 / 资源治理</div>
    <div className="page-head"><h1>{kind==='roles' ? '角色管理' : '权限字典'}</h1>
    {active && (platform || organizations.length>0) && <label className="filter-bar">管理范围
      <select aria-label="角色管理范围" value={scope} onChange={event=>{if(allowNavigation())setChoice(event.target.value);}}>
        {!scope && <option value="">请选择管理范围</option>}
        {organizations.map(o=><option key={o.id} value={o.id}>{o.name}</option>)}
        {platform && <option value="platform">全平台</option>}
      </select>
    </label>}</div>
    <Feedback {...discovery}/>
    {!discovery.loading && !discovery.error && !platform && !organizations.length && <p className="notice">当前没有可管理的角色目录。</p>}
    <Catalog key={kind+'/'+scope} kind={kind} scope={scope} directories={organizations}
      refreshAccess={()=>{discovery.reload();ws.refresh();}}/>
  </>;
}

function Catalog({kind,scope,directories,refreshAccess}:{
  kind:PageKind; scope:string; directories:Organization[]; refreshAccess:()=>void;
}) {
  const ws=useWorkspace();
  const organizationId=scope==='platform' ? null : scope;
  const rolePath=directoryPath('roles',organizationId);
  const roles=useRemote<Role[]>(scope && kind==='roles' ? rolePath : null,0,false,true);
  const dictionary=useRemote<Permission[]>(scope ? directoryPath('permissions',organizationId) : null,0,false,true);
  const [query,setQuery]=useState(''),[view,setView]=useState<string>(),[draft,setDraft]=useState<Draft>();
  const [notice,setNotice]=useState('');
  const roleReady=!!scope && !roles.loading && !roles.error && Array.isArray(roles.data);
  const dictionaryReady=!!scope && !dictionary.loading && !dictionary.error && Array.isArray(dictionary.data);
  const currentRoles=roleReady ? roles.data! : [];
  const items=dictionaryReady ? dictionary.data! : [];
  const currentRole=currentRoles.find(role=>role.id===view);
  const canEdit=(r:Role)=>roleReady && !r.isSystem && (r.organizationId===null
    ? scope==='platform' : directories.some(o=>o.id===r.organizationId));
  function saved() {
    setDraft(undefined);setView(undefined);
    roles.reload();dictionary.reload();refreshAccess();
  }
  function conflict() {
    setDraft(undefined);setNotice('角色修订已更新，请重新打开配置权限并核对最新授权。');
    roles.reload();dictionary.reload();
  }
  const rows=kind==='roles' ? currentRoles.map(role=>({...role,scopeName:role.organizationId
    ? (ws.tree.organizations.find(o=>o.id===role.organizationId)?.name ?? directories.find(o=>o.id===role.organizationId)?.name ?? role.organizationId)
    : '平台'})) : items.map(p=>({...p,name:describePermission(p).label,module:describePermission(p).moduleLabel}));
  const filtered=rows.filter(row=>Object.values(row).some(value=>typeof value==='string' && value.toLowerCase().includes(query.trim().toLowerCase())));
  const columns=kind==='permissions' ? dictionaryColumns : scope==='platform' ? [...roleColumns,['scopeName','所属范围'] as [string,string]] : roleColumns;
  return <>
    <div className="card"><div className="filter-bar">
      <input aria-label="搜索当前页" placeholder="搜索名称 / 编码" value={query} onChange={e=>setQuery(e.target.value)}/>
      <button className="btn" onClick={refreshAccess}>刷新</button>
      {kind==='roles' && <button className="btn primary" disabled={!roleReady} onClick={()=>setDraft({kind:'role'})}>＋ 新建</button>}
    </div>
    {notice && <p role="status" className="notice">{notice}</p>}
    {kind==='roles' && <Feedback {...roles}/>}<Feedback {...dictionary}/>
    <Table rows={filtered} columns={columns} actions={kind==='roles' ? (role:Role)=><>
      <button className="text-btn" disabled={!canEdit(role)} onClick={()=>setDraft({kind:'role',role})}>编辑</button>
      <button className="text-btn" disabled={!dictionaryReady} onClick={()=>setView(role.id)}>查看权限</button>
      <button className="text-btn" disabled={!canEdit(role)||!mayConfigurePermissions(role,items,dictionary)}
        onClick={()=>setDraft({kind:'permissions',role})}>配置权限</button>
    </> : undefined}/>
    </div>
    {currentRole && dictionaryReady && <section className="card permission-preview" aria-label="角色权限详情">
      <header><div><h2>{currentRole.name} · 权限矩阵</h2><p>{currentRole.isSystem?'系统内置角色 · 只读':'自定义角色'} · 修订 {currentRole.revision}</p></div>
        <button className="text-btn" onClick={()=>setView(undefined)}>关闭权限详情</button></header>
      <PermissionMatrix items={items} value={Object.fromEntries(currentRole.permissions.map(code=>[code,true]))} readOnly/>
      {currentRole.permissions.some(code=>!items.some(p=>p.code===code)) && <p className="notice">
        当前字典外的已授予权限　{currentRole.permissions.filter(code=>!items.some(p=>p.code===code)).join('、')}
      </p>}
    </section>}
    {roleReady && draft?.kind==='role' && <Editor title={draft.role?'编辑角色':'新建角色'} fields={roleFields}
      initial={draft.role ?? {}} close={()=>setDraft(undefined)}
      loadLatest={draft.role ? async()=>{
        const latest=(await apiRequest<Role[]>(rolePath)).find(r=>r.id===draft.role!.id);
        if(!latest || !canEdit(latest))throw Error('角色或管理范围已变更，请重新打开。');
        return latest;
      } : undefined}
      save={async(value,key,fresh)=>{
        const r=draft.role;
        await apiRequest(r ? '/roles/'+r.id : '/roles',{method:r?'PUT':'POST',
          body:{code:value.code,name:value.name,organizationId:r ? r.organizationId : organizationId},
          etag:r ? '"'+(fresh?.revision ?? r.revision)+'"' : undefined,idempotencyKey:key});
        saved();
      }}/>}
    {roleReady && dictionaryReady && draft?.kind==='permissions' && <RolePermissionEditor role={draft.role}
      directoryOrganization={organizationId} items={items} close={()=>setDraft(undefined)} saved={saved} conflict={conflict}/>}
  </>;
}

function RolePermissionEditor({role,directoryOrganization,items,close,saved,conflict}:{
  role:Role; directoryOrganization:string|null; items:Permission[]; close:()=>void; saved:()=>void; conflict:()=>void;
}) {
  const needsScopedDictionary=role.organizationId!==directoryOrganization;
  const target=useRemote<Permission[]>(needsScopedDictionary ? directoryPath('permissions',role.organizationId) : null,0,false,true);
  const choices=needsScopedDictionary ? target.data : items;
  if(!choices || !mayConfigurePermissions(role,choices,needsScopedDictionary ? target : undefined)) {
    return <section className="card" aria-label="角色权限">
      {needsScopedDictionary && <Feedback {...target}/>}
      {(!needsScopedDictionary || !target.loading && !target.error) && <p className="notice">当前目录无法配置该角色的完整权限。</p>}
      <button type="button" className="btn" onClick={close}>关闭</button>
    </section>;
  }
  const assigned=new Set(role.permissions);
  return <Editor title={'角色权限 · '+role.name} fields={[]} initial={Object.fromEntries(choices.map(p=>[p.code,assigned.has(p.code)]))}
    explicitSubmitOnly close={close} maySave={v=>choices.some(p=>v[p.code]!==assigned.has(p.code))}
    renderFields={(value,change)=><PermissionMatrix items={choices} value={value} onChange={change}/>}
    save={async(value,key)=>{
      try {
        await apiRequest('/roles/'+role.id+'/permissions',{method:'PUT',etag:'"'+role.revision+'"',idempotencyKey:key,
          body:{permissions:choices.filter(p=>value[p.code]).map(p=>p.code)}});
      } catch(error) {
        if(error instanceof ApiError && error.status===412){conflict();return;}
        throw error;
      }
      saved();
    }}/>;
}
