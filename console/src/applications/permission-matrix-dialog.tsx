import {useCallback,useEffect,useId,useRef,useState,type KeyboardEvent} from 'react';
import {apiRequest,ApiError} from '../api/client';
import {withPage} from '../api/paging.mjs';
import {recoveryFocusTarget,tabFocusTarget} from '../editor-focus.mjs';
import {Feedback,useRemote,useUnsaved} from '../ui';
import {
  matchesMatrixPreview,matrixChange,matrixLegacyMoveCells,matrixPageSize,permissionWindow,toLocalInput
} from './permission-matrix-model.mjs';
import type {
  MatrixCell,MatrixCommitRequest,MatrixDraft,MatrixMode,MatrixPage,MatrixPreview,MatrixReceipt,MatrixRequest
} from './permission-matrix-types';
import {
  allowedModes,keyForCell,matrixCellLabel,matrixModeLabel,matrixMoveLabel,MatrixImpacts,selectableCell
} from './permission-matrix-view';
import {
  readRouteMethodOptions,routeMethodSelection,toggleRouteMethod,type RouteMethodGrant,type RouteMethodOptions
} from './route-method-permissions.mjs';

type Confirmable = {request:MatrixRequest; impact:MatrixPreview};
type PendingCommit = {body:MatrixCommitRequest; key:string};

function denied(status:number|undefined) {
  return status === 401 || status === 403 || status === 404;
}

