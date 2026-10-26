import hashlib
import io
import json
import os
import pathlib
import re
import sys
import types
import unittest
from unittest.mock import patch, mock_open


class ProjectBootstrapTests(unittest.TestCase):
    def run_bootstrap(self, machine='a' * 32):
        text = (pathlib.Path(__file__).resolve().parents[1] / 'src/Codex.Control/OwnedProjectSourceBootstrap.cs').read_text(encoding='utf-8')
        script = re.search(r'Script = """\n(.*?)\n""";', text, re.S).group(1)
        facts = types.SimpleNamespace(st_dev=8, st_ino=17)
        with patch('builtins.open', mock_open(read_data=machine)), \
                patch.object(os, 'open', return_value=7), patch.object(os, 'close'), \
                patch.object(os, 'O_PATH', 0, create=True), patch.object(os, 'O_DIRECTORY', 0, create=True), \
                patch.object(os, 'O_CLOEXEC', 0, create=True), patch.object(os, 'fstat', return_value=facts), \
                patch.object(os, 'stat', return_value=facts), patch.object(os, 'readlink', return_value='/physical/home'), \
                patch.object(os, 'getuid', return_value=1001, create=True), patch.object(os, 'environ', {}), \
                patch.object(sys, 'argv', ['script', '/configured/home', '/bin/codex']), \
                patch.object(sys, 'stdout', new_callable=io.StringIO) as output, \
                patch.object(os, 'execve', side_effect=RuntimeError('fixture_exec')) as execute:
            with self.assertRaisesRegex(RuntimeError, 'fixture_exec'):
                exec(compile(script, '<synthetic-bootstrap>', 'exec'), {})
            return json.loads(output.getvalue()), execute.call_args.args

    def test_hash_matches_collector_identity_tuple_and_exec_stays_in_same_process(self):
        response, args = self.run_bootstrap()
        expected = hashlib.sha256(json.dumps(['a' * 32, 1001, '/physical/home', '8:11'], separators=(',', ':')).encode()).hexdigest()
        self.assertEqual(response, {'projectSourceIdentity': expected})
        self.assertEqual(args, ('/bin/codex', ['/bin/codex', 'app-server', '--listen', 'stdio://'], {'CODEX_HOME': '/configured/home'}))

    def test_missing_source_disables_project_evidence_without_preventing_app_server_exec(self):
        response, args = self.run_bootstrap('invalid-machine-id')
        self.assertEqual(response, {'projectSourceIdentity': None})
        self.assertEqual(args[0], '/bin/codex')
