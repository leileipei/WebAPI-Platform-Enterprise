export function allowNavigation(target?:Window):boolean;
export function navigate(path:string,committed?:boolean,target?:Window):void;
export function bindNavigation(target:Window,onPath:(path:string)=>void):()=>void;
