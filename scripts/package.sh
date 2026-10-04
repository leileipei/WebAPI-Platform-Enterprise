#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
if [ "${1:-}" = observability ]; then
 exec python3 scripts/package-observability.py "${2:?Immutable source commit required}"
fi
[ -f console/dist/index.html ] || { echo 'Build console before packaging.' >&2; exit 2; }
python3 - <<'PY'
from pathlib import Path
import zipfile, json, hashlib, subprocess
root=Path.cwd();out=root/'deliverables';out.mkdir(exist_ok=True)
blocked={'.git','.superpowers','.runtime','.secrets','node_modules','bin','obj','TestResults','deliverables'}
files=[p for p in root.rglob('*') if p.is_file() and not any(x in blocked for x in p.relative_to(root).parts) and p.suffix not in {'.trx','.user','.log'}]
archive=out/'WebAPI_Enterprise_核心闭环源码与验收.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
 for p in sorted(files):z.write(p,'enterprise/'+str(p.relative_to(root)))
with zipfile.ZipFile(archive) as z:
 assert z.testzip() is None
 assert not any(any(x in blocked for x in Path(n).parts) for n in z.namelist())
 assert 'enterprise/README.md' in z.namelist()
sha=hashlib.sha256(archive.read_bytes()).hexdigest()
(out/'manifest.json').write_text(json.dumps({'file':archive.name,'sha256':sha,'files':len(files),'secretsExcluded':True,'sourceRevision':subprocess.check_output(['git','rev-parse','HEAD'],text=True).strip(),'acceptanceComplete':json.loads((root/'docs/evidence/core-loop/verification.json').read_text())['complete']},ensure_ascii=False,indent=2)+'\n')
print(str(archive));print('Files: '+str(len(files))+'; SHA256: '+sha)
PY
