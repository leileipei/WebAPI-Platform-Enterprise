export function editorMaySubmit({authorized,busy=false,invalid=false,discard=false,conflict=false}){return !!authorized&&!busy&&!invalid&&!discard&&!conflict;}
