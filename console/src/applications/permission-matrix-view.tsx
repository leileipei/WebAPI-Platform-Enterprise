import type {MatrixAuthority, MatrixCell, MatrixGrant, MatrixImpact, MatrixMode, MatrixPage} from './permission-matrix-types';
import {matrixBatchLimit, matrixCellKey} from './permission-matrix-model.mjs';

const states = {
  Pending:{label:'待生效',tone:'amber'},
  Active:{label:'窗口内',tone:'green'},
  Expired:{label:'已过期',tone:'red'}
};

export const matrixModeLabel = (mode:MatrixMode) => mode === 'RouteMethod' ? '路由方法' : '旧版 API';
export const matrixCellLabel = (cell:MatrixCell) => cell.api.name + ' · ' + cell.environment.name;
export const keyForCell = (cell:MatrixCell) => matrixCellKey({apiId:cell.api.id,environmentId:cell.environment.id});
export const matrixMoveLabel = (cell:MatrixCell,index:number) => (index === 0 ? '移出 ' : '移入 ') + matrixCellLabel(cell);

export function allowedModes({environment,grant}:MatrixCell):MatrixMode[] {
  const modes:MatrixMode[] = [];
  if (environment.canGrantLegacy && grant?.mode !== 'RouteMethod') modes.push('LegacyApi');
  if (environment.canGrantRouteMethods) modes.push('RouteMethod');
  return modes;
}

export function selectableCell(cell:MatrixCell) {
  return cell.environment.canManage && (!!cell.grant || allowedModes(cell).length > 0);
}

export function MatrixGrid({data,selected,blocked,toggle,edit}:{
  data:MatrixPage; selected:Map<string,MatrixCell>; blocked:boolean; toggle:(cell:MatrixCell)=>void; edit:(cell:MatrixCell)=>void;
}) {
  return <>
    <div className="table-wrap">
      <table className="access-matrix" aria-label="API 授权矩阵">
        <thead><tr>
          <th scope="col">API</th>
          {data.environments.map(environment => <th key={environment.id} scope="col">
            {environment.name} {environment.isProduction && <ProductionBadge/>}
            <small>{environment.code}</small>
          </th>)}
        </tr></thead>
        <tbody>{data.items.map(api => <tr key={api.id}>
          <th scope="row">{api.name}<small>{api.code}</small></th>
          {data.environments.map(environment => {
            const cell = {api,environment,grant:api.grants.find(grant => grant.environmentId === environment.id)};
            const checked = selected.has(keyForCell(cell));
            const label = matrixCellLabel(cell);
            const editLabel = cell.grant ? '编辑授权' : '新增授权';
            return <td key={environment.id}>
              <div className="access-matrix-cell">
                <div className="access-matrix-cell-actions">
                  <input type="checkbox" aria-label={'选择 ' + label} checked={checked}
                    disabled={blocked || !selectableCell(cell) || !checked && selected.size >= matrixBatchLimit}
                    onChange={() => toggle(cell)}/>
                  {selectableCell(cell) && allowedModes(cell).length > 0 && <button type="button" className="text-btn"
                    aria-label={editLabel + ' ' + label} disabled={blocked} onClick={() => edit(cell)}>{editLabel}</button>}
                </div>
                <GrantDetails grant={cell.grant}/>
              </div>
            </td>;
          })}
        </tr>)}</tbody>
      </table>
    </div>
    {!data.environments.length && <p className="empty">当前项目无可读取的启用环境。</p>}
    {!data.items.length && <p className="empty">当前项目暂无 API。</p>}
  </>;
}

function ProductionBadge() {
  return <span className="badge red">生产</span>;
}

function MatrixTime({value}:{value:string}) {
  const date = new Date(value);
  return <time dateTime={value} title={value}>{date.toLocaleString('zh-CN',{hour12:false})}</time>;
}

function GrantDetails({grant}:{grant:MatrixGrant|MatrixAuthority|null|undefined}) {
  if (!grant) return <span className="muted">未授权</span>;
  const state = states[grant.state];
  const methodCount = 'methodCount' in grant ? grant.methodCount :
    grant.routeMethods?.reduce((sum,item) => sum + item.methods.length,0);
  return <div className="access-matrix-grant">
    <div>{matrixModeLabel(grant.mode)} {state && <span className={'badge ' + state.tone}>{state.label}</span>}</div>
    <small>生效 <MatrixTime value={grant.validFrom}/></small>
    <small>失效 {grant.expiresAt ? <MatrixTime value={grant.expiresAt}/> : '无固定时间'}</small>
    {grant.mode === 'RouteMethod' && methodCount !== undefined && <small>{methodCount} 个 HTTP 方法</small>}
    {'methods' in grant && grant.methods !== null && grant.methods.length > 0 && <ul>
      {grant.methods.map(route => <li key={route.routeId + '/' + route.authorizationRevision}>
        <div>{route.matchesRoute === false ? '路由已变化或停用，需要重新授权' : route.path ?? '路由路径不可读取'}
          {' · 授权修订 '}{route.authorizationRevision}</div>
        <span>{route.methods.join('、')}</span>
      </li>)}
    </ul>}
  </div>;
}

export function MatrixImpacts({items,cells,moving}:{items:MatrixImpact[]; cells:MatrixCell[]; moving:boolean}) {
  const impactByCell = new Map(items.map(item => [matrixCellKey(item),item]));
  return <div className="access-matrix-impacts">
    {cells.map((cell,index) => {
      const key = keyForCell(cell);
      const item = impactByCell.get(key);
      if (!item) return null;
      return <article className="promotion-route" key={key}>
        <h3>{moving ? matrixMoveLabel(cell,index) : matrixCellLabel(cell)} {item.isProduction && <ProductionBadge/>}</h3>
        <div className="access-matrix-comparison">
          <section aria-label="变更前工作授权"><h4>变更前工作授权</h4><GrantDetails grant={item.before}/></section>
          <section aria-label="变更后工作授权"><h4>变更后工作授权</h4><GrantDetails grant={item.after}/></section>
          <section aria-label="当前运行依据"><h4>当前运行依据</h4>
            {item.running.status === 'Confirmed' ? <>
              <p>已确认配置版本 {item.running.configVersion ?? '未知'} · 部署序列 {item.running.deploymentSequence ?? '未知'}</p>
              <GrantDetails grant={item.running.grant}/>
            </> : <p>{item.running.status === 'NotVisible' ? '当前权限无法查看运行依据' : '运行版本未确认'}</p>}
          </section>
        </div>
      </article>;
    })}
  </div>;
}
