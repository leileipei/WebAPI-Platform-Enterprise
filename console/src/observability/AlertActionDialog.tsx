import {Editor,type Field} from '../ui';import {actOnAlert,loadAlert,type AlertEventDto,type AlertAction} from '../api/alerts';
const labels={Ack:'确认告警',Resolve:'解决告警',Silence:'静默告警',Unsilence:'解除静默'};
export function AlertActionDialog({event,kind,onClose,onSaved}:{event:AlertEventDto;kind:AlertAction['kind'];onClose:()=>void;onSaved:(event:AlertEventDto)=>void}){
 const fields:Field[]=(kind==='Resolve'||kind==='Silence')?[{key:'reason',label:'处理原因',type:'textarea',required:true}]:[];
 if(kind==='Silence')fields.push({key:'durationSeconds',label:'静默时长',required:true,options:[{value:'900',label:'15 分钟'},{value:'3600',label:'1 小时'},{value:'14400',label:'4 小时'},{value:'86400',label:'24 小时'}]});
 const help=kind==='Resolve'?'人工解决后，持续故障将被抑制；实际恢复后重新布防，新故障生成新事件。':kind==='Silence'?'静默期间仍持续评估；到期恢复为已确认或待处理。':kind==='Ack'?'保留首位确认人；静默中的告警确认后仍保持静默。':'恢复为已确认或待处理，不重开已解决的原事件。';
 return <Editor title={labels[kind]} fields={fields} initial={{reason:'',durationSeconds:'900'}} help={help} close={onClose} loadLatest={async()=>loadAlert(event.id)} save={async(value,key,fresh)=>{const result=await actOnAlert(event.id,{kind,reason:value.reason?.trim()||null,...(kind==='Silence'?{durationSeconds:Number(value.durationSeconds)}:{})},`"${fresh?.revision??event.revision}"`,key);onSaved(result);}}/>;
}
