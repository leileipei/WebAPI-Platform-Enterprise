import {Feedback, Table, useRemote} from '../ui';

type ActivePreview = {
  previewId:string;
  environmentName:string;
  clusterName:string;
  bundleHash:string;
  createdAt:string;
  expiresAt:string;
};

type Props = {
  projectId:string;
  revision:number;
  now:number;
  busy:boolean;
  revokeError:string;
  revoke:(id:string)=>Promise<void>;
};

export function ActiveImportPreviews({projectId, revision, now, busy, revokeError, revoke}:Props) {
  const list = useRemote<ActivePreview[]>(`/openapi/import-previews?projectId=${projectId}`, 0, false, true, revision);
  const rows = (list.data ?? []).filter(item => Date.parse(item.expiresAt) > now).map(item => ({
    id:item.previewId,
    previewId:item.previewId,
    environmentName:item.environmentName,
    clusterName:item.clusterName,
    hash:item.bundleHash.slice(0, 12),
    created:new Date(item.createdAt).toLocaleString(),
    expires:new Date(item.expiresAt).toLocaleString()
  }));

  return <section className="card details">
    <h3 className="section-title">本项目中我的有效预览</h3>
    <button type="button" className="btn" disabled={list.loading} onClick={list.reload}>刷新预览列表</button>
    <Feedback {...list}/>
    {revokeError && <p role="alert" className="error">{revokeError}</p>}
    {list.data && !list.error && <Table rows={rows} columns={[
      ['previewId','预览 ID'], ['environmentName','环境'], ['clusterName','目标集群'],
      ['hash','来源包 SHA 前 12 位'], ['created','创建时间'], ['expires','有效至']
    ]} actions={row => <button type="button" className="text-btn" disabled={busy || list.loading}
      onClick={() => void revoke(row.previewId)}>撤销</button>}/>}
  </section>;
}
