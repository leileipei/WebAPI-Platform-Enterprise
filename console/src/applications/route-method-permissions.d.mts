export type RouteMethodGrant = {routeId:string;authorizationRevision:number;methods:string[]};
export type RouteMethodChoice = RouteMethodGrant & {path:string};
export type RouteMethodOptions = {environmentId:string;apiId:string;routes:RouteMethodChoice[]};
export function readRouteMethodOptions(value:unknown,environmentId:string,apiId:string):RouteMethodOptions|null;
export function routeMethodSelection(selections:unknown,options:RouteMethodOptions|null|undefined):RouteMethodGrant[];
export function toggleRouteMethod(selections:RouteMethodGrant[],route:RouteMethodChoice,method:string,selected:boolean):RouteMethodGrant[];
