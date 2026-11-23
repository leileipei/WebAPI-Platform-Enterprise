function canonical(value){
  if(Array.isArray(value))return value.map(canonical);
  if(value!==null&&typeof value==='object')return Object.fromEntries(Object.keys(value).sort().map(key=>[key,canonical(value[key])]));
  return value;
}

export function compareSnapshots(before,after){
  const changes=[];
  if(before.schemaVersion!==after.schemaVersion)
    changes.push({kind:'schemaVersion',id:'schemaVersion',change:'修改',before:before.schemaVersion,after:after.schemaVersion});
  for(const kind of ['routes','clusters','policies','applications']){
    const left=new Map(before[kind].map(item=>[item.id,item]));
    const right=new Map(after[kind].map(item=>[item.id,item]));
    for(const id of [...new Set([...left.keys(),...right.keys()])].sort()){
      const a=left.get(id),b=right.get(id);
      if(a&&b&&JSON.stringify(canonical(a))===JSON.stringify(canonical(b)))continue;
      changes.push({kind,id,change:a?(b?'修改':'移除'):'新增',before:a,after:b});
    }
  }
  return changes;
}
