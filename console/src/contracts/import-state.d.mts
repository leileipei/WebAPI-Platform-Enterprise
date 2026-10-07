export function importIdentity(input:any):string;
export function invalidatePreview(state:any,input:any):any;
export function canCommitPreview(state:any,now?:number):boolean;
export function mapImportTarget(operation:any,target:any):any;
export function previewRequest(input:any,budget?:any):any;
export function readImportFiles(files:Iterable<any>,budget?:any):Promise<{name:string;content:string;format:string}[]>;
export function recoverImportFailure(state:any,error:any):any;

export function acceptImportPreview(state:any,input:any,preview:any,requestSequence:number):any;
export function importPolicyScope(scope:any):string;
export function selectImportPolicy(scope:any,remote:any,override:any):any;
export function acceptImportPolicySave(current:{scope:string;generation:number},captured:{scope:string;generation:number},value:any):any;
