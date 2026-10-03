export {apiRequest,ApiError} from './transport.mjs';
export type RequestOptions={method?:string;body?:unknown;etag?:string;idempotencyKey?:string;signal?:AbortSignal};
