import {useEffect, useRef, useState} from 'react';
import {apiRequest, ApiError} from '../api/client';
import {Feedback, Table, useRemote, useUnsaved} from '../ui';
import {useWorkspace} from '../Shell';
import {maskAccessKey} from './rotation-state.mjs';
import {
  formalRotationCandidates, formalRotationCommand, formalRotationVerification, formalRotationWindow,
  matchesFormalRotationPreview, matchesFormalRotationReceipt, rotationGuid,
  type RotationCommand, type RotationCredential, type RotationPreview, type RotationReason,
  type RotationReceipt, type RotationStatus, type RotationWindow
} from './formal-rotation-state.mjs';

const reasons: Record<RotationReason,string> = {
  credential_retired:'凭证退役', owner_request:'负责人请求', policy_change:'策略变更'
};
const statusText: Record<RotationStatus,string> = {
  Scheduled:'已登记硬截止',
  ManualRevocationRecorded:'人工撤旧已记录，待核对网关执行',
  AbortPending:'替换钥撤销已记录，等待中止确认',
  Aborted:'已确认中止正式轮换',
  RevocationRecorded:'旧钥撤销已记录，待核对网关执行'
};
function utc(value: string) {
  const date = new Date(value);
  return Number.isFinite(date.valueOf()) ? date.toISOString().slice(0, 19).replace('T', ' ') + ' UTC' : '—';
}
const isRevisionConflict = (error: unknown) => error instanceof ApiError && [409, 412].includes(error.status);
const accessDenied = (status?:number) => status === 401 || status === 403;

export function writeFormalRotationLocation(oldId: string|null, rotationId: string|null = null) {
  const url = new URL(location.href);
  for (const [key,value] of [['formalRotationOld', rotationGuid(oldId)], ['formalRotationId', rotationGuid(rotationId)]] as const) {
    if (value) url.searchParams.set(key, value);
    else url.searchParams.delete(key);
  }
  history.replaceState(history.state, '', url.pathname + url.search + url.hash);
}

function ReasonField({value, onChange}: {value:RotationReason|''; onChange:(value:RotationReason|'')=>void}) {
  return <label>操作理由
    <select aria-label="操作理由" value={value} onChange={event => onChange(event.target.value as RotationReason|'')}>
      <option value="">请选择理由</option>
      {Object.entries(reasons).map(([code,label]) => <option value={code} key={code}>{label}</option>)}
    </select>
  </label>;
}

