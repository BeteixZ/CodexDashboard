import hashlib
import importlib.util
import json
import pathlib
import stat
import types
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('collector', pathlib.Path(__file__).resolve().parents[1] / 'collector.py')
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


class ProjectEvidenceTests(unittest.TestCase):
    def test_machine_account_and_configuration_are_hashed_without_export(self):
        with patch.object(collector.sys, 'platform', 'linux'), \
                patch.object(collector.pathlib.Path, 'read_text', return_value='1' * 32), \
                patch.object(collector.pathlib.Path, 'absolute', return_value=pathlib.Path('/synthetic/codex')), \
                patch.object(collector.os, 'getuid', return_value=1001, create=True), \
                patch.object(collector, 'linux_directory_identity', return_value=('/synthetic/codex', '1:2')):
            identity = collector.project_source_identity(pathlib.Path('/synthetic/codex'))
            expected = hashlib.sha256(json.dumps(['1' * 32, 1001, '/synthetic/codex', '1:2'], separators=(',', ':')).encode()).hexdigest()
            self.assertEqual(identity, expected)
            with patch.object(collector.os, 'getuid', return_value=1002):
                self.assertNotEqual(identity, collector.project_source_identity(pathlib.Path('/synthetic/codex')))
            with patch.object(collector.pathlib.Path, 'read_text', return_value='2' * 32):
                self.assertNotEqual(identity, collector.project_source_identity(pathlib.Path('/synthetic/codex')))
            with patch.object(collector, 'linux_directory_identity', return_value=('/another/codex', '1:3')):
                self.assertNotEqual(identity, collector.project_source_identity(pathlib.Path('/synthetic/codex')))

    def test_missing_machine_identity_has_no_directory_fallback(self):
        with patch.object(collector.sys, 'platform', 'linux'), patch.object(collector.pathlib.Path, 'read_text', side_effect=PermissionError):
            self.assertIsNone(collector.project_source_identity(pathlib.Path('/synthetic/codex')))
        with patch.object(collector.sys, 'platform', 'linux'), patch.object(collector.pathlib.Path, 'read_text', return_value='0' * 32):
            self.assertIsNone(collector.project_source_identity(pathlib.Path('/synthetic/codex')))
        with patch.object(collector.sys, 'platform', 'darwin'), patch.object(collector.pathlib.Path, 'read_text') as read:
            self.assertIsNone(collector.project_source_identity(pathlib.Path('/synthetic/codex')))
            read.assert_not_called()

    def directory_mocks(self, *, current_inode=12, resolved='/work/Project', mode=stat.S_IFDIR):
        self.enterContext(patch.object(collector.os, 'O_PATH', 0x200000, create=True))
        self.enterContext(patch.object(collector.os, 'O_DIRECTORY', 0x10000, create=True))
        self.enterContext(patch.object(collector.os, 'O_CLOEXEC', 0x80000, create=True))
        opened = self.enterContext(patch.object(collector.os, 'open', return_value=33))
        self.enterContext(patch.object(collector.os, 'fstat', return_value=types.SimpleNamespace(st_dev=8, st_ino=12, st_mode=mode)))
        self.enterContext(patch.object(collector.os, 'stat', return_value=types.SimpleNamespace(st_dev=8, st_ino=current_inode)))
        self.enterContext(patch.object(collector.os, 'readlink', return_value=resolved))
        closed = self.enterContext(patch.object(collector.os, 'close'))
        return opened, closed

    def test_opened_symlink_target_and_case_are_preserved(self):
        opened, closed = self.directory_mocks()
        evidence = collector.project_directory_evidence('/alias/Project', 'a' * 64)
        self.assertEqual(evidence, dict(version=1, kind='linux-directory/v1', sourceIdentity='a' * 64,
            requestedDirectory='/alias/Project', canonicalDirectory='/work/Project', directoryIdentity='8:c'))
        opened.assert_called_once_with('/alias/Project', collector.os.O_PATH | collector.os.O_DIRECTORY | collector.os.O_CLOEXEC)
        closed.assert_called_once_with(33)

    def test_retargeted_link_is_not_attested(self):
        _, closed = self.directory_mocks(current_inode=99)
        self.assertIsNone(collector.project_directory_evidence('/alias/Project', 'a' * 64))
        closed.assert_called_once_with(33)

    def test_deleted_handle_is_not_attested(self):
        _, closed = self.directory_mocks(resolved='/work/Project (deleted)')
        self.assertIsNone(collector.project_directory_evidence('/alias/Project', 'a' * 64))
        closed.assert_called_once_with(33)

    def test_unavailable_source_never_probes_task_directory(self):
        with patch.object(collector, 'linux_directory_identity') as probe:
            self.assertIsNone(collector.project_directory_evidence('/alias/Project', None))
            probe.assert_not_called()

    def test_directory_failure_leaves_capability_unavailable(self):
        with patch.object(collector, 'linux_directory_identity', side_effect=PermissionError):
            self.assertIsNone(collector.project_directory_evidence('/alias/Project', 'a' * 64))


if __name__ == '__main__':
    unittest.main()
