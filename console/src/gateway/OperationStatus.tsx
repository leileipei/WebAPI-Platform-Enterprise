import {useEffect,useState} from 'react';
import {apiRequest} from '../api/client';

export type GatewayOperation={id:string;nodeId:string;instanceId:string;state:string;outcome?:string;httpStatus?:number;durationMs?:number};
const labels:Record<string,string>={Queued:'等待节点领取',Running:'节点执行中',Expired:'已超时，未收到有效回执',
  applied:'配置已应用并确认',rejected:'配置或确认被拒绝',no_configuration:'当前环境没有目标配置',unavailable:'节点或目标不可用',
  healthy:'探测成功',unhealthy:'后端返回异常状态',timeout:'探测超时',target_changed:'目标或权限已变化'};

export function OperationStatus({path,initial}:{path:string;initial:GatewayOperation}){
  const [operation,setOperation]=useState(initial),[error,setError]=useState('');
  useEffect(()=>{
    const controller=new AbortController();
    let timer:ReturnType<typeof setTimeout>;
    const refresh=async()=>{
      try{
        const value=await apiRequest<GatewayOperation>(path,{signal:controller.signal});
        if(controller.signal.aborted)return;
        setOperation(value);setError('');
        if(value.state==='Queued'||value.state==='Running')timer=setTimeout(refresh,1500);
      }catch(e){if(!controller.signal.aborted)setError(e instanceof Error?e.message:'无法读取运维回执');}
    };
    void refresh();
    return ()=>{controller.abort();clearTimeout(timer);};
  },[path]);
  return <p className="notice" role="status">{error||labels[operation.outcome||operation.state]||operation.state}
    {operation.httpStatus!=null && ' · HTTP '+operation.httpStatus}{operation.durationMs!=null && ' · '+operation.durationMs+' ms'}</p>;
}
