export type AccessSettings={gatewayPublicUrl?:string|null;gatewayInternalUrl?:string|null;basePath?:string|null};
export function normalizeAccessSettings<T extends AccessSettings>(settings:T,isProduction?:boolean):T & {gatewayPublicUrl:string|null;gatewayInternalUrl:string|null;basePath:string};
export function buildAddressTemplate(settings:AccessSettings,path:string):string|null;
export function buildAddressExample(settings:AccessSettings,path:string,values:Record<string,string>):string|null;
export function buildCurlExample(method:string,address:string):string;
