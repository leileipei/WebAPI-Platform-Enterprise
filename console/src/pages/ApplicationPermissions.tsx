import {useEffect, useState} from 'react';
import {useWorkspace} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {apiRequest, ApiError} from '../api/client';
import {Editor, Feedback, Table, useRemote, type Field} from '../ui';
import {
  readRouteMethodOptions, routeMethodSelection, toggleRouteMethod, type RouteMethodGrant, type RouteMethodOptions
} from '../applications/route-method-permissions.mjs';

type Permission = {
  id:string; apiId:string; environmentId:string; validFrom:string; expiresAt?:string|null; revision:number;
  mode?:'LegacyApi'|'RouteMethod'; routeMethods?:RouteMethodGrant[]|null;
};
type PermissionEdit = {record?:Permission; mode:'LegacyApi'|'RouteMethod'};

function local(value: string) {
  const date = new Date(value);
  return new Date(+date - date.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
}

function initialPermission(record: Permission|undefined, environmentId: string) {
  return record ? {...record, validFrom:local(record.validFrom), expiresAt:record.expiresAt ? local(record.expiresAt) : '',
    routeMethods:record.mode === 'RouteMethod' ? record.routeMethods ?? [] : []}
    : {environmentId, validFrom:local(new Date().toISOString()), expiresAt:'', routeMethods:[]};
}

export function ApplicationPermissions({application, rows, reload}: {application:any; rows:Permission[]; reload:()=>void}) {
  const ws = useWorkspace(), {can} = useSession();
  const [edit, setEdit] = useState<PermissionEdit>();
  const projectId = application.projectId || ws.projectId;
  const apis = useRemote<any>(projectId ? `/projects/${projectId}/apis` : null, 0, true);
  const allowed = can('app.permission.manage', {organizationId:application.organizationId, projectId:application.projectId}, true);
  const environments = ws.tree.environments.filter(environment => environment.projectId === projectId);
  const canWriteEnvironment = (id: string) => environments.some(environment => environment.id === id) &&
    can('app.permission.manage', {organizationId:application.organizationId, projectId, environmentId:id}, true);
  const fields: Field[] = [
    {key:'apiId', label:'API', required:true, options:(apis.data?.items || []).map((api:any) => ({value:api.id, label:api.name}))},
    {key:'environmentId', label:'环境', required:true, options:environments.map(environment => ({value:environment.id, label:environment.name}))},
    {key:'validFrom', label:'授权生效时间', type:'datetime-local', required:true},
    {key:'expiresAt', label:'授权失效时间', type:'datetime-local', help:'留空表示无固定失效时间'}
  ];
  const close = () => setEdit(undefined);

  return <>
    <div className="filter-bar">
      <button className="btn primary" disabled={!allowed || !apis.data?.items.length}
        onClick={() => setEdit({mode:'LegacyApi'})}>＋ 新增授权</button>
      <button className="btn" disabled={!allowed || !apis.data?.items.length || !environments.some(environment => canWriteEnvironment(environment.id))}
        onClick={() => setEdit({mode:'RouteMethod'})}>＋ 新增路由方法授权</button>
    </div>
    <Table rows={rows.map(row => ({...row, modeLabel:row.mode === 'RouteMethod' ? '路由方法' : '旧版 API',
      methodCount:row.mode === 'RouteMethod' ? row.routeMethods?.reduce((count, grant) => count + grant.methods.length, 0) : undefined}))}
      columns={[['apiId','API ID'],['environmentId','环境 ID'],['modeLabel','授权模式'],['methodCount','方法数'],
        ['validFrom','生效'],['expiresAt','失效'],['revision','修订']]}
      actions={(row:Permission) => <>
        <button disabled={!allowed || row.mode === 'RouteMethod' && !canWriteEnvironment(row.environmentId)} className="text-btn"
          onClick={() => setEdit({record:row, mode:row.mode ?? 'LegacyApi'})}>编辑授权</button>
        {row.mode !== 'RouteMethod' && <button disabled={!allowed || !canWriteEnvironment(row.environmentId)} className="text-btn"
          onClick={() => setEdit({record:row, mode:'RouteMethod'})}>改为路由方法授权</button>}
      </>}/>
    {edit?.mode === 'LegacyApi' && <Editor title="API 授权" fields={fields}
      initial={initialPermission(edit.record, ws.environmentId)} allowed={allowed} close={close}
      save={async (value,key) => {
        await savePermission(application.id, edit.record, {apiId:value.apiId, environmentId:value.environmentId,
          validFrom:value.validFrom, expiresAt:value.expiresAt || null}, key, edit.record?.revision);
        reload();
      }}/>}
    {edit?.mode === 'RouteMethod' && <RouteMethodEditor applicationId={application.id} original={edit.record}
      environmentId={ws.environmentId} fields={fields} allowed={allowed} canWriteEnvironment={canWriteEnvironment} close={close} reload={reload}/>}
  </>;
}

async function savePermission(applicationId: string, original: Permission|undefined, body: unknown, key: string, revision?:number) {
  await apiRequest(`/applications/${applicationId}/permissions${original ? '/' + original.id : ''}`, {
    method:original ? 'PUT' : 'POST', body, etag:original ? `"${revision}"` : undefined, idempotencyKey:key
  });
}

function RouteMethodEditor({applicationId, original, environmentId, fields, allowed, canWriteEnvironment, close, reload}: {
  applicationId:string; original?:Permission; environmentId:string; fields:Field[]; allowed:boolean;
  canWriteEnvironment:(environmentId:string)=>boolean; close:()=>void; reload:()=>void;
}) {
  const [initial] = useState(() => initialPermission(original, canWriteEnvironment(environmentId) ? environmentId : ''));
  const [options, setOptions] = useState<RouteMethodOptions|null>(null);
  const [refreshRequired, setRefreshRequired] = useState(false);
  const [apiField, environmentField, ...timeFields] = fields;
  const routeFields = [apiField, {...environmentField, options:environmentField.options?.filter(option => canWriteEnvironment(option.value))}];

  function selected(value:Record<string,any>) {
    if (!canWriteEnvironment(value.environmentId) || refreshRequired || options?.apiId !== value.apiId ||
        options?.environmentId !== value.environmentId ||
        original && (original.apiId !== value.apiId || original.environmentId !== value.environmentId)) return [];
    const valid = routeMethodSelection(value.routeMethods, options);
    return valid.length === value.routeMethods?.length ? valid : [];
  }

  async function readLatest() {
    const latest = (await apiRequest<Permission[]>(`/applications/${applicationId}/permissions`))
      .find(row => row.id === original?.id);
    if (!original || !latest || latest.apiId !== original.apiId || latest.environmentId !== original.environmentId)
      throw new Error('授权范围已变化，请关闭表单并重新读取应用授权。');
    return latest;
  }

  return <Editor title={original?.mode === 'RouteMethod' ? '编辑路由方法授权' : original ? '改为路由方法授权' : '新增路由方法授权'}
    fields={fields} initial={initial} allowed={allowed} close={close} explicitSubmitOnly loadLatest={original ? readLatest : undefined}
    maySave={value => selected(value).length > 0}
    help="保存至工作配置，经 2.4 发布和节点确认后才影响网关。"
    renderFields={(value,change) => <>
      {routeFields.map(field => <label key={field.key}>{field.label}
        <select aria-label={field.label} required disabled={!!original} value={value[field.key] ?? ''} onChange={event => {
          setOptions(null);
          change(field.key,event.target.value);
          change('routeMethods',[]);
        }}>
          <option value="">请选择</option>
          {field.options?.map(option => <option value={option.value} key={option.value}>{option.label}</option>)}
        </select>
      </label>)}
      {timeFields.map(field => <label key={field.key}>{field.label}
        <input aria-label={field.label} type="datetime-local" required={field.required} value={value[field.key] ?? ''}
          onChange={event => change(field.key,event.target.value)}/>
        {field.help && <small>{field.help}</small>}
      </label>)}
      <RouteMethodFields applicationId={applicationId} environmentId={value.environmentId || ''} apiId={value.apiId || ''}
        allowed={allowed && canWriteEnvironment(value.environmentId)} methods={value.routeMethods}
        refreshRequired={refreshRequired} onOptions={setOptions} onRefresh={() => setRefreshRequired(false)}
        onChange={methods => change('routeMethods', methods)}/>
    </>}
    save={async (value,key,fresh) => {
      const routeMethods = selected(value);
      if (!routeMethods.length) throw new Error('请基于当前路由修订选择需要授权的方法。');
      const sameTime = (field:'validFrom'|'expiresAt') => !!initial[field] && value[field] === new Date(initial[field]).toISOString();
      try {
        await savePermission(applicationId, original, {mode:'RouteMethod', apiId:value.apiId, environmentId:value.environmentId,
          validFrom:original && sameTime('validFrom') ? original.validFrom : value.validFrom,
          expiresAt:original && sameTime('expiresAt') ? original.expiresAt : value.expiresAt || null, routeMethods}, key, fresh?.revision ?? original?.revision);
        reload();
      } catch (error) {
        if (error instanceof ApiError && (error.status === 409 || error.status === 412)) {
          setOptions(null);
          setRefreshRequired(true);
          throw new ApiError(error.status,error.status === 412
            ? '授权修订已变化。请查看最新授权修订，重新读取目标路由，再核对要授权的方法。'
            : '目标路由或方法授权已变化。请重新读取目标路由并选择方法。',error.traceId,error.issues,error.code);
        }
        if (!(error instanceof ApiError) || error.status >= 500) {
          setOptions(null);
          setRefreshRequired(true);
          throw new Error('提交结果待核对。请先核对应用授权记录，再重新读取目标路由。');
        }
        if (error.status === 401 || error.status === 403) {
          setOptions(null);
          setRefreshRequired(true);
        }
        throw error;
      }
    }}/>
}

function RouteMethodFields({applicationId, environmentId, apiId, allowed, methods, refreshRequired, onOptions, onRefresh, onChange}: {
  applicationId:string; environmentId:string; apiId:string; allowed:boolean; methods:unknown; refreshRequired:boolean;
  onOptions:(options:RouteMethodOptions|null)=>void; onRefresh:()=>void; onChange:(methods:RouteMethodGrant[])=>void;
}) {
  const query = allowed && environmentId && apiId ? new URLSearchParams({environmentId, apiId}) : null;
  const path = query && !refreshRequired ? `/applications/${applicationId}/route-method-options?${query}` : null;
  const remote = useRemote<unknown>(path);
  const verified = path && !remote.loading && !remote.error ? readRouteMethodOptions(remote.data,environmentId,apiId) : null;
  const invalidResponse = !!path && !remote.loading && !remote.error && !!remote.updated && !verified;
  const chosen = routeMethodSelection(methods, verified);
  useEffect(() => {
    onOptions(verified);
    if (!Array.isArray(methods) || !methods.length) return;
    const current = routeMethodSelection(methods, verified);
    if (refreshRequired || remote.error || invalidResponse || verified && current.length !== methods.length) onChange(current);
  }, [verified, methods, refreshRequired, remote.error, invalidResponse, onOptions, onChange]);

  return <section aria-label="目标路由及方法" style={{gridColumn:'1/-1'}}>
    <h3>目标路由及方法</h3>
    {!query && <p className="muted">选择可写目标环境和 API 后读取当前路由。</p>}
    {query && <button type="button" className="btn" disabled={!!path && remote.loading} onClick={() => {
      onOptions(null);
      onChange([]);
      if (refreshRequired) onRefresh();
      else remote.reload();
    }}>重新读取目标路由</button>}
    {path && <Feedback {...remote} error={remote.error && !remote.errorStatus ? '目标路由读取失败，请重试。' : remote.error}/>}
    {invalidResponse && <p className="error" role="alert">目标路由数据与所选环境、API 或修订不匹配。请重新读取。</p>}
    {verified?.routes.length === 0 && <p className="notice">该环境和 API 暂无可授权的路由方法。</p>}
    {verified?.routes.map(route => <fieldset className="promotion-route" key={route.routeId}>
      <legend>{route.path} · 授权修订 {route.authorizationRevision}</legend>
      <div className="actions" style={{flexWrap:'wrap'}}>
        {route.methods.map(method => <label className="checkline" key={method}>
          <input type="checkbox" aria-label={method + ' ' + route.path}
            checked={chosen.some(grant => grant.routeId === route.routeId && grant.methods.includes(method))}
            onChange={event => onChange(toggleRouteMethod(chosen, route, method, event.target.checked))}/>{method}
        </label>)}
      </div>
    </fieldset>)}
  </section>;
}
