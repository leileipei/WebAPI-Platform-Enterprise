import type {SettingsGroupDto,SettingsInputValues} from '../settings/contracts';
import type {NotificationChannel,NotificationDelivery} from './api';
export type NotificationTestState={channel:NotificationChannel;revision:number;authority:string;email:string;epoch:number;receipt:NotificationDelivery|null;key:string|null;busy:boolean;conflict:boolean;invalid:boolean;error:string};
export function notificationSettingsInput(dto:SettingsGroupDto):SettingsInputValues;
export function notificationTestLabel(receipt:{status:string}|null):string;
export function mayTestNotification(state:{authorized:boolean;busy?:boolean;dirty:boolean;conflict:boolean;etag:string|null;loaded:{revision:number}|null}):boolean;
export function createNotificationTestState(channel:NotificationChannel,revision:number,authority:string):NotificationTestState;
export function notificationTestState(state:NotificationTestState,event:{type:string;revision?:number;epoch?:number;key?:string;email?:string;status?:number;message?:string;receipt?:NotificationDelivery}):NotificationTestState;
