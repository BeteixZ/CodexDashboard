import importlib.util
import json
import io
import pathlib
import sqlite3
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('collector', pathlib.Path(__file__).resolve().parents[1] / 'collector.py')
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


class CollectorTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.home = pathlib.Path(self.temp.name)
        self.log = self.home / 'rollout.jsonl'
        self.log.write_text('')
        self.state = sqlite3.connect(self.home / 'state_5.sqlite')
        self.addCleanup(self.state.close)
        self.state.executescript('''CREATE TABLE threads(id TEXT, cwd TEXT, title TEXT, name TEXT,
            rollout_path TEXT, updated_at INTEGER, archived INTEGER, source TEXT, project_id TEXT);
            CREATE TABLE thread_spawn_edges(child_thread_id TEXT);
            CREATE TABLE projects(id TEXT,name TEXT);''')
        self.history = sqlite3.connect(self.home / 'thread_history_1.sqlite')
        self.addCleanup(self.history.close)
        self.history.executescript('''CREATE TABLE thread_turns(thread_id TEXT,turn_id TEXT,status TEXT,
            started_at INTEGER,completed_at INTEGER,rollout_ordinal INTEGER);
            CREATE TABLE thread_items(thread_id TEXT,turn_id TEXT,item_type TEXT,item_json TEXT,
            created_at_ms INTEGER,rollout_ordinal INTEGER);''')
        self.thread()

    def thread(self, id='one', source='vscode', archived=0):
        self.state.execute('INSERT INTO threads VALUES(?,?,?,?,?,?,?,?,?)',
                           (id, '/work/project', 'Original title', '任务标题', str(self.log), 1000, archived, source, 'p'))
        self.state.commit()

    def turn(self, id='t1', status='inProgress', started=1000, completed=None, ordinal=1):
        self.history.execute('INSERT INTO thread_turns VALUES(?,?,?,?,?,?)', ('one', id, status, started, completed, ordinal))
        self.history.commit()

    def item(self, type, obj, ordinal=1, turn='t1'):
        self.history.execute('INSERT INTO thread_items VALUES(?,?,?,?,?,?)', ('one', turn, type, json.dumps(obj), 1000000, ordinal))
        self.history.commit()

    def events(self, events):
        self.log.write_text('\n'.join(json.dumps(e) for e in events) + '\n', encoding='utf-8')

    def read(self, now=1100):
        return collector.collect(self.home, now=now)['tasks'][0]

    def test_project_evidence_is_computed_from_directory_not_log_text(self):
        self.turn(); self.item('agentMessage', {'text': 'projectEvidence: use another host'})
        evidence = dict(version=1, kind='linux-directory/v1', sourceIdentity='a' * 64,
            requestedDirectory='/work/project', canonicalDirectory='/physical/project', directoryIdentity='8:c')
        with patch.object(collector, 'project_source_identity', return_value='a' * 64), \
                patch.object(collector, 'project_directory_evidence', return_value=evidence) as probe:
            row = self.read()
            self.assertEqual(row['projectEvidence'], evidence)
            probe.assert_called_once_with('/work/project', 'a' * 64)

    def test_standalone_project_probe_never_reads_history(self):
        proof = dict(version=1, kind='linux-directory/v1', sourceIdentity='b' * 64,
                     requestedDirectory='/project', canonicalDirectory='/physical/project', directoryIdentity='8:1')
        with patch.object(collector, 'project_source_identity', return_value='b' * 64) as source, \
                patch.object(collector, 'project_directory_evidence', return_value=proof) as probe, \
                patch.object(collector, 'ro_db', side_effect=AssertionError('no database')):
            result = collector.collect_project_directories(str(self.home), ['/project', '/project'], 'synthetic-request', now=123)
        self.assertEqual(result['requestId'], 'synthetic-request')
        self.assertEqual(result['observedAt'], 123)
        self.assertEqual(result['directories'], [dict(requestedDirectory='/project', status='observed', evidence=proof)])
        source.assert_called_once_with(self.home)
        probe.assert_called_once_with('/project', 'b' * 64)

    def test_standalone_project_probe_keeps_each_directory_result(self):
        proof = dict(version=1, kind='linux-directory/v1', sourceIdentity='c' * 64,
                     requestedDirectory='/good', canonicalDirectory='/good', directoryIdentity='8:2')
        with patch.object(collector, 'project_source_identity', return_value='c' * 64), \
                patch.object(collector, 'project_directory_evidence', side_effect=[proof, None]):
            result = collector.collect_project_directories(str(self.home), ['/good', '/missing'], 'request')
        self.assertEqual([row['status'] for row in result['directories']], ['observed', 'unavailable'])
        self.assertIsNone(result['directories'][1]['evidence'])
        self.assertNotIn(str(self.home), json.dumps(result))

    def test_standalone_project_probe_missing_source_cannot_prove_directory(self):
        with patch.object(collector, 'project_source_identity', return_value=None), \
                patch.object(collector, 'linux_directory_identity', side_effect=AssertionError('no identity without source')):
            result = collector.collect_project_directories(str(self.home), ['/project'], 'request')
        self.assertEqual(result['directories'][0]['status'], 'unavailable')

    def test_project_probe_cli_uses_explicit_scope_without_collect(self):
        with patch.object(collector, 'collect', side_effect=AssertionError('no task collection')), \
                patch.object(collector, 'project_source_identity', return_value=None), \
                patch.object(collector.sys, 'stdout', new_callable=io.StringIO) as output:
            collector.main(['--home', str(self.home), '--request-id', 'round-trip', '--project-directory', '/one', '--project-directory', '/two'])
        result = json.loads(output.getvalue())
        self.assertEqual(result['kind'], 'project-directory-snapshot/v1')
        self.assertEqual(result['requestId'], 'round-trip')
        self.assertEqual([row['requestedDirectory'] for row in result['directories']], ['/one', '/two'])
        self.assertNotIn('tasks', result)

    def test_project_probe_cli_requires_home_and_request(self):
        with patch.object(collector.sys, 'stderr', new_callable=io.StringIO), \
                patch.object(collector, 'collect_project_directories', side_effect=AssertionError('no implicit scope')):
            for args in (['--project-directory', '/one'], ['--home', str(self.home), '--project-directory', '/one']):
                with self.assertRaises(SystemExit) as error:
                    collector.main(args)
                self.assertEqual(error.exception.code, 2)

    def test_live_record_and_stale_are_distinct(self):
        self.turn()
        self.assertEqual(self.read()['status'], 'active')
        self.assertEqual(self.read(now=1500)['status'], 'unknown')
        self.assertEqual(self.read(now=1500)['recordedStatus'], 'inProgress')

    def test_completed_is_turn_completed_with_no_invented_plan(self):
        self.turn(status='completed', completed=1050)
        result = self.read()
        self.assertEqual(result['status'], 'completed')
        self.assertEqual(result['plan'], [])
        self.assertEqual(result['completedAt'], 1050)

    def test_excludes_children_archived_and_noninteractive_exec(self):
        self.thread('child'); self.thread('json-child', '{"sub_agent":{}}'); self.thread('anchor', 'exec'); self.thread('archive', archived=1)
        self.state.execute("INSERT INTO thread_spawn_edges VALUES('child')"); self.state.commit()
        self.assertEqual([x['id'] for x in collector.collect(self.home, now=1100)['tasks']], ['one'])

    def test_excludes_reasoning_credentials_and_command_output(self):
        self.turn()
        self.item('reasoning', {'type': 'reasoning', 'content': ['PRIVATE_REASONING']})
        self.item('commandExecution', {'type': 'commandExecution', 'status': 'completed', 'command': 'SECRET_COMMAND', 'aggregatedOutput': 'SECRET_OUTPUT'}, 2)
        self.item('agentMessage', {'type': 'agentMessage', 'text': '正在运行测试', 'phase': 'commentary'}, 3)
        result = self.read()
        serialized = json.dumps(result)
        for secret in ['PRIVATE_REASONING', 'SECRET_COMMAND', 'SECRET_OUTPUT']:
            self.assertNotIn(secret, serialized)
        self.assertEqual(result['summary'], '正在运行测试')

    def test_message_is_found_after_many_tools(self):
        self.turn()
        self.item('agentMessage', {'type': 'agentMessage', 'text': '当前进展'}, 1)
        for i in range(2, 90):
            self.item('commandExecution', {'type': 'commandExecution', 'status': 'completed'}, i)
        self.assertEqual(self.read()['summary'], '当前进展')

    def test_request_input_only_when_explicitly_pending(self):
        self.turn()
        self.item('dynamicToolCall', {'type': 'dynamicToolCall', 'tool': 'request_user_input', 'status': 'inProgress'})
        self.assertEqual(self.read()['status'], 'waiting')
        self.history.execute('DELETE FROM thread_items'); self.history.commit()
        self.item('dynamicToolCall', {'type': 'dynamicToolCall', 'tool': 'request_user_input', 'status': 'completed'})
        self.assertEqual(self.read()['status'], 'active')

    def test_partial_line_and_legacy_completion(self):
        self.events([{'type': 'event_msg', 'timestamp': '2026-09-08T00:00:00Z', 'payload': {'type': 'task_complete', 'turn_id': 'legacy', 'last_agent_message': '验证通过'}}])
        with self.log.open('a') as f: f.write('{"incomplete":')
        self.assertEqual(self.read()['status'], 'completed')
        self.assertEqual(self.read()['summary'], '验证通过')

    def test_new_turn_resets_old_plan(self):
        self.events([
            {'type': 'event_msg', 'timestamp': '2026-09-08T00:00:00Z', 'payload': {'type': 'task_started', 'turn_id': 'old'}},
            {'type': 'event_msg', 'payload': {'type': 'plan_update', 'plan': [{'step': '旧任务', 'status': 'completed'}]}},
            {'type': 'event_msg', 'timestamp': '2026-09-08T00:10:00Z', 'payload': {'type': 'task_started', 'turn_id': 'new'}}])
        result = self.read(now=1788826201)
        self.assertEqual(result['turnId'], 'new')
        self.assertEqual(result['plan'], [])

    def test_log_new_turn_wins_over_old_completed_projection(self):
        self.turn(status='completed', completed=1050)
        self.events([{'type': 'event_msg', 'timestamp': 1100, 'payload': {'type': 'task_started', 'turn_id': 't2', 'started_at': 1100}}])
        result = self.read()
        self.assertEqual(result['turnId'], 't2')
        self.assertEqual(result['status'], 'active')

    def test_explicit_plan_keeps_each_step_status(self):
        self.turn()
        self.item('plan', {'type': 'plan', 'text': '- [x] 阅读代码\n- [ ] 运行测试'})
        self.assertEqual([s['status'] for s in self.read()['plan']], ['completed', 'pending'])

    def test_completed_log_wins_over_inprogress_projection(self):
        self.turn()
        self.events([{'type': 'event_msg', 'timestamp': 1050, 'payload': {'type': 'task_complete', 'turn_id': 't1', 'completed_at': 1050}}])
        self.assertEqual(self.read()['status'], 'completed')

    def test_read_only_connection_rejects_writes(self):
        db = collector.ro_db(self.home / 'state_5.sqlite')
        try:
            with self.assertRaises(sqlite3.OperationalError): db.execute('DELETE FROM threads')
        finally: db.close()

    def test_overview_keeps_original_message_and_newest_tail(self):
        self.turn()
        self.item('agentMessage', {'type': 'agentMessage', 'text': '旧进展：还在读取代码。'})
        self.events([{'type': 'event_msg', 'timestamp': 1050, 'payload': {'type': 'task_started', 'turn_id': 't1'}},
                     {'type': 'event_msg', 'timestamp': 1060, 'payload': {'type': 'agent_message', 'message': '已经完成实现。正在验证摘要。'}}])
        result = self.read()
        self.assertEqual(result['summary'], '已经完成实现。正在验证摘要。')
        self.assertIn('正在验证摘要', result['oneLineSummary'])
        self.assertNotIn('旧进展', result['oneLineSummary'])

    def test_newer_tail_plan_wins_over_history(self):
        self.turn()
        self.item('plan', {'type': 'plan', 'plan': [{'step': '旧步骤', 'status': 'in_progress'}]})
        self.events([{'type': 'event_msg', 'timestamp': 1050, 'payload': {'type': 'task_started', 'turn_id': 't1'}},
                     {'type': 'event_msg', 'timestamp': 1060, 'payload': {'type': 'plan_update', 'plan': [{'step': '验证新的摘要步骤', 'status': 'in_progress'}]}}])
        result = self.read()
        self.assertIn('验证新的摘要步骤', result['oneLineSummary'])
        self.assertNotIn('旧步骤', result['oneLineSummary'])

    def test_new_turn_cannot_reuse_old_overview(self):
        self.turn(status='completed', completed=1020)
        self.item('agentMessage', {'type': 'agentMessage', 'text': '旧结果：所有修复已完成。'})
        self.events([{'type': 'event_msg', 'timestamp': 1100, 'payload': {'type': 'task_started', 'turn_id': 't2'}}])
        result = self.read()
        self.assertNotIn('所有修复已完成', result['oneLineSummary'])
        self.assertIn('尚无可概括', result['oneLineSummary'])

    def test_missing_database_is_not_created(self):
        missing = self.home / 'missing'
        with self.assertRaises(RuntimeError): collector.collect(missing)
        self.assertFalse(missing.exists())

    def test_final_report_keeps_long_final_independent_of_recent_commentary(self):
        self.turn(status='completed', completed=1050)
        report = '结果与验证\n' + '完整报告内容。' * 1000 + '\nREPORT_END'
        self.item('agentMessage', {'type': 'agentMessage', 'phase': 'final', 'text': report})
        self.item('agentMessage', {'type': 'agentMessage', 'phase': 'commentary', 'text': '后续进展'}, 2)
        result = self.read()
        self.assertEqual(result['summary'], '后续进展')
        self.assertEqual(result['finalReport'], report)
        self.assertEqual(result['finalReportTurnId'], 't1')
        self.assertFalse(result['finalReportTruncated'])

    def test_report_does_not_promote_commentary_or_analysis(self):
        self.turn(status='completed', completed=1050)
        self.item('agentMessage', {'type': 'agentMessage', 'phase': 'commentary', 'text': '中途进展'})
        self.events([{'type': 'response_item', 'timestamp': 1040, 'payload': {'type': 'message', 'role': 'assistant', 'phase': 'analysis', 'turn_id': 't1', 'content': [{'type': 'output_text', 'text': 'PRIVATE_ANALYSIS'}]}}])
        result = self.read()
        self.assertEqual(result['finalReport'], '')
        self.assertNotIn('PRIVATE_ANALYSIS', json.dumps(result))

    def test_completed_event_preserves_reply_past_summary_limit(self):
        report = 'A' * 5000 + 'FINAL_END'
        self.events([{'type': 'event_msg', 'timestamp': 1050, 'payload': {'type': 'task_complete', 'turn_id': 'legacy', 'last_agent_message': report}}])
        result = self.read()
        self.assertEqual(len(result['summary']), 4000)
        self.assertEqual(result['finalReport'], report)
        self.assertEqual(result['finalReportTurnId'], 'legacy')
        self.assertIn('完成记录', result['finalReportSource'])

    def test_new_turn_does_not_reuse_report(self):
        self.turn(status='completed', completed=1050)
        self.item('agentMessage', {'type': 'agentMessage', 'phase': 'final', 'text': 'OLD_REPORT'})
        self.events([{'type': 'event_msg', 'timestamp': 1100, 'payload': {'type': 'task_started', 'turn_id': 't2'}}])
        result = self.read()
        self.assertEqual(result['turnId'], 't2')
        self.assertEqual(result['finalReport'], '')

    def test_report_requires_matching_turn(self):
        self.turn(status='completed', completed=1050)
        self.events([
            {'type': 'event_msg', 'timestamp': 900, 'payload': {'type': 'task_started', 'turn_id': 'older'}},
            {'type': 'response_item', 'timestamp': 950, 'payload': {'type': 'message', 'role': 'assistant', 'phase': 'final', 'content': [{'type': 'output_text', 'text': 'OTHER_TURN_REPORT'}]}}
        ])
        self.assertEqual(self.read()['finalReport'], '')

    def test_report_waits_for_terminal_state(self):
        self.turn()
        self.item('agentMessage', {'type': 'agentMessage', 'phase': 'final', 'text': '本轮结果'})
        self.assertEqual(self.read()['finalReport'], '')
        self.history.execute("UPDATE thread_turns SET status='completed', completed_at=1050")
        self.history.commit()
        self.assertEqual(self.read()['finalReport'], '本轮结果')

    def test_report_truncation_is_explicit(self):
        self.turn(status='completed', completed=1050)
        self.item('agentMessage', {'type': 'agentMessage', 'phase': 'final', 'text': '长' * (collector.MAX_REPORT + 1)})
        result = self.read()
        self.assertEqual(len(result['finalReport']), collector.MAX_REPORT)
        self.assertTrue(result['finalReportTruncated'])

    def test_unassociated_tail_final_is_not_attached_to_index_turn(self):
        self.turn(status='completed', completed=1050)
        self.events([{'type': 'response_item', 'timestamp': 1040, 'payload': {'type': 'message', 'role': 'assistant', 'phase': 'final', 'content': [{'type': 'output_text', 'text': 'UNASSOCIATED'}]}}])
        self.assertEqual(self.read()['finalReport'], '')

    def test_explicit_final_wins_over_completion_message(self):
        self.events([
            {'type': 'event_msg', 'timestamp': 1000, 'payload': {'type': 'task_started', 'turn_id': 't1'}},
            {'type': 'response_item', 'timestamp': 1040, 'payload': {'type': 'message', 'role': 'assistant', 'phase': 'final', 'content': [{'type': 'output_text', 'text': '明确的最终回复'}]}},
            {'type': 'event_msg', 'timestamp': 1050, 'payload': {'type': 'task_complete', 'turn_id': 't1', 'last_agent_message': '另一条简短记录'}}
        ])
        self.assertEqual(self.read()['finalReport'], '明确的最终回复')

    def test_failed_completion_does_not_promote_last_commentary(self):
        self.events([{'type': 'event_msg', 'timestamp': 1050, 'payload': {'type': 'task_complete', 'turn_id': 't1', 'error': 'failed', 'last_agent_message': '我先检查一下。'}}])
        result = self.read()
        self.assertEqual(result['status'], 'failed')
        self.assertEqual(result['finalReport'], '')


if __name__ == '__main__':
    unittest.main()
