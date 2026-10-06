import {useEffect,useRef,useState} from 'react';
import {useWorkspace} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {apiRequest} from '../api/client';
import {createObservationGate} from '../observability/query-state.mjs';
import {workbenchPlan,loadWorkbench,type WorkbenchResults} from '../workbench/model.mjs';
import {WorkbenchView} from '../workbench/WorkbenchView';

export function Workbench(){
 const ws=useWorkspace(),session=useSession(),gate=useRef(createObservationGate());
 const [end,setEnd]=useState(()=>new Date().toISOString());
 const scope={organizationId:ws.organizationId,projectId:ws.projectId,environmentId:ws.environmentId};
 const identity=JSON.stringify([scope,session.user?.id,session.user?.permissions,session.user?.scopes,session.error,end]);
 const plan=workbenchPlan(scope,(code,target)=>session.can(code,target),end);
 const [result,setResult]=useState<{identity:string;data:WorkbenchResults}|null>(null);
 useEffect(()=>{
  const request=gate.current.begin(identity);setResult({identity,data:plan});
  void loadWorkbench(plan,(path,{signal})=>apiRequest(path,{signal:AbortSignal.any([signal,AbortSignal.timeout(15000)])}),request.signal,(key,value)=>{
   if(gate.current.isCurrent(request))setResult(previous=>previous?.identity===identity?{identity,data:{...previous.data,[key]:value}}:previous);
  });
  return()=>request.controller.abort();
 },[identity]);
 useEffect(()=>{const timer=setInterval(()=>setEnd(new Date().toISOString()),30000);return()=>clearInterval(timer);},[]);
 return <WorkbenchView results={result?.identity===identity?result.data:plan} end={end} refresh={()=>setEnd(new Date().toISOString())}/>;
}
