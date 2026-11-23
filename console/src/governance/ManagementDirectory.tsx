import {useState} from 'react';
import {apiRequest} from '../api/client';
import {Feedback,Pagination,usePaged,useRemote} from '../ui';
import type {Resource} from '../api/types';

type Person={id:string;displayName:string;username:string;revision:number;administrator:boolean;legacyAdministrator:boolean};
type Entry={kind:'detail'|'administrators'|'owner';row:Resource};
export type ManagementEntry=Entry;

export function ManagementDirectory({entry,onClose,onSaved}:{entry:Entry;onClose:()=>void;onSaved:()=>void}){
 const {kind,row}=entry;
 const [search,setSearch]=useState(''),[query,setQuery]=useState(''),[candidates,setCandidates]=useState(false);
 const [selected,setSelected]=useState<Person>(),[busy,setBusy]=useState(false),[error,setError]=useState('');
 const detail=useRemote<Resource&{ownerUserId?:string|null}>(kind==='detail'?'/organizations/'+row.id:kind==='owner'?'/projects/'+row.id:null,0,false,true);
 const base=kind==='owner'?`/projects/${row.id}/owner-candidates`:`/organizations/${row.id}/administrators`;
 const list=usePaged(kind==='detail'?null:base+'?'+new URLSearchParams({search:query,...(kind==='administrators'?{candidates:String(candidates)}:{})}));
 const rows:Person[]=list.data?.items??[];
 function reset(){setSelected(undefined);setSearch('');setQuery('');setError('');}
 async function save(person:Person){
  if(busy||!rows.some(x=>x.id===person.id&&x.revision===person.revision)||list.error||list.loading)return;
  setError('');setBusy(true);
  try{
   if(kind==='owner'){
    if(detail.error||detail.loading||detail.data?.id!==row.id)return;
    await apiRequest('/projects/'+row.id+'/owner',{method:'PUT',etag:'"'+detail.data.revision+'"',body:{userId:person.id},idempotencyKey:crypto.randomUUID()});
   }
   else await apiRequest(base+'/'+person.id,{method:'PUT',etag:'"'+person.revision+'"',body:{enabled:candidates},idempotencyKey:crypto.randomUUID()});
   onSaved();
  }catch(e){setError((e as Error).message);setSelected(undefined);list.reload();if(kind==='owner')detail.reload();}
  finally{setBusy(false);}
 }
 return <section className="card details" aria-label={kind==='owner'?'设置项目负责人':kind==='detail'?'组织详情':'组织管理员设置'}>
  <header><h2>{row.name} · {kind==='owner'?'设置项目负责人':kind==='detail'?'组织详情':'管理员设置'}</h2>
   <button className="text-btn" disabled={busy} onClick={onClose}>关闭</button></header>
  {kind==='detail'?<><Feedback {...detail}/>{!detail.error&&detail.data?.id===row.id&&<dl>
    <dt>组织名称</dt><dd>{detail.data.name}</dd><dt>编码</dt><dd>{detail.data.code}</dd><dt>状态</dt><dd>{detail.data.status}</dd>
    <dt>组织 ID</dt><dd>{detail.data.id}</dd><dt>当前修订</dt><dd>{detail.data.revision}</dd>
  </dl>}</>:<>
   {kind==='owner'&&<><Feedback {...detail}/>{detail.data?.id===row.id&&!detail.error&&<p>当前负责人 {detail.data.ownerUserId??'未指定'} · 项目修订 {detail.data.revision}</p>}</>}
   {kind==='administrators'&&<div className="tabs">{[[false,'当前管理员'],[true,'添加管理员']].map(([value,label])=><button key={String(value)} className={candidates===value?'active':''} disabled={busy} onClick={()=>{reset();setCandidates(value===true);}}>{label}</button>)}</div>}
   <form className="filter-bar" onSubmit={e=>{e.preventDefault();setSelected(undefined);setQuery(search.trim());list.setPage(1);}}>
    <label>查找人员<input value={search} disabled={busy} maxLength={128} onChange={e=>setSearch(e.target.value)} placeholder="用户名或显示名称"/></label><button className="btn" disabled={busy}>搜索</button>
   </form><Feedback {...list}/>{error&&<p className="error" role="alert">{error}</p>}
   {!list.loading&&!list.error&&<><div className="table-wrap"><table><thead><tr><th>姓名</th><th>账号</th><th>操作</th></tr></thead><tbody>{rows.map(person=><tr key={person.id}><td>{person.displayName}</td><td>{person.username}</td><td>
    <button className="text-btn" disabled={busy||kind==='owner'&&(detail.loading||!!detail.error||detail.data?.id!==row.id)} onClick={()=>setSelected(person)}>{kind==='owner'?'选为负责人':candidates?'指定为管理员':'移除管理员'}</button>
   </td></tr>)}</tbody></table>{!rows.length&&<p className="empty">没有匹配的人员</p>}</div><Pagination remote={list}/></>}
   {selected&&rows.some(x=>x.id===selected.id&&x.revision===selected.revision)&&!list.error&&!list.loading&&<section className="notice" role="alert" aria-label="确认人员变更">
    <strong>{selected.displayName}（{selected.username}）</strong><p>{kind==='owner'?'确认将当前项目交接给此人？此次交接仅变更负责人。':candidates?'确认授予此人当前组织的组织管理员权限和组织级读写范围？':selected.legacyAdministrator?'此人持有全局组织管理员角色。移除后，该角色保留，当前组织的组织级读写范围降为只读。':'确认移除此人在当前组织的管理员角色？原有数据范围继续保留。'}</p>
    <button className="btn" disabled={busy} onClick={()=>setSelected(undefined)}>取消</button><button className="btn primary" disabled={busy} onClick={()=>void save(selected)}>确认变更</button>
   </section>}
  </>}
 </section>;
}
