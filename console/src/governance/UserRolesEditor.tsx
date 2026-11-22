import {apiRequest, ApiError} from '../api/client';
import {Editor} from '../ui';

export type RoleOption = {id:string; name:string; code:string};
export type AssignmentUser = {id:string; displayName:string; roleIds:string[]; revision:number};
export type RoleDraft = {user:AssignmentUser; catalog:RoleOption[]; context:string};

export function roleReplacement(assigned:string[], catalog:RoleOption[], choices:Record<string,unknown>) {
  const selected = new Set(assigned);
  let changed = false;
  for (const {id} of catalog) {
    const checked = choices[id];
    if (typeof checked !== 'boolean') return undefined;
    if (checked === selected.has(id)) continue;
    if (checked) selected.add(id);
    else selected.delete(id);
    changed = true;
  }
  return changed ? {roleIds:[...selected]} : undefined;
}

type Props = {draft:RoleDraft; close:()=>void; onSaved:()=>void; onConflict:()=>void};
export function UserRolesEditor({draft:{user,catalog},close,onSaved,onConflict}:Props) {
  const assigned = new Set(user.roleIds);
  const outsideCount = user.roleIds.filter(id => !catalog.some(role => role.id === id)).length;
  return <Editor title={'分配角色 · ' + user.displayName}
    fields={catalog.map(role => ({key:role.id,label:role.name+' · '+role.code,type:'checkbox'}))}
    initial={Object.fromEntries(catalog.map(role => [role.id,assigned.has(role.id)]))}
    help={outsideCount ? '另有 '+outsideCount+' 项既有授权未在目录中显示，保存时将保留。' : '勾选或取消角色后保存。'}
    explicitSubmitOnly close={close} maySave={choices => !!roleReplacement(user.roleIds,catalog,choices)}
    save={async (choices,idempotencyKey) => {
      const body = roleReplacement(user.roleIds,catalog,choices);
      if (!body) throw new Error('请重新核对角色变更。');
      try {
        await apiRequest('/users/'+user.id+'/roles',{method:'PUT',body,etag:'"'+user.revision+'"',idempotencyKey});
      } catch (error) {
        if (error instanceof ApiError && error.status === 412) {
          onConflict();
          return;
        }
        throw error;
      }
      onSaved();
    }}
  />;
}
