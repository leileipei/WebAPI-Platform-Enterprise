export type TransformDraft=Record<string,any>;
export function parseTransformDraft(source:string):TransformDraft;
export function serializeTransformDraft(draft:TransformDraft):string;
export function policyConfigSource(draft:any):string;
export function parsePolicyConfig(type:string,source:string):any;
export function previewReducer(state:any,event:any):any;