export function FormalCredentialRotation({old, credentials, environmentName, initialRotationId, onClose, onChanged, onAccessDenied}: {
  old: RotationCredential;
  credentials: RotationCredential[];
  environmentName: string;
  initialRotationId: string|null;
  onClose: () => void;
  onChanged: () => void;
  onAccessDenied: () => void;
}) {
  const {scopeReady} = useWorkspace();
  const root = useRef<HTMLElement>(null);
  const request = useRef<AbortController|undefined>(undefined);
  const command = useRef<RotationCommand|undefined>(undefined);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [rotationId, setRotationId] = useState(initialRotationId);
  const [lookup, setLookup] = useState(initialRotationId || '');
  const [accepted, setAccepted] = useState<RotationReceipt>();
  const receiptRead = useRemote<unknown>(rotationId ? `/credential-rotations/${rotationId}` : null);
  const readReceipt = rotationId && matchesFormalRotationReceipt(receiptRead.data, old, rotationId,
    accepted?.replacementCredentialId, accepted?.hardDeadline) ? receiptRead.data : undefined;
  const currentReceipt = !receiptRead.loading && !receiptRead.error && readReceipt &&
    (!accepted || readReceipt.revision >= accepted.revision) ? readReceipt : undefined;
  const receipt = receiptRead.errorStatus === 404 ? undefined : currentReceipt ?? accepted;
  const receiptMismatch = !!rotationId && !receiptRead.loading && !receiptRead.error && !currentReceipt;
  const verificationRead = useRemote<unknown>(currentReceipt?.status === 'Scheduled'
    ? `/credential-rotations/${rotationId}/verification` : null);
  const deniedRead = accessDenied(receiptRead.errorStatus) || accessDenied(verificationRead.errorStatus);
  const verification = formalRotationVerification(verificationRead.error || verificationRead.loading ? undefined : verificationRead.data,
    rotationId || '');

  useEffect(() => {
    const prior = document.activeElement;
    root.current?.focus();
    return () => {
      request.current?.abort();
      if (prior instanceof HTMLElement && prior.isConnected) prior.focus();
    };
  }, []);
  useEffect(() => { writeFormalRotationLocation(old.id, rotationId); }, [old.id, rotationId]);
  useEffect(() => { if (deniedRead) onAccessDenied(); }, [deniedRead, onAccessDenied]);
  useUnsaved(busy);

  async function run<T>(send:(signal:AbortSignal)=>Promise<T>, apply:(result:T)=>void, rejected?:(error:unknown)=>void) {
    if (!scopeReady || request.current) return;
    const controller = new AbortController();
    request.current = controller;
    setBusy(true);
    setError('');
    try {
      const result = await send(controller.signal);
      if (!controller.signal.aborted) apply(result);
    } catch (failure) {
      if (!controller.signal.aborted) {
        setError(failure instanceof Error ? failure.message : '正式轮换请求失败，请核对回执。');
        if (failure instanceof ApiError && accessDenied(failure.status)) onAccessDenied();
        else rejected?.(failure);
      }
    } finally {
      request.current = undefined;
      if (!controller.signal.aborted) setBusy(false);
    }
  }

  function write(path: string, revision: number, body: unknown, signal: AbortSignal) {
    command.current = formalRotationCommand(command.current, path, revision, body);
    const {etag, idempotencyKey} = command.current;
    return apiRequest<unknown>(path, {method:'POST', body, etag, idempotencyKey, signal});
  }

  function accept(result: RotationReceipt) {
    command.current = undefined;
    setAccepted(result);
    setRotationId(result.rotationId);
    setLookup(result.rotationId);
    receiptRead.reload();
    onChanged();
  }

  function lookupReceipt() {
    const id = rotationGuid(lookup.trim());
    if (!id || busy) return;
    setError('');
    if (id !== rotationId) {
      setAccepted(undefined);
      setRotationId(id);
      command.current = undefined;
    } else {
      receiptRead.reload();
      verificationRead.reload();
    }
  }

  function readPreview(window: RotationWindow, apply:(preview:RotationPreview)=>void) {
    void run(signal => apiRequest<unknown>(`/credentials/${old.id}/formal-rotation/preview`, {
      method:'POST', body:window, etag:`"${old.revision}"`, signal
    }), result => {
      if (!matchesFormalRotationPreview(result, old, window)) throw new Error('预览与当前旧凭证、修订或时间窗口不匹配。');
      apply(result);
    }, failure => { if (isRevisionConflict(failure)) onChanged(); });
  }

  function confirm(window: RotationWindow, preview: RotationPreview, reason: RotationReason, onConflict:()=>void) {
    void run(signal => write(`/credentials/${old.id}/formal-rotation/confirm`, preview.credentialRevision,
      {window, previewHash:preview.previewHash, confirmAutomaticRevocation:true, reason}, signal), result => {
      if (!matchesFormalRotationReceipt(result, old, null, window.replacementCredentialId, window.hardDeadline))
        throw new Error('确认回执与当前旧凭证、替换钥或硬截止不匹配。');
      accept(result);
    }, failure => {
      if (isRevisionConflict(failure)) { onConflict(); onChanged(); }
    });
  }

  function decide(action:'complete'|'abort', reason:RotationReason, onConflict:()=>void) {
    if (currentReceipt?.status !== 'Scheduled' || verification.result?.state === 'Closed' ||
        action === 'complete' && !verification.complete) return;
    void run(signal => write(`/credential-rotations/${currentReceipt.rotationId}/${action}`, currentReceipt.revision,
      {confirm:true, reason}, signal), result => {
      if (!matchesFormalRotationReceipt(result, old, currentReceipt.rotationId, currentReceipt.replacementCredentialId,
        currentReceipt.hardDeadline) || result.revision <= currentReceipt.revision)
        throw new Error('返回的决定回执与当前轮换不匹配。');
      accept(result);
    }, failure => {
      if (isRevisionConflict(failure)) { onConflict(); receiptRead.reload(); verificationRead.reload(); }
    });
  }

  if (deniedRead) return null;
  return <section className="card wp4-rotation" ref={root} tabIndex={-1} aria-label="正式轮换">
    <div className="ob-card-head">
      <div><h2>正式轮换</h2><small>{environmentName} · 旧 Key {maskAccessKey(old.accessKey)}</small></div>
      <button type="button" className="btn" disabled={busy} onClick={onClose}>收起正式轮换</button>
    </div>
    <div className="filter-bar">
      <label>轮换 ID <input value={lookup} maxLength={36} onChange={event => setLookup(event.target.value)} disabled={busy}/></label>
      <button type="button" className="btn" disabled={busy || !rotationGuid(lookup.trim())} onClick={lookupReceipt}>查询回执</button>
      {rotationId && <button type="button" className="text-btn" disabled={busy} onClick={() => {
        setRotationId(null); setLookup(''); setAccepted(undefined); setError(''); command.current = undefined;
      }}>返回预览</button>}
    </div>
    {error && <div className="error" role="alert">{error}</div>}
    {rotationId ? <div className="details">
      <Feedback {...receiptRead}/>
      {receiptMismatch && <div className="error" role="alert">回执与当前旧凭证、环境或已知修订不匹配。</div>}
      {receipt && <RotationReceiptView key={receipt.rotationId + ':' + receipt.revision} receipt={receipt} busy={busy || !scopeReady}
        current={!!currentReceipt} verification={verification} verificationRead={verificationRead}
        refresh={() => { receiptRead.reload(); verificationRead.reload(); }} decide={decide}/>}
    </div> : old.status === 'Active'
      ? <RotationSchedule key={old.revision} old={old} credentials={credentials} busy={busy || !scopeReady} readPreview={readPreview} confirm={confirm}/>
      : <p className="notice">可按轮换 ID 查询该旧凭证的正式轮换回执。</p>}
  </section>;
}

