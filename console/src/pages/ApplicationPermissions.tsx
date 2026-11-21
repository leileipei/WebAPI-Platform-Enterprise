import {useEffect,useRef,useState} from 'react';
import {allowNavigation,useWorkspace} from '../Shell';
import {useSession} from '../auth/SessionProvider';
import {Feedback,Pagination,useRemote} from '../ui';
import {matrixBatchLimit,matrixLegacyMoveCells,matrixPageSize} from '../applications/permission-matrix-model.mjs';
import {allowedModes,keyForCell,MatrixGrid,selectableCell} from '../applications/permission-matrix-view';
import {PermissionMatrixDialog} from '../applications/permission-matrix-dialog';
import type {MatrixAction,MatrixCell,MatrixDraft,MatrixPage,MatrixReceipt} from '../applications/permission-matrix-types';

type Application = {id:string; organizationId:string; projectId:string|null};

export function ApplicationPermissions({application,reload}:{application:Application; reload:()=>void}) {
  const ws = useWorkspace();
  const {user,can,error} = useSession();
  const [chosenProject,setChosenProject] = useState('');
  const canReadApp = can('app.read',{organizationId:application.organizationId,projectId:application.projectId});
  const canReadProject = (projectId:string) => can('api.read',{organizationId:application.organizationId,projectId});
  const projects = ws.tree.projects.filter(project => project.organizationId === application.organizationId && canReadProject(project.id));
  const sharedProjectId = [chosenProject,ws.projectId,projects[0]?.id].find(id => projects.some(project => project.id === id));
  const projectId = application.projectId ?? sharedProjectId;
  const projectName = projects.find(project => project.id === projectId)?.name;
  const authority = JSON.stringify([application.id,projectId,user?.id,user?.permissions,user?.scopes,error]);

  return <section aria-labelledby="access-matrix-title">
    <h2 id="access-matrix-title">API 授权矩阵</h2>
    {canReadApp && (application.projectId ?
      (projectName && <p>所属项目 <strong>{projectName}</strong></p>) :
      <label className="access-matrix-project">授权项目
        <select value={projectId ?? ''} onChange={event => {
          if (allowNavigation()) setChosenProject(event.target.value);
        }}>
          {!projects.length && <option value="">无可读取项目</option>}
          {projects.map(project => <option key={project.id} value={project.id}>{project.name}</option>)}
        </select>
      </label>)}
    {projectId && canReadApp && canReadProject(projectId)
      ? <ProjectPermissionMatrix key={authority} applicationId={application.id} projectId={projectId} reloadApplication={reload}/>
      : <p className="notice">当前会话无法读取应用及目标项目的 API 授权。</p>}
  </section>;
}

