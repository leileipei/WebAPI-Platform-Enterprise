import {useId} from 'react';import type {MetricPoint} from '../api/observability';import {chartSegments,metricDisplay} from './query-state.mjs';
export function TrendChart({points,unit,label,sampleCount=0}:{points:MetricPoint[];unit:string;label:string;sampleCount?:number}){
 const id=useId(),segments=chartSegments(points),valid=segments.flat();const minTime=valid.length?Math.min(...valid.map(p=>Date.parse(p.time))):0,maxTime=valid.length?Math.max(...valid.map(p=>Date.parse(p.time))):1,maxValue=Math.max(...valid.map(p=>p.value),1);
 const xy=(p:{time:string;value:number})=>[90+((Date.parse(p.time)-minTime)/Math.max(maxTime-minTime,1))*474,162-p.value/maxValue*132];
 return <section className="card ob-trend"><div className="ob-card-head"><h3>{label}</h3><small>{unit==='ratio'?'%':unit} · 样本 {sampleCount.toLocaleString()}</small></div>
 {!valid.length?<div className="ob-chart-empty">暂无可用趋势样本</div>:<svg viewBox="0 0 600 204" role="img" aria-labelledby={id}><title id={id}>{label}，单位 {unit}，{valid.length} 个数据点，样本量 {sampleCount}</title>
 {[0,0.5,1].map(v=><g key={v}><line x1="90" x2="564" y1={162-v*132} y2={162-v*132} stroke="#e9edf4"/><text x="84" y={166-v*132} textAnchor="end">{metricDisplay({value:v*maxValue,unit})}</text></g>)}
 {segments.map((s,i)=><polyline key={i} points={s.map(p=>xy(p).join(',')).join(' ')} fill="none" stroke="var(--primary)" strokeWidth="2"/>)}
 {valid.map((p,i)=><circle key={i} cx={xy(p)[0]} cy={xy(p)[1]} r={valid.length<=8?3:1.5} fill="var(--primary)"><title>{new Date(p.time).toLocaleString('zh-CN',{hour12:false})} · {metricDisplay({value:p.value,unit})} · 窗口样本 {sampleCount}</title></circle>)}
 <text x="90" y="192">{new Date(minTime).toLocaleTimeString('zh-CN',{hour12:false})}</text><text x="564" y="192" textAnchor="end">{new Date(maxTime).toLocaleTimeString('zh-CN',{hour12:false})}</text></svg>}
 <details className="ob-trend-summary"><summary>查看同源数据表（最多 600 点）</summary><div className="table-wrap"><table><caption>{label}，单位 {unit}，窗口样本量 {sampleCount}</caption><thead><tr><th>时间</th><th>值</th></tr></thead><tbody>{points.slice(0,600).map((p,i)=><tr key={i}><td>{new Date(p.time).toLocaleString('zh-CN',{hour12:false})}</td><td>{metricDisplay({value:p.value,unit})}</td></tr>)}</tbody></table></div></details></section>;
}
