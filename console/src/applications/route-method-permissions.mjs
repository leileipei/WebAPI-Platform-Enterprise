const supportedMethods = new Set(['GET','POST','PUT','PATCH','DELETE','HEAD','OPTIONS']);
const revision = value => Number.isSafeInteger(value) && value > 0;
const isIdentifier = value => typeof value === 'string' && value.length > 0;
const validMethods = methods => Array.isArray(methods) && methods.length > 0 &&
  new Set(methods).size === methods.length && methods.every(method => supportedMethods.has(method));

export function readRouteMethodOptions(value, environmentId, apiId) {
  if (!value || value.environmentId !== environmentId || value.apiId !== apiId || !Array.isArray(value.routes) ||
      value.routes.some(route => !isIdentifier(route?.routeId) || typeof route.path !== 'string' || !route.path.startsWith('/') ||
        !revision(route.authorizationRevision) || !validMethods(route.methods)) ||
      new Set(value.routes.map(route => route.routeId)).size !== value.routes.length) return null;
  return value;
}

export function routeMethodSelection(selections, options) {
  if (!options || !Array.isArray(selections) ||
      new Set(selections.map(selection => selection?.routeId)).size !== selections.length) return [];
  return selections.filter(selection => {
    const route = options.routes.find(candidate => candidate.routeId === selection?.routeId);
    return route && route.authorizationRevision === selection.authorizationRevision && validMethods(selection.methods) &&
      selection.methods.every(method => route.methods.includes(method));
  }).map(({routeId,authorizationRevision,methods}) => ({routeId,authorizationRevision,methods}));
}

export function toggleRouteMethod(selections, route, method, selected) {
  if (!route.methods.includes(method)) return selections;
  const previous = selections.find(selection => selection.routeId === route.routeId &&
    selection.authorizationRevision === route.authorizationRevision);
  const methods = route.methods.filter(item => item === method ? selected : previous?.methods.includes(item));
  const remaining = selections.filter(selection => selection.routeId !== route.routeId);
  return methods.length ? [...remaining, {routeId:route.routeId, authorizationRevision:route.authorizationRevision, methods}] : remaining;
}
