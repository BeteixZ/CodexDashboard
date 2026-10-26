import importlib.util
import pathlib
import unittest

spec = importlib.util.spec_from_file_location('collector_summary', pathlib.Path(__file__).resolve().parents[1] / 'collector.py')
collector = importlib.util.module_from_spec(spec)
spec.loader.exec_module(collector)


class SummaryTests(unittest.TestCase):
    def summary(self, message='', status='active', plan=None, timeline=None):
        return collector.task_overview('改进监控面板', message, plan or [], timeline or [], status)

    def test_selects_real_progress_after_acknowledgement(self):
        text, source = self.summary('好的。\n## 更新\n已修复本轮问题，改动已写入工作区：\n- 修复摘要被旧历史索引覆盖的问题。')
        self.assertEqual(text, '改进监控面板：修复摘要被旧历史索引覆盖的问题。')
        self.assertEqual(source, '最近回复')

    def test_includes_explicit_current_step(self):
        text, source = self.summary('已经完成接口实现。', plan=[{'step': '验证桌面与网页摘要一致', 'status': 'in_progress'}])
        self.assertIn('已经完成接口实现', text)
        self.assertIn('当前计划步骤：验证桌面与网页摘要一致', text)
        self.assertIn('结构化计划', source)

    def test_preserves_blocker_and_negation(self):
        text, _ = self.summary('已实现摘要。\n不过测试仍未通过，暂不能发布。')
        self.assertIn('已实现摘要；不过测试仍未通过，暂不能发布', text)

    def test_completed_turn_does_not_claim_task_completion(self):
        text, _ = self.summary(status='completed')
        self.assertIn('本轮已结束', text)
        self.assertNotIn('任务已完成', text)

    def test_ignores_stale_plan_for_completed_turn(self):
        text, source = self.summary('已通过验证。', status='completed', plan=[{'step': '运行旧测试', 'status': 'in_progress'}])
        self.assertNotIn('运行旧测试', text)
        self.assertNotIn('结构化计划', source)

    def test_no_progress_has_explicit_fallback(self):
        text, source = self.summary('好的。')
        self.assertIn('尚无可概括的进展记录', text)
        self.assertEqual(source, '任务名称与状态')

    def test_uses_only_public_progress_timeline(self):
        text, source = self.summary('好的。', timeline=[{'kind': '进展', 'text': '正在验证摘要展示。'}, {'kind': '执行命令', 'text': 'PRIVATE_COMMAND'}])
        self.assertIn('正在验证摘要展示', text)
        self.assertNotIn('PRIVATE_COMMAND', text)
        self.assertEqual(source, '本轮进展记录')

    def test_strips_markup_artifacts_and_code(self):
        text, _ = self.summary('## 结果\n已生成**中文演讲稿**。\n打印版：:codex-file-citation{path="PRIVATE_PATH"}\n```sh\nPRIVATE_COMMAND\n```\n[说明](https://example.com/private)')
        self.assertIn('已生成中文演讲稿', text)
        for token in ['**', 'PRIVATE_', 'https://', '```', 'codex-file-citation']:
            self.assertNotIn(token, text)

    def test_never_cuts_a_long_sentence_before_its_qualifier(self):
        text, _ = self.summary('完成了' + '多项调整' * 50 + '，但测试尚未通过。')
        self.assertIn('请展开原文', text)
        self.assertNotIn('完成了多项', text)
        self.assertLess(len(text), 240)

    def test_stale_record_is_not_presented_as_live_work(self):
        text, _ = self.summary('正在运行验证。', status='unknown')
        self.assertIn('最近记录：正在运行验证', text)

    def test_failure_stays_visible_even_after_positive_message(self):
        text, _ = self.summary('已经写入修改。', status='failed')
        self.assertIn('本轮失败', text)

    def test_no_newlines_and_preserves_decimals(self):
        text, _ = self.summary('好的。\n诊断发现误差为 0.25，并未达到目标。')
        self.assertNotIn('\n', text)
        self.assertIn('0.25', text)
        self.assertEqual(text.count('。'), 1)

    def test_required_restart_beats_incidental_backup(self):
        text, _ = self.summary('原配置已备份。等当前任务结束后，重启应用，让任务重新加载设置。', status='completed')
        self.assertIn('重启应用', text)
        self.assertNotIn('原配置已备份', text)


if __name__ == '__main__':
    unittest.main()