function RotationSchedule({old, credentials, busy, readPreview, confirm}: {
  old: RotationCredential; credentials: RotationCredential[]; busy: boolean;
  readPreview: (window:RotationWindow, apply:(result:RotationPreview)=>void)=>void;
  confirm: (window:RotationWindow, preview:RotationPreview, reason:RotationReason, onConflict:()=>void)=>void;
}) {
  const [replacementId, setReplacementId] = useState('');
  const [overlap, setOverlap] = useState('');
  const [deadline, setDeadline] = useState('');
  const [reason, setReason] = useState<RotationReason|''>('');
  const [checked, setChecked] = useState(false);
  const [preview, setPreview] = useState<RotationPreview>();
  const candidates = formalRotationCandidates(old, credentials);
  const replacement = candidates.find(item => item.id === replacementId);
  const window = formalRotationWindow(replacement, overlap, deadline);
  const reviewed = !!window && matchesFormalRotationPreview(preview, old, window) ? preview : undefined;
  const mayPreview = !!window && Number.isSafeInteger(old.revision) && old.revision > 0;
  function clearReview() { setPreview(undefined); setChecked(false); }

  return <div className="details">
    <fieldset className="form-grid" disabled={busy}>
      <label>替换凭证
        <select aria-label="替换凭证" value={replacementId} onChange={event => { setReplacementId(event.target.value); clearReview(); }}>
          <option value="">选择同环境已绑定凭证</option>
          {candidates.map(item => <option key={item.id} value={item.id}>{maskAccessKey(item.accessKey)}</option>)}
        </select>
      </label>
      {replacement && <p>替换钥有效期<br/>{utc(replacement.validFrom)} 至 {utc(replacement.expiresAt)}</p>}
      <label>重叠窗口结束（UTC）
        <input type="datetime-local" value={overlap} onChange={event => { setOverlap(event.target.value); clearReview(); }}/>
      </label>
      <label>硬截止（UTC）
        <input type="datetime-local" value={deadline} onChange={event => { setDeadline(event.target.value); clearReview(); }}/>
      </label>
    </fieldset>
    {candidates.length === 0 && <p className="notice">此应用在目标环境中暂无可选的有效替换凭证。</p>}
    {replacement && overlap && deadline && !window && <p className="error" role="alert">重叠结束须在替换钥生效后且不晚于硬截止。硬截止须早于替换钥过期。</p>}
    <div className="actions">
      <button type="button" className="btn" disabled={busy || !mayPreview} onClick={() => {
        if (window) { clearReview(); readPreview(window, setPreview); }
      }}>预览正式轮换</button>
    </div>
    {reviewed && window && <section className="details" aria-label="正式轮换影响">
      <h3>确认影响与硬截止</h3>
      <p>当前发布配置 {reviewed.configVersion} · 序列 {reviewed.deploymentSequence} · {reviewed.targets.length} 个网关实例</p>
      <p>重叠结束 {utc(reviewed.overlapUntil)} · 硬截止 {utc(reviewed.hardDeadline)}</p>
      <Table rows={reviewed.targets} columns={[[ 'nodeId', '目标节点'], ['instanceId', '进程实例']]}/>
      <div className="notice">
        <p>到达硬截止时，系统停止旧钥准入并触发自动撤销，即使替换钥尚未完成验证。仍使用旧钥的调用可能中断。</p>
        <p>安全租约无法续签时，同环境依赖安全状态的其他 ApiKey 或 JWT 请求也可能暂时收到 503。</p>
        {reviewed.lastWorkingCredentialAtDeadline && <p>截止时工作配置中缺少其他有效凭证。</p>}
        {reviewed.lastPublishedCredentialAtDeadline && <p>截止时已发布配置中缺少其他有效凭证。</p>}
      </div>
      <fieldset className="form-grid" disabled={busy}>
        <ReasonField value={reason} onChange={setReason}/>
        <label className="checkline">
          <input type="checkbox" checked={checked} onChange={event => setChecked(event.target.checked)}/>
          我已核对影响并确认到达硬截止时自动撤销旧钥
        </label>
      </fieldset>
      <button type="button" className="btn danger" disabled={busy || !checked || !reason}
        onClick={() => { if (checked && reason) confirm(window, reviewed, reason, clearReview); }}>确认登记正式轮换</button>
    </section>}
  </div>;
}

