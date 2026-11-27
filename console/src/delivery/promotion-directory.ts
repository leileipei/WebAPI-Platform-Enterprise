import {useEffect,useRef,useState} from 'react';
import {apiRequest} from '../api/client';

type Choice={id:string;[key:string]:any};
type RouteChoice=Choice&{apiId:string;apiVersionId?:string|null;path:string;methods:string[];revision:number};
type Group<T=Choice>=T[]|null;
type Directory={clusters:Group;routes:Group<RouteChoice>;policies:Group;applications:Group;truncated:boolean;governedTarget?:boolean};
type Mapping={routes:{clusterId:string;targetRouteId:string|null;policies:{targetPolicyId:string|null}[]}[];applications:{applicationId:string;credentialIds:string[];authorizationIds:string[]}[]};
type DirectoryPage={owner:Directory;value:Directory;page:number;query:string};
function mergeChoices<T extends Choice>(old:T[],next:T[]){
  const items=new Map(old.map(item=>[item.id,item]));
  for(const item of next)items.set(item.id,item);
  return [...items.values()];
}
function join<T extends Choice>(old:Group<T>|undefined,next:Group<T>){
  return next===null?null:mergeChoices(old||[],next);
}
function merge(old:Partial<Directory>,next:Directory):Directory{
  const previous=new Map((old.applications||[]).map(app=>[app.id,app]));
  return {...next,clusters:join(old.clusters,next.clusters),routes:join(old.routes,next.routes),policies:join(old.policies,next.policies),
    applications:next.applications===null?null:mergeChoices(old.applications||[],next.applications.map(app=>({...app,
      credentials:mergeChoices(previous.get(app.id)?.credentials||[],app.credentials),
      authorizations:mergeChoices(previous.get(app.id)?.authorizations||[],app.authorizations)})))};
}
function selections(options:Directory,mapping?:Mapping):Partial<Directory>{
  const routes=mapping?.routes||[],apps=new Map((mapping?.applications||[]).map(a=>[a.applicationId,a]));
  const clusters=new Set(routes.map(r=>r.clusterId)),selectedRoutes=new Set(routes.map(r=>r.targetRouteId));
  const policies=new Set(routes.flatMap(r=>r.policies.map(p=>p.targetPolicyId)));
  return {clusters:options.clusters?.filter(x=>clusters.has(x.id)),routes:options.routes?.filter(x=>selectedRoutes.has(x.id)),
    policies:options.policies?.filter(x=>policies.has(x.id)),applications:options.applications?.filter(a=>apps.has(a.id)).map(a=>({...a,
      credentials:a.credentials.filter((k:Choice)=>apps.get(a.id)!.credentialIds.includes(k.id)),
      authorizations:a.authorizations.filter((g:Choice)=>apps.get(a.id)!.authorizationIds.includes(g.id))}))};
}

export function usePromotionDirectory(id:string,initial:Directory|undefined,mapping:Mapping|undefined,lease:()=>()=>boolean,fail:(e:any)=>void){
  const [result,setResult]=useState<DirectoryPage>(),[search,setSearch]=useState(''),[busy,setBusy]=useState(false);
  const abort=useRef<AbortController|undefined>(undefined);
  useEffect(()=>{setResult(undefined);setSearch('');setBusy(false);return()=>abort.current?.abort();},[id,initial]);
  const current=result&&result.owner===initial?result:undefined;
  const options=current?.value||initial;
  async function load(more:boolean){
    if(!initial||!options)return;
    abort.current?.abort();
    const controller=new AbortController(),alive=lease();
    abort.current=controller;setBusy(true);
    const query=more?(current?.query||''):search.trim(),page=more?(current?.page||1)+1:1;
    try{
      const params=new URLSearchParams({page:String(page),pageSize:'1000',search:query});
      const next=await apiRequest<Directory>('/release-promotions/'+id+'/mapping-options?'+params,{signal:controller.signal});
      if(alive()&&!controller.signal.aborted)setResult({owner:initial,query,page,value:merge(more?options:selections(options,mapping),next)});
    }catch(e){if(alive()&&!controller.signal.aborted)fail(e);}
    finally{if(alive()&&!controller.signal.aborted)setBusy(false);}
  }
  return {options,search,setSearch,busy,load};
}
