import { test } from 'node:test';
import assert from 'node:assert/strict';
import { statusOf, selectTasks, planProgress, overviewOf } from '../web/model.mjs';
const now = 2000000;
const tasks = [
  { key: 'a', title: '运行测试', project: '本机项目', host: 'local', status: 'active', lastActivity: 1990 },
  { key: 'b', title: '检查实现', project: '远程项目', host: 'GPU', status: 'waiting', lastActivity: 1980 },
  { key: 'c', title: '旧记录', project: '本机项目', host: 'local', status: 'active', lastActivity: 1000 },
  { key: 'd', title: '完成', project: '远程项目', host: 'GPU', status: 'completed', lastActivity: 1970 },
];
test('stale running record becomes unknown even if the hub is offline', () => assert.equal(statusOf(tasks[2], now), 'unknown'));
test('completion remains completion after time passes', () => assert.equal(statusOf(tasks[3], now + 99999999), 'completed'));
test('host, project, search, and status filters combine', () => {
  assert.deepEqual(selectTasks(tasks, { host: 'local', project: '本机项目', status: 'active', query: '测试' }, now).map(t => t.key), ['a']);
});
test('pending input sorts before running tasks', () => assert.equal(selectTasks(tasks, { status: 'all' }, now)[0].key, 'b'));
test('unknown filter includes stale activity and disconnects', () => assert.deepEqual(selectTasks([...tasks, { ...tasks[0], key: 'e', status: 'offline' }], { status: 'unknown' }, now).map(t => t.key), ['e', 'c']));
test('no plan never implies zero percent or completion', () => assert.equal(planProgress([]), null));
test('only explicitly completed steps contribute to progress', () => assert.deepEqual(planProgress([{ status: 'completed' }, { status: 'in_progress' }, { status: 'pending' }]), { done: 1, total: 3 }));
test('unrecognized future statuses are displayed as unknown', () => assert.equal(statusOf({ status: 'newState' }, now), 'unknown'));
test('overview uses the same generated sentence as desktop, not message prefix', () => assert.equal(overviewOf({ oneLineSummary: '正在验证摘要一致性。', summary: '好的。原始长回复。' }), '正在验证摘要一致性。'));
test('missing overview has an explicit fallback without inventing a summary', () => assert.equal(overviewOf({ summary: '好的。' }), '当前记录暂无一句话概览。'));
test('search also matches progress present only in the overview', () => assert.equal(selectTasks([{ ...tasks[0], oneLineSummary: '正在检查回归边界' }], { query: '回归边界' }, now).length, 1));
