export function importIdentity(input:any):string;
export function invalidatePreview(state:any,input:any):any;
export function canCommitPreview(state:any,now?:number):boolean;
export function mapImportTarget(operation:any,target:any):any;
export function previewRequest(input:any,budget?:any):any;
export function readImportFiles(files:Iterable<any>,budget?:any):Promise<{name:string;content:string;format:string}[]>;
export function recoverImportFailure(state:any,error:any):any;

export function acceptImportPreview(state:any,input:any,preview:any,requestSequence:number):any;
