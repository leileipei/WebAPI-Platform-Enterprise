import type {ReactNode} from 'react';
import {apiDetailPath,apiInWorkspace,type ApiDetail} from '../api/catalog';
import {useSession} from '../auth/SessionProvider';
import {Link,useWorkspace} from '../Shell';
import {Feedback,useRemote} from '../ui';

export type ProjectApiRemote = ReturnType<typeof useRemote<ApiDetail>> & {data:ApiDetail};

export function ProjectApi({id,children}:{id:string;children:(remote:ProjectApiRemote)=>ReactNode}){
  const ws=useWorkspace(),{can}=useSession();
  const remote=useRemote<ApiDetail>(ws.projectId?apiDetailPath(id,ws.environmentId):null,0,false,true);
  const detail=remote.data;
  if(!detail)return <><Feedback {...remote}/>{ws.scopeReady&&!ws.projectId&&
    <p className="notice" role="status">请选择项目后查看 API。 <Link to="/apis">API 目录</Link></p>}</>;
  if(apiInWorkspace(detail.api,id,ws))return children({...remote,data:detail});

  const api=detail.api;
  const owner=api.id?.toLowerCase()===id.toLowerCase()&&can('api.read',{organizationId:api.organizationId,projectId:api.projectId})&&
    ws.tree.projects.find(project=>project.id===api.projectId&&project.organizationId===api.organizationId);
  return <><Feedback {...remote}/><p className="notice" role="status">
    {owner?`此 API 属于「${owner.name}」，请切换到所属项目后打开。`:'当前组织和项目无法核实此 API 的归属。'} <Link to="/apis">API 目录</Link>
  </p></>;
}
