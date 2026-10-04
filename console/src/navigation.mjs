// Keep history acceptance separate from UI rendering so a rejected Back cannot discard an editor.
export function allowNavigation(target=window){return target.dispatchEvent(new target.Event('app-navigation',{cancelable:true}));}
export function navigate(path,committed=false,target=window){
 if(!committed&&!allowNavigation(target))return;
 target.history.pushState({...target.history.state,webapiIndex:(target.history.state?.webapiIndex||0)+1},'',path);
 target.dispatchEvent(new target.Event('app-route'));
}
export function bindNavigation(target,onPath){
 const history=target.history;let acceptedIndex=history.state?.webapiIndex||0,acceptedUrl=target.location.href,restoring=false;
 history.replaceState({...history.state,webapiIndex:acceptedIndex},'',acceptedUrl);
 const changed=()=>{acceptedIndex=history.state?.webapiIndex||0;acceptedUrl=target.location.href;onPath(target.location.pathname,target.location.search);};
 const popped=event=>{
  if(restoring){restoring=false;return;}
  if(!allowNavigation(target)){
   const index=event.state?.webapiIndex;
   if(typeof index==='number'&&index!==acceptedIndex){restoring=true;history.go(acceptedIndex-index);}
   else history.replaceState({...event.state,webapiIndex:acceptedIndex},'',acceptedUrl);
   return;
  }
  changed();
 };
 target.addEventListener('app-route',changed);target.addEventListener('popstate',popped);
 return()=>{target.removeEventListener('app-route',changed);target.removeEventListener('popstate',popped);};
}
