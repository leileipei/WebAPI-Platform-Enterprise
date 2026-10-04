export async function copyTraceId(id,platform={clipboard:navigator.clipboard,document}){
 if(!/^[0-9a-f]{32}$/i.test(id)||/^0+$/.test(id))return false;
 try{if(platform.clipboard){await platform.clipboard.writeText(id);return true;}}catch{}
 const doc=platform.document,previous=doc.activeElement,textarea=doc.createElement('textarea');textarea.value=id;textarea.style.position='fixed';textarea.style.opacity='0';textarea.setAttribute('readonly','');doc.body.appendChild(textarea);
 try{textarea.select();return !!doc.execCommand('copy');}catch{return false;}finally{textarea.remove();previous?.focus();}
}
export function saveCsvBlob(blob,filename,platform={document,URL,later:fn=>setTimeout(fn,1000)}){const url=platform.URL.createObjectURL(blob),anchor=platform.document.createElement('a');anchor.href=url;anchor.download=filename;platform.document.body.appendChild(anchor);try{anchor.click();}finally{anchor.remove();platform.later(()=>platform.URL.revokeObjectURL(url));}}