function ProjectPermissionMatrix({applicationId,projectId,reloadApplication}:{
  applicationId:string; projectId:string; reloadApplication:()=>void;
}) {
  const [page,setPage] = useState(1);
  const [selected,setSelected] = useState(new Map<string,MatrixCell>());
  const [draft,setDraft] = useState<MatrixDraft>();
  const [accessLost,setAccessLost] = useState(false);
  const [targetsMoved,setTargetsMoved] = useState(false);
  const [receipt,setReceipt] = useState<{result:MatrixReceipt; previousData?:MatrixPage}>();
  const summary = useRef<HTMLSpanElement>(null);
  const basePath = '/applications/' + applicationId + '/permission-matrix';
  const query = new URLSearchParams({projectId,page:String(page),pageSize:String(matrixPageSize)});
  const remote = useRemote<MatrixPage>(basePath + '?' + query);
  const data = !remote.error && !accessLost && remote.data?.applicationId === applicationId &&
    remote.data.projectId === projectId && remote.data.page === page ? remote.data : undefined;
  const picked = [...selected.values()];
  const legacyMove = matrixLegacyMoveCells(picked);
  const readingAfterWrite = !!receipt && !remote.error && remote.data === receipt.previousData;
  const blocked = remote.loading || !data || !!draft || readingAfterWrite;

  function clearAccess() {
    setSelected(new Map());
    setDraft(undefined);
    setReceipt(undefined);
    setTargetsMoved(false);
    setAccessLost(true);
  }

  useEffect(() => {
    if (remote.errorStatus === 401 || remote.errorStatus === 403 || remote.errorStatus === 404) clearAccess();
  },[remote.errorStatus]);

  useEffect(() => {
    if (!data) return;
    if (page > Math.max(1, Math.ceil(data.total / data.pageSize))) {
      setPage(1);
      return;
    }
    setSelected(previous => {
      if (!previous.size) return previous;
      const next = new Map(previous);
      for (const [key,cell] of previous) {
        const environment = data.environments.find(item => item.id === cell.environment.id);
        if (!environment || !environment.canManage ||
            environment.canGrantLegacy !== cell.environment.canGrantLegacy ||
            environment.canGrantRouteMethods !== cell.environment.canGrantRouteMethods) return new Map();
        const api = data.items.find(item => item.id === cell.api.id);
        if (api) {
          const grant = api.grants.find(item => item.environmentId === environment.id);
          if (grant?.id !== cell.grant?.id) next.delete(key);
          else next.set(key,{api,environment,grant});
        }
      }
      return next;
    });
  },[data,page]);

  function toggle(cell:MatrixCell) {
    setSelected(previous => {
      const key = keyForCell(cell);
      const next = new Map(previous);
      if (next.has(key)) next.delete(key);
      else if (next.size < matrixBatchLimit && selectableCell(cell)) next.set(key,cell);
      return next;
    });
  }

  function open(action:MatrixAction,cells:MatrixCell[]) {
    setReceipt(undefined);
    setTargetsMoved(false);
    setDraft({action,cells});
  }

  function refresh() {
    setAccessLost(false);
    setTargetsMoved(false);
    remote.reload();
  }

  return <>
    <div className="filter-bar access-matrix-toolbar">
      <span ref={summary} tabIndex={-1} role="status">已选 {picked.length} 项 / 最多 {matrixBatchLimit} 项</span>
      <button type="button" className="btn primary" disabled={blocked || !picked.length || picked.some(cell => cell.grant || !selectableCell(cell))}
        onClick={() => open('Save',picked)}>批量授权</button>
      <button type="button" className="btn" disabled={blocked || !picked.length ||
        picked.some(cell => !cell.grant || !selectableCell(cell) || !allowedModes(cell).includes(cell.grant.mode))}
        onClick={() => open('SetWindow',picked)}>修改有效期</button>
      <button type="button" className="btn" disabled={blocked || !legacyMove}
        title="选择一个旧版授权和一个允许旧版授权的空单元"
        onClick={() => {if (legacyMove) open('MoveLegacy',legacyMove);}}>移动旧版授权</button>
      <button type="button" className="btn danger" disabled={blocked || !picked.length || picked.some(cell => !cell.grant || !selectableCell(cell))}
        onClick={() => open('Revoke',picked)}>撤销授权</button>
      {!!picked.length && <button type="button" className="text-btn" onClick={() => setSelected(new Map())}>清除选择</button>}
      <button type="button" className="text-btn" disabled={remote.loading || !!draft} onClick={refresh}>重新读取矩阵</button>
    </div>
    {receipt && !remote.loading && !readingAfterWrite && <p className="notice" role="status">
      <span>已提交 {receipt.result.appliedCount} 项授权变更至工作配置。</span> 发布和节点确认后才影响网关。
    </p>}
    {accessLost && <p className="error" role="alert">授权范围或资源已失效，已清除选择和草稿。请重新读取矩阵。</p>}
    {targetsMoved && <p className="notice" role="status">授权记录或操作范围已变化。请在刷新后的矩阵中重新选择。</p>}
    <Feedback {...remote} error={accessLost ? '' : remote.error}/>
    {data && <MatrixGrid data={data} selected={selected} blocked={blocked} toggle={toggle}
      edit={cell => {if (allowedModes(cell).length) open('Save',[cell]);}}/>}
    {!accessLost && (data || page > 1) && <Pagination remote={{data,page,setPage,loading:blocked}}/>}
    {draft && <PermissionMatrixDialog basePath={basePath} applicationId={applicationId} projectId={projectId} draft={draft}
      close={() => setDraft(undefined)} accessLost={clearAccess} restoreFocus={() => summary.current?.focus()}
      targetsChanged={() => {
        setDraft(undefined);
        setSelected(new Map());
        setTargetsMoved(true);
        remote.reload();
      }} applied={result => {
        setDraft(undefined);
        setSelected(new Map());
        setReceipt({result,previousData:remote.data});
        remote.reload();
        reloadApplication();
      }}/>}
  </>;
}
