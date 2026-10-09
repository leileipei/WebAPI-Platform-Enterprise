export type EnvironmentChoice={id:string;projectId:string;name:string;active:boolean;isProduction:boolean};
export type PipelineStageDefinition={order:number;environmentId:string;requiredTestTypes:string[];evidenceValidityMinutes:number;waitTimeoutMinutes:number;approvalFlowId:string|null};
export type PipelineDefinition={name:string;description:string;stages:PipelineStageDefinition[]};
export function validatePipelineDefinition(definition:PipelineDefinition,environments:EnvironmentChoice[]):string[];
export function pipelineAuthorityKey(actor:unknown,resourceId:string,attemptId?:string|null,profileHash?:string|null,epoch?:number):string;
export function reconcilePipelineDraft(draft:unknown,latest:unknown,status:number):any;
export function pipelineCanManage(actor:unknown,scope:unknown):boolean;
export function newPipelineStage(environmentId:string,order:number,environments:EnvironmentChoice[]):PipelineStageDefinition;