export function PermissionMatrixDialog({basePath,applicationId,projectId,draft,close,applied,accessLost,targetsChanged,restoreFocus}: {
  basePath:string; applicationId:string; projectId:string; draft:MatrixDraft;
  close:()=>void; applied:(receipt:MatrixReceipt)=>void; accessLost:()=>void; targetsChanged:()=>void; restoreFocus:()=>void;
}) {
  const {action} = draft;
  const [cells,setCells] = useState(draft.cells);
  const [modeEdits,setModeEdits] = useState<Record<string,MatrixMode>>({});
  const [methodEdits,setMethodEdits] = useState<Record<string,RouteMethodGrant[]>>({});
  const [options,setOptions] = useState<Record<string,RouteMethodOptions|null>>({});
  const [routeVersion,setRouteVersion] = useState(0);
  const [validFrom,setValidFrom] = useState<string>();
  const [expiresAt,setExpiresAt] = useState<string>();
  const [editStart,setEditStart] = useState(false);
  const [editEnd,setEditEnd] = useState(false);
  const [confirmable,setConfirmable] = useState<Confirmable>();
  const [confirmProduction,setConfirmProduction] = useState(false);
  const [pending,setPending] = useState<PendingCommit>();
  const [revisionRequired,setRevisionRequired] = useState(false);
  const [dirty,setDirty] = useState(false);
  const [discard,setDiscard] = useState(false);
  const [busy,setBusy] = useState(false);
  const [error,setError] = useState('');
  const [notice,setNotice] = useState('');
  const [defaultStart] = useState(() => toLocalInput(new Date().toISOString()));

  const requestLock = useRef(false);
  const root = useRef<HTMLDivElement>(null);
  const heading = useRef<HTMLHeadingElement>(null);
  const titleId = useId();
  const dismissPending = busy || !!pending;
  const requiresProduction = !!confirmable && (confirmable.impact.requiresProductionConfirmation ||
    confirmable.impact.items.some(item => item.isProduction) || cells.some(cell => cell.environment.isProduction));
  const moving = action === 'MoveLegacy';
  const legacyMove = moving ? matrixLegacyMoveCells(cells) : undefined;
  const needsStart = moving || action === 'Save' || action === 'SetWindow' && editStart;
  const needsEnd = moving || action === 'Save' || action === 'SetWindow' && editEnd;
  const singleGrant = cells.length === 1 ? cells[0].grant : undefined;
  const windowGrant = legacyMove?.[0].grant ?? singleGrant;
  const startValue = validFrom ?? (windowGrant ? toLocalInput(windowGrant.validFrom) : defaultStart);
  const endValue = expiresAt ?? toLocalInput(windowGrant?.expiresAt);
  const title = confirmable ? '授权影响预览' : moving ? '移动旧版授权' : action === 'SetWindow' ? '修改有效期' : action === 'Revoke' ? '撤销授权'
    : cells.length > 1 ? '批量授权' : singleGrant ? '编辑授权' : '新增授权';

  function modeFor(cell:MatrixCell) {
    const available = allowedModes(cell);
    const savedMode = cell.grant?.mode;
    return modeEdits[keyForCell(cell)] ?? (savedMode && available.includes(savedMode) ? savedMode : available[0]);
  }

  function methodChoices(cell:MatrixCell) {
    return methodEdits[keyForCell(cell)] ?? cell.grant?.routeMethods ?? [];
  }

  const canPreview = !busy && !revisionRequired && !discard && (!needsStart || !!startValue) && (!moving || !!legacyMove) &&
    (action !== 'SetWindow' || editStart || editEnd) && (action !== 'Save' || cells.every(cell => {
      const mode = modeFor(cell);
      if (!allowedModes(cell).includes(mode)) return false;
      if (mode === 'LegacyApi') return true;
      const chosen = methodChoices(cell);
      return chosen.length > 0 && routeMethodSelection(chosen,options[keyForCell(cell)]).length === chosen.length;
    }));

  const setVerifiedOptions = useCallback((key:string,value:RouteMethodOptions|null) => {
    setOptions(previous => previous[key] === value ? previous : {...previous,[key]:value});
  },[]);

  function setMethods(key:string,methods:RouteMethodGrant[]) {
    setMethodEdits(previous => ({...previous,[key]:methods}));
  }

  function requireDiscard() {
    if (dismissPending) return;
    if (dirty || confirmable) setDiscard(true);
    else close();
  }

  function focusables(container:HTMLElement) {
    return [...container.querySelectorAll<HTMLElement>(
      'input:not(:disabled),select:not(:disabled),button:not(:disabled),a[href]'
    )].filter(item => item.tabIndex >= 0 && item.getClientRects().length > 0);
  }

  useEffect(() => {
    const previous = document.activeElement as HTMLElement|null;
    return () => {
      if (previous?.isConnected && !previous.matches(':disabled')) previous.focus();
      else restoreFocus();
    };
  },[]);
  useEffect(() => {heading.current?.focus();},[title]);
  useEffect(() => {
    if (!root.current) return;
    const trap = root.current.querySelector<HTMLElement>('[role="alertdialog"]') ?? root.current;
    if (discard || !trap.contains(document.activeElement) || (document.activeElement as HTMLButtonElement)?.disabled)
      recoveryFocusTarget(focusables(trap),document.activeElement,heading.current ?? trap)?.focus();
  },[discard,busy,revisionRequired,pending]);

  useUnsaved(dirty || !!confirmable || dismissPending, () => {
    if (dismissPending) setError('提交结果尚待核对，请留在当前对话框。');
    else setDiscard(true);
  });

  function dialogKey(event:KeyboardEvent<HTMLDivElement>) {
    if (event.key === 'Escape') {
      event.preventDefault();
      if (discard) setDiscard(false);
      else requireDiscard();
    }
    if (event.key === 'Tab') {
      const trap = event.currentTarget.querySelector<HTMLElement>('[role="alertdialog"]') ?? event.currentTarget;
      const next = tabFocusTarget(focusables(trap),document.activeElement,event.shiftKey,heading.current ?? trap);
      if (next) {
        event.preventDefault();
        next.focus();
      }
    }
  }

  function changeField(update:()=>void) {
    update();
    setNotice('');
    setDirty(true);
  }

  function resetPreview() {
    setConfirmable(undefined);
    setConfirmProduction(false);
    if (confirmable) setOptions({});
  }

  function recover(failure:unknown) {
    if (!(failure instanceof ApiError)) {
      setError('请求失败，请重试。');
      return;
    }
    if (denied(failure.status)) {
      accessLost();
      return;
    }
    if (failure.status === 412) {
      resetPreview();
      setRevisionRequired(true);
      setError('授权修订已变化。请读取最新修订并重新核对影响。');
      return;
    }
    if (failure.status === 409) {
      resetPreview();
      setNotice('');
      if (failure.code?.includes('route') && cells.some(cell => modeFor(cell) === 'RouteMethod')) {
        setOptions({});
        setMethodEdits(Object.fromEntries(cells.map(cell => [keyForCell(cell),[]])));
        setRouteVersion(value => value + 1);
      }
      if (failure.code === 'permission_already_exists' || failure.code === 'legacy_permission_cutover')
        setRevisionRequired(true);
      setError('预览需要更新。' + failure.message + ' 请重新预览。');
      return;
    }
    if (failure.code === 'invalid_route_method_permissions' && action === 'Save') {
      setOptions({});
      setRouteVersion(value => value + 1);
    }
    setError(failure.message);
  }

  async function run(operation:()=>Promise<void>) {
    if (requestLock.current) return;
    requestLock.current = true;
    setBusy(true);
    setError('');
    try {await operation();}
    catch (failure) {recover(failure);}
    finally {
      requestLock.current = false;
      setBusy(false);
    }
  }

  function preview() {
    if (!canPreview) return;
    void run(async () => {
      const changes = legacyMove ? [matrixChange(legacyMove[0],'MoveLegacy',permissionWindow(windowGrant,startValue,endValue),{
        destination:legacyMove[1]
      })] : cells.map(cell => {
        if (action === 'Revoke') return matrixChange(cell,action);
        const start = needsStart ? startValue : undefined;
        const end = needsEnd ? endValue : undefined;
        const window = permissionWindow(cell.grant,start,end);
        if (action === 'SetWindow') return matrixChange(cell,action,window);
        return matrixChange(cell,'Save',window,{
          mode:modeFor(cell),routeMethods:routeMethodSelection(methodChoices(cell),options[keyForCell(cell)])
        });
      });
      const request = {projectId,changes};
      const impact = await apiRequest<MatrixPreview>(basePath + '/preview',{method:'POST',body:request});
      if (!impact.previewToken || !matchesMatrixPreview(changes,impact.items)) {
        setError('预览返回的授权单元与当前请求不符。请重新读取。');
        return;
      }
      setNotice('');
      setConfirmable({request,impact});
    });
  }

  function commit() {
    if (busy || discard || !confirmable || requiresProduction && !confirmProduction) return;
    const attempt = pending ?? {
      body:{...confirmable.request,previewToken:confirmable.impact.previewToken,confirmProduction},
      key:crypto.randomUUID()
    };
    void run(async () => {
      try {
        const receipt = await apiRequest<MatrixReceipt>(basePath + '/commit',{method:'POST',body:attempt.body,idempotencyKey:attempt.key});
        if (receipt.appliedCount !== attempt.body.changes.length || receipt.activation !== 'WorkingConfiguration') {
          throw new Error('无法核对提交回执。');
        }
        applied(receipt);
      } catch (failure) {
        if (!(failure instanceof ApiError) || failure.status === 408 || failure.status >= 500) {
          setPending(attempt);
          setError('提交结果尚未确认。请使用原请求和幂等键核对提交结果。');
          return;
        }
        setPending(undefined);
        resetPreview();
        throw failure;
      }
    });
  }

  function acceptLatestRevision() {
    void run(async () => {
      const query = new URLSearchParams({projectId,pageSize:String(matrixPageSize)});
      const path = basePath + '?' + query;
      const latest = new Map<string,MatrixCell>();
      for (let page = 1; latest.size < cells.length; page++) {
        const result = await apiRequest<MatrixPage>(withPage(path,page));
        if (result.applicationId !== applicationId || result.projectId !== projectId || result.page !== page) {
          accessLost();
          return;
        }
        for (const cell of cells) {
          const api = result.items.find(item => item.id === cell.api.id);
          const environment = result.environments.find(item => item.id === cell.environment.id);
          if (api && environment) latest.set(keyForCell(cell),{
            api,environment,grant:api.grants.find(item => item.environmentId === environment.id)
          });
        }
        if (result.pageSize * page >= result.total || !result.items.length) break;
      }
      const refreshed = cells.map(cell => latest.get(keyForCell(cell)));
      if (refreshed.some((cell,index) => !cell || !selectableCell(cell) || cell.grant?.id !== cells[index].grant?.id ||
          action === 'Save' && !allowedModes(cell).includes(modeFor(cells[index])) ||
          action === 'SetWindow' && (!cell.grant || !allowedModes(cell).includes(cell.grant.mode))) ||
          moving && !matrixLegacyMoveCells(refreshed as MatrixCell[])) {
        targetsChanged();
        return;
      }
      setCells(refreshed as MatrixCell[]);
      setOptions({});
      setRouteVersion(value => value + 1);
      setRevisionRequired(false);
      setNotice('已读取最新授权修订。请重新预览并检查变更前后的值。' +
        (action === 'SetWindow' ? '如路由修订也已变化，请编辑相应授权并刷新方法选择。' : ''));
    });
  }

  return <div className="overlay"><div ref={root} className="dialog access-matrix-dialog" role="dialog" aria-modal="true"
    aria-labelledby={titleId} aria-busy={busy} onKeyDown={dialogKey}>
    <header><h2 id={titleId} ref={heading} tabIndex={-1}>{title}</h2>
      <button type="button" className="text-btn" disabled={dismissPending || discard} onClick={requireDiscard}>取消</button>
    </header>
    {error && <p className="error" role="alert">{error}</p>}
    {notice && <p className="notice" role="status">{notice}</p>}
    {discard && <div className="notice" role="alertdialog" aria-label="放弃未提交的授权修改">
      <p>确认放弃这次授权修改？</p>
      <button type="button" className="btn" onClick={() => setDiscard(false)}>继续编辑</button>
      <button type="button" className="btn danger" onClick={close}>确认放弃</button>
    </div>}
    <fieldset className="access-matrix-dialog-content" disabled={busy || discard || !!pending || revisionRequired}>
      {confirmable ? <>
        <p className="notice">提交将更新工作配置。发布和节点确认后才影响网关。</p>
        <p className="muted">预览有效至 <time dateTime={confirmable.impact.expiresAt}>{new Date(confirmable.impact.expiresAt).toLocaleString('zh-CN')}</time></p>
        <MatrixImpacts items={confirmable.impact.items} cells={cells} moving={moving}/>
        {requiresProduction && <div className="notice access-matrix-production">
          <label className="checkline"><input type="checkbox" checked={confirmProduction}
            onChange={event => setConfirmProduction(event.target.checked)}/>我确认本次变更涉及生产环境</label>
        </div>}
      </> : <>
        {action === 'Revoke' ? <p className="notice">下列单元的工作授权将被撤销。请先核对变更影响。</p> : <div className="access-matrix-window">
          {action === 'SetWindow' && <label className="checkline">
            <input type="checkbox" checked={editStart} onChange={event => changeField(() => setEditStart(event.target.checked))}/>修改生效时间
          </label>}
          {action === 'SetWindow' && <label className="checkline">
            <input type="checkbox" checked={editEnd} onChange={event => changeField(() => setEditEnd(event.target.checked))}/>修改失效时间
          </label>}
          <label>授权生效时间
            <input aria-label="授权生效时间" type="datetime-local" step="1" value={needsStart ? startValue : ''} disabled={!needsStart}
              onChange={event => changeField(() => setValidFrom(event.target.value))}/>
          </label>
          <label>授权失效时间
            <input aria-label="授权失效时间" type="datetime-local" step="1" value={needsEnd ? endValue : ''} disabled={!needsEnd}
              onChange={event => changeField(() => setExpiresAt(event.target.value))}/>
            <small>留空表示无固定失效时间</small>
          </label>
        </div>}
        {cells.map((cell,index) => {
          const key = keyForCell(cell);
          const label = matrixCellLabel(cell);
          return <fieldset key={key} className="promotion-route access-matrix-target">
            <legend>{moving ? matrixMoveLabel(cell,index) : label}</legend>
            {cell.environment.isProduction && <span className="badge red">生产</span>}
            {action !== 'Save' ? <p>{cell.grant ? matrixModeLabel(cell.grant.mode) + ' · 当前修订 ' + cell.grant.revision : '未授权'}</p> : <>
              <label>授权模式
                <select aria-label={'授权模式 ' + label} value={modeFor(cell)}
                  onChange={event => {
                    const mode = event.target.value as MatrixMode;
                    setVerifiedOptions(key,null);
                    changeField(() => setModeEdits(previous => ({...previous,[key]:mode})));
                  }}>
                  {allowedModes(cell).map(mode => <option key={mode} value={mode}>{matrixModeLabel(mode)}</option>)}
                </select>
              </label>
              {modeFor(cell) === 'RouteMethod' && <MatrixRouteOptions key={routeVersion} applicationId={applicationId} cell={cell}
                methods={methodChoices(cell)} onVerify={setVerifiedOptions} onDenied={accessLost}
                onChange={methods => changeField(() => setMethods(key,methods))}/>}
            </>}
          </fieldset>;
        })}
      </>}
    </fieldset>
    <footer>
      {revisionRequired && <button type="button" className="btn" disabled={busy || discard} onClick={acceptLatestRevision}>读取最新授权修订</button>}
      {confirmable ? <>
        {!pending && <button type="button" className="btn" disabled={busy || discard} onClick={resetPreview}>返回编辑</button>}
        <button type="button" className="btn primary" disabled={busy || discard || requiresProduction && !confirmProduction} onClick={commit}>
          {pending ? '核对提交结果' : '提交工作配置'}
        </button>
      </> : <button type="button" className="btn primary" disabled={!canPreview} onClick={preview}>预览影响</button>}
      {busy && <span role="status">正在等待服务端响应…</span>}
    </footer>
  </div></div>;
}

