import {useSession} from '../auth/SessionProvider';
import {Feedback,Pagination,Table,usePaged} from '../ui';
export function ApplicationAudit({application}:{application:any}){
 const {can}=useSession(),scope={organizationId:application.organizationId,projectId:application.projectId},allowed=can('app.read',scope)&&can('audit.read',scope);
 const remote=usePaged(allowed?`/applications/${application.id}/audit`:null);
 if(!allowed)return <div className="error" role="alert">需要此应用范围内的 app.read 和 audit.read 权限。</div>;
 return <><div className="ob-card-head"><div><h2>应用审计</h2><small>由服务端限定应用、凭证及授权关联记录</small></div><button className="btn" disabled={remote.loading} onClick={remote.reload}>刷新</button></div><Feedback {...remote}/><Table rows={(remote.data?.items||[]).map((row:any)=>({...row,actor:row.userId||'历史未记录',time:new Date(row.createdAt).toLocaleString('zh-CN',{hour12:false})}))} columns={[["time","时间"],["action","操作"],["actor","操作者 ID"],["resourceType","资源类型"],["resourceId","资源 ID"]]}/><Pagination remote={remote}/><p className="muted">没有可信历史审计时，创建／撤销人显示未记录；应用负责人不代表操作人。</p></>;
}
