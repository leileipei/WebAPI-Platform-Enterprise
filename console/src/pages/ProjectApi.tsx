import type {ReactNode} from 'react';
import {apiDetailPath,apiInWorkspace,type ApiDetail} from '../api/catalog';
import {useSession} from '../auth/SessionProvider';
import {Link,useWorkspace} from '../Shell';
import {Feedback,useRemote} from '../ui';

export type ProjectApiRemote = ReturnType<typeof useRemote<ApiDetail>> & {data:ApiDetail;readOnly?:boolean};

export function ProjectApi({id,children,allowDirectRead=false}:{id:string;allowDirectRead?:boolean;children:(remote:ProjectApiRemote)=>ReactNode}){
  const ws=useWorkspace(),{can}=useSession();
  const directRead=allowDirectRead&&ws.scopeReady&&!ws.projectId;
  const remote=useRemote<ApiDetail>(ws.projectId||directRead?apiDetailPath(id,ws.environmentId):null,0,false,true);
  const detail=remote.data;
  if(!detail)return <><Feedback {...remote}/>{ws.scopeReady&&!ws.projectId&&!directRead&&
    <p className="notice" role="status">请选择项目后查看 API。 <Link to="/apis">API 目录</Link></p>}</>;
  if(apiInWorkspace(detail.api,id,ws))return children({...remote,data:detail});

  const api=detail.api;
  const readable=api.id?.toLowerCase()===id.toLowerCase()&&can('api.read',{organizationId:api.organizationId,projectId:api.projectId});
  if(directRead&&readable&&(!ws.organizationId||ws.organizationId===api.organizationId)&&!remote.loading&&!remote.error)
    return children({...remote,data:detail,readOnly:true});

  const owner=readable&&ws.tree.projects.find(project=>project.id===api.projectId&&project.organizationId===api.organizationId);
  return <><Feedback {...remote}/><p className="notice" role="status">
    {owner?`此 API 属于「${owner.name}」，请切换到所属项目后打开。`:'当前组织和项目无法核实此 API 的归属。'} <Link to="/apis">API 目录</Link>
  </p></>;
}
