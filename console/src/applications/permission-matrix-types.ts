import type {Page} from '../api/types';
import type {RouteMethodGrant} from './route-method-permissions.mjs';

export type MatrixMode = 'LegacyApi' | 'RouteMethod';
export type MatrixState = 'Pending' | 'Active' | 'Expired';
export type MatrixAction = 'Save' | 'SetWindow' | 'Revoke' | 'MoveLegacy';
export type MatrixWindow = {validFrom:string; expiresAt:string|null};
export type MatrixGrant = MatrixWindow & {
  id:string; environmentId:string; mode:MatrixMode; revision:number; state:MatrixState; routeMethods:RouteMethodGrant[]|null;
};
export type MatrixEnvironment = {
  id:string; code:string; name:string; isProduction:boolean; canManage:boolean; canGrantLegacy:boolean; canGrantRouteMethods:boolean;
};
export type MatrixApi = {id:string; code:string; name:string; grants:MatrixGrant[]};
export type MatrixPage = Page<MatrixApi> & {
  applicationId:string; projectId:string; evaluatedAt:string; environments:MatrixEnvironment[];
};
export type MatrixCell = {api:MatrixApi; environment:MatrixEnvironment; grant?:MatrixGrant};
export type MatrixCellIdentity = {apiId:string; environmentId:string};
export type MatrixDraft = {action:MatrixAction; cells:MatrixCell[]};
export type MatrixChange = MatrixCellIdentity & Partial<MatrixWindow> & {
  action:MatrixAction; permissionId?:string; revision?:number; mode?:MatrixMode; routeMethods?:RouteMethodGrant[];
  destinationApiId?:string; destinationEnvironmentId?:string;
};
export type MatrixRequest = {projectId:string; changes:MatrixChange[]};
export type MatrixAuthority = MatrixWindow & {
  mode:MatrixMode; state:MatrixState; methodCount:number;
  methods:(RouteMethodGrant & {path:string|null; matchesRoute?:boolean|null})[]|null;
};
export type MatrixImpact = MatrixCellIdentity & {
  isProduction:boolean; before:MatrixAuthority|null; after:MatrixAuthority|null;
  running:{
    status:'Confirmed'|'Unconfirmed'|'NotVisible'; configVersion:number|null; deploymentSequence:number|null; grant:MatrixAuthority|null;
  };
};
export type MatrixPreview = {previewToken:string; expiresAt:string; requiresProductionConfirmation:boolean; items:MatrixImpact[]};
export type MatrixCommitRequest = MatrixRequest & {previewToken:string; confirmProduction:boolean};
export type MatrixReceipt = {appliedCount:number; activation:'WorkingConfiguration'};
