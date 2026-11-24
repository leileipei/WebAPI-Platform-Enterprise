import json
import pathlib
import subprocess
import sys
import tempfile
import unittest


SCRIPTS = pathlib.Path(__file__).resolve().parents[2] / 'scripts'


class PolicyCleanupReceiptTests(unittest.TestCase):
    def test_policy_verifier_requires_measured_cleanup_for_the_reviewed_project(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            evidence = root / 'docs' / 'evidence' / 'policies'
            evidence.mkdir(parents=True)
            scripts = root / 'scripts'
            scripts.mkdir()
            verify = scripts / 'verify-policies-delivery.py'
            verify.write_bytes((SCRIPTS / verify.name).read_bytes())
            helper = SCRIPTS / 'acceptance_cleanup.py'
            (scripts / helper.name).write_bytes(helper.read_bytes())

            clear = {'secretsRemaining': 0, 'containersRemaining': 0, 'volumesRemaining': 0}
            cases = [
                ({'cleanup': {**clear, 'secretsRemaining': 1}}, None),
                ({'cleanup': {**clear, 'secretsRemaining': None}}, None),
                ({'cleanup': {**clear, 'secretsRemaining': False}}, None),
                ({'cleanup': {**clear, 'failed': True}}, None),
                ({'cleanup': clear}, {'project': 'run', **clear, 'secretsRemaining': 2}),
                ({'review': {'project': 'run'}}, {'project': 'other', **clear}),
            ]
            for result, separate in cases:
                with self.subTest(result=result, separate=separate):
                    (evidence / 'verification.json').write_text(json.dumps({'complete': True, 'project': 'run', **result}))
                    receipt = evidence / 'cleanup.json'
                    if separate:
                        receipt.write_text(json.dumps(separate))
                    else:
                        receipt.unlink(missing_ok=True)
                    run = subprocess.run([sys.executable, '-B', '-O', str(verify)], capture_output=True, text=True)
                    self.assertNotEqual(run.returncode, 0)
                    self.assertIn('cleanup receipt', run.stderr)
