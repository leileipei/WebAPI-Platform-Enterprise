export const matrixBatchLimit = 20;
export const matrixPageSize = 50;

export function matrixCellKey({apiId, environmentId}) {
  return apiId + '/' + environmentId;
}

export function matrixLegacyMoveCells(cells) {
  if (cells.length !== 2 || cells.some(({environment}) => !environment.canManage || !environment.canGrantLegacy)) return;
  const source = cells.find(cell => cell.grant?.mode === 'LegacyApi');
  const destination = cells.find(cell => !cell.grant);
  if (source && destination && (source.api.id !== destination.api.id || source.environment.id !== destination.environment.id))
    return [source,destination];
}

export function toLocalInput(value) {
  if (!value) return '';
  const date = new Date(value);
  return new Date(+date - date.getTimezoneOffset() * 60000).toISOString().slice(0, 19);
}

function permissionTime(input, original) {
  const withSeconds = input?.length === 16 ? input + ':00' : input;
  if (withSeconds === undefined || withSeconds === toLocalInput(original)) return original ?? null;
  return input ? new Date(input).toISOString() : null;
}

export function permissionWindow(grant, validFrom, expiresAt) {
  const start = permissionTime(validFrom, grant?.validFrom);
  if (!start) throw new Error('请选择授权生效时间。');
  return {validFrom:start, expiresAt:permissionTime(expiresAt, grant?.expiresAt)};
}

export function matrixChange(cell, action, window, details) {
  const grant = cell.grant;
  const identity = {
    apiId:cell.api.id, environmentId:cell.environment.id, action,
    ...(grant ? {permissionId:grant.id,revision:grant.revision} : {})
  };
  if (action === 'Revoke') return identity;
  if (action === 'SetWindow') return {...identity,...window};
  if (action === 'MoveLegacy') {
    const {destination} = details;
    return {...identity,...window,destinationApiId:destination.api.id,destinationEnvironmentId:destination.environment.id};
  }
  const {mode,routeMethods} = details;
  return {...identity,...window,mode,...(mode === 'RouteMethod' ? {routeMethods} : {})};
}

export function matchesMatrixPreview(changes, items) {
  const expected = changes.map(matrixCellKey);
  const move = changes.find(change => change.action === 'MoveLegacy');
  if (move) {
    if (changes.length !== 1 || !move.destinationApiId || !move.destinationEnvironmentId) return false;
    expected.push(matrixCellKey({apiId:move.destinationApiId,environmentId:move.destinationEnvironmentId}));
  }
  const targets = new Set(expected);
  if (!targets.size || targets.size !== expected.length) return false;
  return Array.isArray(items) && items.length === targets.size && items.every(item => targets.delete(matrixCellKey(item)));
}
