import {useId,useState} from 'react';
import {permissionGroups,type DisplayPermission,type Permission} from './permission-display.mjs';
import {permissionGuidance} from './permission-guidance';

const columns=[['read','查看'],['configure','创建与配置'],['release','发布与审批'],['manage','管理与操作'],['other','其他权限']];
type Selection={
 value:Record<string,boolean>;
 readOnly?:boolean;
 onChange?:(code:string,checked:boolean)=>void;
};

function PermissionChoice({item,value,readOnly,onChange}:Selection&{item:DisplayPermission}){
 const hintId=useId();
 const guidance=permissionGuidance[item.code];
 const description=item.description==='源文档平台功能权限'?undefined:item.description;
 return <label className="permission-choice" title={item.description}>
  <input type="checkbox" aria-label={item.label+' · '+item.code} aria-describedby={guidance||description?hintId:undefined}
   checked={!!value[item.code]} disabled={readOnly} onChange={event=>onChange?.(item.code,event.target.checked)}/>
  <span>{item.label}<code>{item.code}</code>
   {(guidance||description)&&<span id={hintId}>
    {description&&<small className="block">{description}</small>}
    {guidance?.impact&&<small className="block"><strong>敏感操作</strong> · {guidance.impact}</small>}
    {guidance?.alsoRequires&&<small className="block">{guidance.operation}另需 {
     guidance.alsoRequires.map(code=>code+(value[code]?'（本角色已选）':'（本角色未选）')).join('、')
    }。</small>}
    {guidance?.condition&&<small className="block">{guidance.condition}</small>}
   </span>}
  </span>
 </label>;
}

export function PermissionMatrix({items,value,readOnly=false,onChange}:Selection&{items:Permission[]}){
 const [query,setQuery]=useState(''),groups=permissionGroups(items,query),selected=items.filter(p=>value[p.code]).length;
 const visibleColumns=columns.filter(([key])=>key!=='other'||items.some(p=>permissionGroups([p])[0]?.items[0]?.column==='other'));
 return <div className="permission-matrix">
  <div className="permission-toolbar">
   <label>筛选权限<input aria-label="筛选权限" placeholder="名称、编码、模块或说明" value={query}
    onKeyDown={e=>{if(e.key==='Enter')e.preventDefault();}} onChange={e=>setQuery(e.target.value)}/></label>
   <span role="status">已选 {selected} / {items.length} 项{query&&` · 筛选显示 ${groups.reduce((n,g)=>n+g.items.length,0)} 项`}</span>
  </div>
  <p className="permission-note">功能权限与用户数据范围共同生效。关联权限可由同一范围的其他角色提供，勾选只修改当前选项。</p>
  <div className="permission-scroll"><table>
   <caption>权限模块与功能矩阵</caption>
   <thead><tr><th scope="col">模块</th>{visibleColumns.map(([key,title])=><th scope="col" key={key}>{title}</th>)}</tr></thead>
   <tbody>{groups.map(group=><tr key={group.key}>
    <th scope="row"><strong>{group.label}</strong><small>{group.key}</small></th>
    {visibleColumns.map(([column])=><td key={column}>{group.items.filter(p=>p.column===column).map(item=>
     <PermissionChoice key={item.code} item={item} value={value} readOnly={readOnly} onChange={onChange}/>
    )}</td>)}
   </tr>)}</tbody>
  </table>{!groups.length&&<p className="empty">没有匹配的权限；筛选不会改变已勾选内容。</p>}</div>
 </div>;
}
