import {IconActivity,IconCode,IconShieldCheck,IconClock,IconBell,IconServer,IconRefresh} from '@tabler/icons-react';
import type {ReactNode,MouseEvent} from 'react';
import {navigate} from '../navigation.mjs';
import {TrendChart} from '../observability/TrendChart';
import {SourceNotice,sourceNames} from '../observability/SourceNotice';
import {metricDisplay,serializeObservationSearch} from '../observability/query-state.mjs';
import {workbenchMetric,type WorkbenchEntry,type WorkbenchResults} from './model.mjs';

const available=(entry:WorkbenchEntry)=>!['denied','empty'].includes(entry.state);
function Link({to,children}:{to:string;children:ReactNode}){return <a href={to} onClick={(e:MouseEvent<HTMLAnchorElement>)=>{if(e.button||e.metaKey||e.ctrlKey||e.shiftKey||e.altKey)return;e.preventDefault();navigate(to);}}>{children}</a>;}
function Status({entry}:{entry:WorkbenchEntry}){
 if(entry.state==='ready')return null;
 return <div className={'wb-status '+(entry.state==='error'?'error':'')} role={entry.state==='error'?'alert':'status'}>{entry.state==='denied'?'无读取权限或当前范围已失效':entry.state==='empty'?'请选择可访问的项目和环境':entry.state==='loading'?'正在读取真实数据…':entry.error||'读取失败，请刷新重试。'}</div>;
}
function RowsEmpty({children}:{children:ReactNode}){return <div className="wb-status">{children}</div>;}
const date=(value:string)=>new Date(value).toLocaleString('zh-CN',{hour12:false});
export function WorkbenchView({results:r,end,refresh}:{results:WorkbenchResults;end:string;refresh:()=>void}){
 const envelope=r.metrics.state==='ready'?r.metrics.data:undefined;
 const metricsAvailable=!!envelope&&['Available','Partial'].includes(envelope.sourceState);
 const data=metricsAvailable?envelope.data:undefined;
 const rankedGroups=(data?.groups||[]).filter((row:any)=>{const count=row.values.find((v:any)=>v.metric==='request_count');return count&&['Available','Partial'].includes(count.state)&&Number.isFinite(count.value);});
 const query=serializeObservationSearch({range:'24h',end,group:'Api',page:1});
 const metric=(name:string)=>workbenchMetric(r.metrics,name);
 const metricHint=r.metrics.state==='ready'?(sourceNames[envelope?.sourceState as keyof typeof sourceNames]||'暂无指标'):r.metrics.state==='denied'?'无读取权限':r.metrics.state==='error'?'读取失败':r.metrics.state==='empty'?'未选择环境':'正在读取';
 const count=(entry:WorkbenchEntry)=>entry.state==='ready'&&Number.isFinite(entry.data?.total)?entry.data.total.toLocaleString():'—';
 const cards=[
  {label:'API 总数',value:count(r.api),hint:'当前项目 · 全部 API',icon:IconCode},
  {label:'请求量 · 24h',value:metric('request_count')?metricDisplay(metric('request_count')!):'—',hint:metricHint,icon:IconActivity},
  {label:'成功率 · 24h',value:metric('success_ratio')?metricDisplay(metric('success_ratio')!):'—',hint:metricHint,icon:IconShieldCheck},
  {label:'P95 延迟 · 24h',value:metric('latency_p95_ms')?metricDisplay(metric('latency_p95_ms')!):'—',hint:metricHint,icon:IconClock},
  {label:'未确认告警',value:count(r.alerts),hint:'当前环境 · Open 状态',icon:IconBell},
  {label:'网关节点',value:count(r.nodes),hint:'当前环境 · 包含停用身份',icon:IconServer}
 ];
 const releases=r.releases.state==='ready'?r.releases.data?.items||[]:[];
 const pending=releases.filter((item:any)=>item.state==='WaitingApproval');
 const nodes=r.nodes.state==='ready'?r.nodes.data?.items||[]:[];
 return <>
  <div className="breadcrumb">工作台 / 企业工作台</div>
  <div className="page-head"><div><div className="ob-eyebrow">ENTERPRISE · WORKBENCH</div><h1>企业工作台</h1><p>API 运行与治理总览 · 仅展示当前授权范围内的真实数据</p></div><div className="actions">{available(r.metrics)&&<Link to={'/observability/metrics'+query}>查看监控 →</Link>}<button className="btn" onClick={refresh}><IconRefresh size={15}/> 刷新数据</button></div></div>
  <div className="wb-scope"><span>API 总数：当前项目</span><span>运行指标、网关、告警与发布：当前环境</span><small>窗口截止 {date(end)} · 每 30 秒重新查询</small></div>
  <div className="wb-kpis">{cards.map(({label,value,hint,icon:Icon})=><section className="card ob-kpi" key={label}><div><span>{label}</span><Icon size={17}/></div><strong>{value}</strong><small>{hint}</small></section>)}</div>
  <Status entry={r.api}/>
  {r.metrics.state==='denied'||r.metrics.state==='empty'?<Status entry={r.metrics}/>:<SourceNotice value={envelope} loading={r.metrics.state==='loading'} error={r.metrics.error} errorStatus={r.metrics.status} retry={refresh}/>}
  <div className="wb-layout">
   <div className="wb-trend"><TrendChart label="请求趋势 · 近 24 小时" unit="requests/s" points={data?.trends?.request_rps||[]} sampleCount={metric('request_count')?.sampleCount||0}/><p className="wb-footnote">请求量和趋势为采集估计；空白或断点表示缺失数据。</p></div>
   <section className="card"><div className="ob-card-head"><div><h3>Gateway 运行状态</h3><small>节点总数 {count(r.nodes)} · 展示前 5 个</small></div>{available(r.nodes)&&<Link to="/nodes">全部节点 →</Link>}</div><Status entry={r.nodes}/>{r.nodes.state==='ready'&&<><div className="table-wrap"><table><thead><tr><th>节点 / 状态</th><th>配置 / 序列</th><th>与目标一致</th></tr></thead><tbody>{nodes.map((n:any)=><tr key={n.id}><td><Link to={'/nodes/'+n.id}>{n.nodeName}</Link><small className="wb-cell-meta">{n.offline?'Offline':n.status}{!n.enabled?' · 身份停用':''}</small></td><td>v{n.currentConfigVersion} / seq {n.currentDeploymentSequence}<small className="wb-cell-meta">目标 v{n.desiredConfigVersion??0} / seq {n.desiredDeploymentSequence}</small></td><td><span className={'badge '+(n.enabled&&!n.offline&&n.currentConfigVersion===n.desiredConfigVersion&&n.currentDeploymentSequence===n.desiredDeploymentSequence?'green':'amber')}>{n.enabled&&!n.offline&&n.currentConfigVersion===n.desiredConfigVersion&&n.currentDeploymentSequence===n.desiredDeploymentSequence?'一致':'待核对'}</span></td></tr>)}</tbody></table></div>{!nodes.length&&<RowsEmpty>当前环境暂无节点身份</RowsEmpty>}</>}</section>
   <section className="card"><div className="ob-card-head"><div><h3>Top API · 请求量</h3><small>近 24 小时 · 前 5 名</small></div>{available(r.metrics)&&<Link to={'/observability/metrics'+query}>详细指标 →</Link>}</div><Status entry={r.metrics}/>{r.metrics.state==='ready'&&<><div className="table-wrap"><table><thead><tr><th>API</th><th>请求量</th><th>成功率</th><th>P95</th></tr></thead><tbody>{rankedGroups.map((row:any)=>{const value=(name:string)=>{const k=row.values.find((v:any)=>v.metric===name);return k&&['Available','Partial'].includes(k.state)?metricDisplay(k):'—';};return <tr key={row.key}><td>{/^[0-9a-f-]{36}$/i.test(row.key)?<Link to={'/observability/apis/'+row.key+query}>{row.name}</Link>:row.name}</td><td>{value('request_count')}</td><td>{value('success_ratio')}</td><td>{value('latency_p95_ms')}</td></tr>;})}</tbody></table></div>{!rankedGroups.length&&<RowsEmpty>当前窗口暂无可用 API 排名</RowsEmpty>}</>}</section>
   <section className="card"><div className="ob-card-head"><div><h3>未确认告警</h3><small>仅 Open 状态 · 展示最近 5 条</small></div>{available(r.alerts)&&<Link to="/observability/alerts?status=Open">告警中心 →</Link>}</div><Status entry={r.alerts}/>{r.alerts.state==='ready'&&<><div className="table-wrap"><table><thead><tr><th>告警</th><th>级别</th><th>发生时间</th></tr></thead><tbody>{(r.alerts.data?.items||[]).map((item:any)=><tr key={item.id}><td><span className="wb-message">{item.message}</span><small className="wb-cell-meta">{item.status}</small></td><td><span className={'badge '+(item.severity==='Critical'?'red':'amber')}>{item.severity==='Critical'?'严重':item.severity==='Warning'?'警告':'提示'}</span></td><td>{date(item.startedAt)}</td></tr>)}</tbody></table></div>{!r.alerts.data?.items?.length&&<RowsEmpty>当前环境没有未确认告警</RowsEmpty>}</>}</section>
   <section className="card wb-wide"><div className="ob-card-head"><div><h3>最近发布</h3><small>当前环境 · 展示最近 5 条</small></div>{available(r.releases)&&<Link to="/releases">发布中心 →</Link>}</div><Status entry={r.releases}/>{r.releases.state==='ready'&&<><div className="table-wrap"><table><thead><tr><th>发布编号</th><th>状态</th><th>目标配置 / 序列</th><th>发布确认</th><th>创建时间</th></tr></thead><tbody>{releases.slice(0,5).map((item:any)=><tr key={item.id}><td><Link to={'/releases/'+item.id}>{item.releaseNo}</Link></td><td><span className="badge blue">{item.state}</span></td><td>v{item.targetConfigVersion} / seq {item.deploymentSequence??'—'}</td><td>{item.targets?.filter((target:any)=>target.acknowledged).length||0} / {item.targets?.length||0}</td><td>{date(item.createdAt)}</td></tr>)}</tbody></table></div>{!releases.length&&<RowsEmpty>当前环境暂无发布记录</RowsEmpty>}</>}</section>
   <section className="card wb-wide"><div className="ob-card-head"><div><h3>最近发布中的待审批</h3><small>在最近 50 条发布中筛选，不代表全部任务或个人可审批任务</small></div>{available(r.releases)&&<Link to="/approvals">审批中心 →</Link>}</div><Status entry={r.releases}/>{r.releases.state==='ready'&&<><div className="wb-pending">{pending.slice(0,5).map((item:any)=><div key={item.id}><Link to={'/releases/'+item.id}>{item.releaseNo}</Link><span className="badge amber">WaitingApproval</span><small>{date(item.createdAt)}</small></div>)}</div>{!pending.length&&<RowsEmpty>最近 50 条发布中没有待审批记录</RowsEmpty>}{pending.length>5&&<p className="wb-footnote">本区展示前 5 条，更多记录请进入审批中心。</p>}</>}</section>
  </div>
  <p className="wb-footnote">各模块独立查询，时间可能略有差异；工作配置与网关运行配置分别保留原事实。工作台中的快捷入口遵循已有服务端权限。</p>
 </>;
}
