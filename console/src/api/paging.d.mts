import type {RequestOptions} from './client';
export function withPage(path:string,page:number):string;
export function readAllPages(request:(path:string,options?:RequestOptions)=>Promise<any>,path:string,options?:RequestOptions):Promise<any>;