function RotationReceiptView({receipt, busy, current, verification, verificationRead, refresh, decide}: {
  receipt: RotationReceipt; busy: boolean; current:boolean;
  verification: ReturnType<typeof formalRotationVerification>;
  verificationRead: {loading:boolean;error:string;updated?:Date};
  refresh:()=>void;
  decide: (action:'complete'|'abort', reason:RotationReason, onConflict:()=>void)=>void;
}) {
  const [action, setAction] = useState<'complete'|'abort'>();
  const [checked, setChecked] = useState(false);
  const [reason, setReason] = useState<RotationReason|''>('');
  const canDecide = current && receipt.status === 'Scheduled';
  const canComplete = canDecide && verification.complete;
  const proof = verification.result;
  const canAbort = canDecide && proof?.state !== 'Closed';
  const proofText = verification.complete ? '全部当前网关实例已核验替换钥请求'
    : proof?.state === 'AwaitingProof' || proof?.state === 'Verified' ? '等待完整的替换钥请求证明'
    : proof?.state === 'Closed' ? '验证窗口已关闭' : '暂无法核验替换钥请求';
  const tabs = [
    ['nodeId','目标节点'], ['instanceId','进程实例'], ['proofLabel','替换钥请求证明']
  ] as [string,string][];
  function select(next?:'complete'|'abort') { setAction(next); setReason(''); setChecked(false); }

  return <section aria-label="正式轮换回执">
    <h3>{current ? '正式轮换回执' : '最近收到的正式轮换回执'}</h3>
    <div className="notice" role="status">
      <p>{statusText[receipt.status]} · 修订 {receipt.revision}</p>
      <p>硬截止 {utc(receipt.hardDeadline)}</p>
      <p>轮换 ID <code>{receipt.rotationId}</code></p>
      {receipt.revocationOperationId && <p>旧钥撤销操作 <code>{receipt.revocationOperationId}</code></p>}
      {receipt.abortRevocationOperationId && <p>替换钥撤销操作 <code>{receipt.abortRevocationOperationId}</code></p>}
      {receipt.status === 'AbortPending' && <p>等待撤销确认期间，原硬截止继续生效。</p>}
    </div>
    <button type="button" className="btn" disabled={busy} onClick={refresh}>刷新回执与验证</button>
    {canDecide && <section className="details" aria-label="替换钥验证">
      <h3>替换钥验证</h3>
      <Feedback {...verificationRead}/>
      {!verificationRead.loading && <p role="status">{proofText}</p>}
      {proof?.configVersion != null && <p>配置 {proof.configVersion} · 序列 {proof.deploymentSequence ?? '—'}</p>}
      {!!proof?.targets.length && <Table rows={proof.targets.map(target => ({...target,
        proofLabel:target.state === 'Verified' ? '有证明' : '待核验'}))} columns={tabs}/>}
      <div className="actions">
        <button type="button" className="btn danger" disabled={busy || !canComplete} onClick={() => select('complete')}>人工完成并撤销旧钥</button>
        <button type="button" className="btn" disabled={busy || !canAbort} onClick={() => select('abort')}>请求中止轮换</button>
      </div>
      {action && <section className="details" aria-label={action === 'complete' ? '确认人工撤旧' : '确认中止请求'}>
        <p className="notice">{action === 'complete' ? '确认后将记录旧钥撤销。网关执行情况需继续核对。'
          : '确认后先撤销替换钥。中止确认前，原硬截止继续生效。'}</p>
        <fieldset className="form-grid" disabled={busy}>
          <ReasonField value={reason} onChange={setReason}/>
          <label className="checkline">
            <input type="checkbox" checked={checked} onChange={event => setChecked(event.target.checked)}/>
            {action === 'complete' ? '我确认提前撤销旧钥' : '我确认撤销替换钥并请求中止'}
          </label>
        </fieldset>
        <button type="button" className="btn danger" disabled={busy || !checked || !reason || !(action === 'complete' ? canComplete : canAbort)}
          onClick={() => { if (checked && reason) decide(action, reason, () => select()); }}>提交{action === 'complete' ? '人工撤旧' : '中止请求'}</button>
        <button type="button" className="text-btn" disabled={busy} onClick={() => select()}>取消</button>
      </section>}
    </section>}
  </section>;
}
