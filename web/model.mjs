export const statuses = {
  active: { label: '运行中', color: '#10893e', rank: 2 },
  waiting: { label: '等待输入', color: '#c27c00', rank: 0 },
  failed: { label: '失败', color: '#d13438', rank: 1 },
  completed: { label: '本轮结束', color: '#0078d4', rank: 6 },
  interrupted: { label: '已中断', color: '#8a8886', rank: 5 },
  offline: { label: '连接异常', color: '#8a8886', rank: 3 },
  unknown: { label: '待确认', color: '#8a8886', rank: 4 }
};
export function statusOf(task, now = Date.now()) {
  if (['active', 'waiting'].includes(task.status) && now / 1000 - task.lastActivity > 300) return 'unknown';
  return Object.hasOwn(statuses, task.status) ? task.status : 'unknown';
}
export function selectTasks(tasks, { project = '', host = '', status = 'recent', query = '' } = {}, now = Date.now()) {
  return tasks.filter(t => !project || t.project === project)
    .filter(t => !host || t.host === host)
    .filter(t => !query || `${t.project} ${t.host} ${t.title} ${t.summary} ${t.oneLineSummary || ''}`.toLowerCase().includes(query.trim().toLowerCase()))
    .filter(t => {
      const s = statusOf(t, now);
      if (status === 'all') return true;
      if (status === 'recent') return now / 1000 - t.lastActivity < 7 * 86400 || ['active', 'waiting'].includes(s);
      if (status === 'attention') return ['waiting', 'failed'].includes(s);
      if (status === 'unknown') return ['unknown', 'offline'].includes(s);
      return s === status;
    }).sort((a, b) => statuses[statusOf(a, now)].rank - statuses[statusOf(b, now)].rank || b.lastActivity - a.lastActivity);
}
export function planProgress(plan = []) {
  return plan.length ? { done: plan.filter(p => p.status === 'completed').length, total: plan.length } : null;
}
export function age(seconds, now = Date.now()) {
  if (!seconds) return '暂无活动';
  const delta = Math.max(0, Math.floor(now / 1000 - seconds));
  return delta < 60 ? `${delta} 秒前` : delta < 3600 ? `${Math.floor(delta / 60)} 分钟前` : delta < 86400 ? `${Math.floor(delta / 3600)} 小时前` : `${Math.floor(delta / 86400)} 天前`;
}
export function overviewOf(task) {
  return task.oneLineSummary || '当前记录暂无一句话概览。';
}
