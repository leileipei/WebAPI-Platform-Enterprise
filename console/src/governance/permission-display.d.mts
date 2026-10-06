export type Permission={id?:string;code:string;name?:string;module?:string;description?:string};
export type DisplayPermission=Permission&{label:string;moduleLabel:string;column:string};
export function describePermission(p:Permission):DisplayPermission;
export function permissionGroups(items:Permission[],query?:string):{key:string;label:string;items:DisplayPermission[]}[];
export function mayConfigurePermissions(role:{isSystem:boolean;permissions:string[]},items:Permission[]|undefined,status?:{loading?:boolean;error?:string}):boolean;