function MatrixRouteOptions({applicationId,cell,methods,onVerify,onDenied,onChange}:{
  applicationId:string; cell:MatrixCell; methods:RouteMethodGrant[];
  onVerify:(key:string,value:RouteMethodOptions|null)=>void; onDenied:()=>void; onChange:(methods:RouteMethodGrant[])=>void;
}) {
  const apiId = cell.api.id;
  const environmentId = cell.environment.id;
  const key = keyForCell(cell);
  const query = new URLSearchParams({apiId,environmentId});
  const remote = useRemote<unknown>('/applications/' + applicationId + '/route-method-options?' + query);
  const verified = !remote.loading && !remote.error ? readRouteMethodOptions(remote.data,environmentId,apiId) : null;
  const chosen = routeMethodSelection(methods,verified);
  useEffect(() => {onVerify(key,verified);},[key,onVerify,verified]);
  useEffect(() => {if (denied(remote.errorStatus)) onDenied();},[remote.errorStatus,onDenied]);

  return <section aria-label="目标路由及方法">
    <h3>目标路由及方法</h3>
    <button type="button" className="text-btn" disabled={remote.loading} onClick={() => {
      onVerify(key,null);
      onChange([]);
      remote.reload();
    }}>重新读取目标路由</button>
    <Feedback {...remote}/>
    {!verified && remote.updated && !remote.error && !remote.loading && <p className="error" role="alert">目标路由与当前 API 或环境不匹配。</p>}
    {verified && methods.length !== chosen.length && <p className="notice">部分现有方法的修订已变化，请重新选择。</p>}
    {verified?.routes.length === 0 && <p className="notice">该环境和 API 暂无可授权的路由方法。</p>}
    {verified?.routes.map(route => <fieldset className="promotion-route" key={route.routeId}>
      <legend>{route.path} · 授权修订 {route.authorizationRevision}</legend>
      <div className="access-matrix-methods">
        {route.methods.map(method => <label className="checkline" key={method}>
          <input type="checkbox" aria-label={method + ' ' + route.path}
            checked={chosen.some(grant => grant.routeId === route.routeId && grant.methods.includes(method))}
            onChange={event => onChange(toggleRouteMethod(chosen,route,method,event.target.checked))}/>{method}
        </label>)}
      </div>
    </fieldset>)}
  </section>;
}
