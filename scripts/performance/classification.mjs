const commitPattern=/^[a-f0-9]{40}$/;
const hashPattern=/^[a-f0-9]{64}$/;
export function validateClassificationManifest(manifest,sourceCommit){
  if(!commitPattern.test(sourceCommit??'')||manifest?.schemaVersion!==1||!Array.isArray(manifest.sourceCommits)||!manifest.sourceCommits.length||manifest.sourceCommits.some(c=>!commitPattern.test(c))||new Set(manifest.sourceCommits).size!==manifest.sourceCommits.length||!manifest.sourceCommits.includes(sourceCommit))throw Error('SQL classification source identity is not covered');
  if(!manifest.rules||Array.isArray(manifest.rules)||typeof manifest.rules!=='object')throw Error('SQL classification rules missing');
  for(const [hash,rule] of Object.entries(manifest.rules)){
    if(!hashPattern.test(hash)||!['authorization','data'].includes(rule?.classification)||typeof rule.reason!=='string'||rule.reason.trim().length<5||!Array.isArray(rule.sourceRefs)||!rule.sourceRefs.length)throw Error('SQL classification evidence invalid');
    for(const ref of rule.sourceRefs)if(!manifest.sourceCommits.includes(ref.commit)||typeof ref.path!=='string'||!/^src\/[A-Za-z0-9_./-]+\.cs$/.test(ref.path)||ref.path.split('/').includes('..')||!Number.isSafeInteger(ref.line)||ref.line<1)throw Error('SQL classification source evidence invalid');
  }
  return manifest;
}
export function classifyMeasurement(measurement,{manifest,sourceCommit,manifestHash}){
  validateClassificationManifest(manifest,sourceCommit);if(!hashPattern.test(manifestHash??''))throw Error('SQL classification manifest hash missing');
  const unknown=new Set();
  const samples=measurement.samples.map(sample=>{
    if(sample.rawSql||!Number.isSafeInteger(sample.sql?.total)||sample.sql.total<0||!Array.isArray(sample.shapes))throw Error('SQL command totals are invalid');
    let authorization=0,data=0,unclassified=0;
    const shapes=sample.shapes.map(shape=>{
      if(!hashPattern.test(shape.hash??'')||!Number.isSafeInteger(shape.count)||shape.count<1||!['reader','scalar','nonquery'].includes(shape.kind))throw Error('SQL command shape invalid');
      const rule=manifest.rules[shape.hash],sourceEvidence=rule?.sourceRefs.filter(ref=>ref.commit===sourceCommit)??[];
      const classification=sourceEvidence.length?rule.classification:'unclassified';
      if(classification==='authorization')authorization+=shape.count;else if(classification==='data')data+=shape.count;else {unclassified+=shape.count;unknown.add(shape.hash);}
      return {...shape,classification,sourceEvidence,classificationReason:sourceEvidence.length?rule.reason:null};
    });
    if(authorization+data+unclassified!==sample.sql.total)throw Error('SQL command total differs from original probe');
    return {...sample,rawSql:{...sample.sql},shapes,sql:{...sample.sql,authorization,data,unclassified}};
  });
  return {...measurement,samples,classificationComplete:samples.length>0&&samples.every(s=>s.sql.unclassified===0),classificationManifest:{hash:manifestHash,sourceCommit,unclassifiedHashes:[...unknown].sort()}};
}
