type MappingProps = {
 operations:any[];
 targets:any[];
 revisionReview?:string[];
 onToggle:(operation:any,checked:boolean)=>void;
 onEdit:(operation:any)=>void;
 disabled?:boolean;
};

export function ImportOperationMapping({operations,targets,revisionReview=[],onToggle,onEdit,disabled=false}:MappingProps){
 const byOperation=new Map(targets.map(target=>[target.operationId,target]));
 const review=new Set(revisionReview);
 return <div className="comparison-table"><table>
  <thead><tr><th>选择 / 支持</th><th>Operation / 方法</th><th>路径与限制</th><th>映射目标</th></tr></thead>
  <tbody>{operations.map((operation,i)=>{
   const target=byOperation.get(operation.operationId);
   return <tr key={operation.operationId+':'+i}>
    <td><label><input type="checkbox" disabled={disabled||!operation.supported} checked={!!target}
     aria-label={'选择 Operation '+operation.operationId} onChange={e=>onToggle(operation,e.target.checked)}/>
     {operation.supported?'可导入':'不可导入'}</label></td>
    <td><strong>{operation.operationId}</strong><br/>{operation.method}<br/><small>{operation.summary}</small></td>
    <td><code>{operation.path}</code>{operation.warnings?.map((warning:string,j:number)=><p className="notice" key={j}>{warning}</p>)}
     <small>{operation.parameterCount??0} 个参数 · {operation.schemaCount??0} 个 Schema</small></td>
    <td>{target&&<p>{target.existingVersionId?'草稿版本 '+target.existingVersionId:target.apiId?'已有 API '+target.apiId:target.newApiName} · {target.version}</p>}
     {target&&review.has(operation.operationId)&&<p className="notice">请核对版本修订（上次 {target.expectedRevision}）
      {target.existingRouteId&&<>及路由修订（上次 {target.expectedRouteRevision}）</>}，再保存映射。</p>}
     <button type="button" className="text-btn" disabled={disabled||!operation.supported||!target} onClick={()=>onEdit(operation)}>映射目标</button></td>
   </tr>;
  })}</tbody>
 </table></div>;
}
