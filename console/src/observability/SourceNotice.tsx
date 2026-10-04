import {observationErrorLabel} from './investigation-state.mjs';import type {ObservationEnvelope,SourceState} from '../api/observability';
export const sourceNames:Record<SourceState,string>={Available:'采集正常',NoData:'暂无请求样本',Partial:'数据覆盖不完整',Unavailable:'采集源不可用',Stale:'采集已过期',NotApplicable:'尚未启用'};
const reasonNames:Record<string,string>={missing_or_stale_nodes:'节点采集缺失或过期',incomplete_window:'所选历史窗口尚未完整覆盖',environments_without_collection_nodes:'部分环境尚未接入采集节点',telemetry_loss:'检测到指标丢弃或导出失败',no_collection_nodes:'尚未接入采集节点'};
export function SourceNotice({value,error,errorStatus,loading,retry,noun='指标'}:{value?:ObservationEnvelope<unknown>;error?:string;errorStatus?:number;loading?:boolean;retry:()=>void;noun?:'指标'|'日志'|'链路'}){
 const state=error?'Unavailable':value?.sourceState;return <div className={'ob-source '+(error||state==='Partial'||state==='Stale'?'warn':'')} role={error?'alert':'status'}>
 <div><strong>{loading?`正在读取${noun}…`:error?observationErrorLabel(errorStatus):state?(state==='NoData'?`暂无${noun}数据`:sourceNames[state]):`选择项目与环境后读取真实${noun}`}</strong><p>{error||(!value?'查询仅覆盖当前授权环境。':value.coverage.complete?'所选时间窗口的启用节点采集覆盖完整。':noun==='指标'?'未完整覆盖的指标保持空值，请检查采集节点或缩短时间范围。':'当前结果仅覆盖已收到的数据，请检查采集节点或缩短时间范围。')}{value?.coverage.reason&&' '+(reasonNames[value.coverage.reason]||'采集覆盖不足。')}</p>
 {value?.coverage.missingNodes.length!==0&&value?.coverage.missingNodes?.length?<small>缺失节点：{value.coverage.missingNodes.join('、')}</small>:null}</div>
 <div className="ob-source-meta">{value?.observedAt&&<small>采集观察 {new Date(value.observedAt).toLocaleString('zh-CN',{hour12:false})}</small>}<button className="btn" disabled={loading} onClick={retry}>重新查询</button></div></div>;
}
