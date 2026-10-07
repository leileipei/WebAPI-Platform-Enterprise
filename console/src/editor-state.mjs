export function editorMaySubmit({authorized,busy=false,invalid=false,discard=false,conflict=false}){return !!authorized&&!busy&&!invalid&&!discard&&!conflict;}
export function editorMayImplicitlySubmit({explicitSubmitOnly=false,key,tag}){return !(explicitSubmitOnly&&key==='Enter'&&['INPUT','SELECT'].includes(tag));}
