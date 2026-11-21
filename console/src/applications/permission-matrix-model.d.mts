import type {MatrixCell, MatrixCellIdentity, MatrixChange, MatrixGrant, MatrixMode, MatrixWindow} from './permission-matrix-types';
import type {RouteMethodGrant} from './route-method-permissions.mjs';

export const matrixBatchLimit: number;
export const matrixPageSize: number;
export function matrixCellKey(identity:MatrixCellIdentity):string;
export function matrixLegacyMoveCells(cells:MatrixCell[]):[MatrixCell,MatrixCell]|undefined;
export function toLocalInput(value:string|null|undefined):string;
export function permissionWindow(grant:MatrixGrant|undefined, validFrom?:string, expiresAt?:string):MatrixWindow;
export function matrixChange(cell:MatrixCell, action:'Revoke'):MatrixChange;
export function matrixChange(cell:MatrixCell, action:'SetWindow', window:MatrixWindow):MatrixChange;
export function matrixChange(cell:MatrixCell, action:'MoveLegacy', window:MatrixWindow, details:{destination:MatrixCell}):MatrixChange;
export function matrixChange(cell:MatrixCell, action:'Save', window:MatrixWindow, details:{mode:MatrixMode; routeMethods:RouteMethodGrant[]}):MatrixChange;
export function matchesMatrixPreview(changes:MatrixChange[], items:MatrixCellIdentity[]):boolean;
