export type AccessState={identity:string;authorized:boolean;epoch:number;loading:boolean;conflict:boolean;detail?:Record<string,any>;draft?:Record<string,any>;internalUrl?:string;error?:string};
export function createAccessState(identity:string,authorized:boolean,epoch?:number):AccessState;
export function accessReducer(state:AccessState,event:Record<string,any>):AccessState;
export function mayCopyAddress(result:Record<string,any>|undefined,address:string|null|undefined):boolean;
export function releaseEntrySummary(release:Record<string,any>):{recorded:string;current:string;changed:boolean};
