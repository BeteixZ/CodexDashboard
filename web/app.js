import { statuses, statusOf, selectTasks, planProgress, age, overviewOf } from './model.mjs';

const $ = id => document.getElementById(id);
const node = (tag, text = '', className = '') => {
  const el = document.createElement(tag);
  el.textContent = text;
  if (className) el.className = className;
  return el;
};
const state = { snapshot: null, project: '', host: '', status: 'recent', query: '', selected: '', key: '', busy: false, connected: false };
try { state.key = sessionStorage.getItem('codex-monitor-key') || ''; } catch { }
const fragmentKey = new URLSearchParams(location.hash.slice(1)).get('token');
if (fragmentKey) { state.key = fragmentKey; history.replaceState(null, '', location.pathname); }

function statusBadge(task) {
  const status = statusOf(task);
  const badge = node('span', '', 'status-label');
  badge.append(node('span', '', `dot ${status}`), node('span', statuses[status].label));
  return badge;
}
function clock(seconds) {
  return seconds ? new Date(seconds * 1000).toLocaleString(undefined, { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit' }) : '—';
}
function updateOptions(select, values) {
  const current = select.value;
  if (JSON.stringify(Array.from(select.options).slice(1).map(o => o.value)) === JSON.stringify(values)) return;
  while (select.options.length > 1) select.remove(1);
  for (const value of values) { const option = node('option', value); option.value = value; select.append(option); }
  select.value = values.includes(current) ? current : '';
}
function render() {
  const data = state.snapshot;
  if (!data) return;
  const now = Date.now();
  const tasks = data.tasks || [];
  const projectNames = [...new Set(tasks.map(t => t.project))].sort((a, b) => a.localeCompare(b));
  if (state.project && !projectNames.includes(state.project)) state.project = '';
  const nav = document.createDocumentFragment();
  for (const project of ['', ...projectNames]) {
    const matching = tasks.filter(t => !project || t.project === project);
    const active = matching.filter(t => statusOf(t, now) === 'active').length;
    const button = node('button', '', `nav-item${project === state.project ? ' active' : ''}`);
    button.append(node('span', project || '全部项目', 'project-name'), node('small', active ? `${active} 运行` : `${matching.length}`));
    button.setAttribute('aria-pressed', String(project === state.project));
    button.onclick = () => { state.project = project; render(); };
    nav.append(button);
  }
  $('projects').replaceChildren(nav);
  const hosts = data.hosts || [];
  updateOptions($('host-filter'), hosts.map(h => h.name));
  if (state.host && !hosts.some(h => h.name === state.host)) state.host = '';
  $('hosts').replaceChildren(...hosts.map(h => {
    const row = node('div', '', 'host-row');
    row.title = h.error || `最近读取 ${h.lastSuccess ? new Date(h.lastSuccess).toLocaleTimeString() : '—'}`;
    row.append(node('span', '', `dot ${h.connected ? 'active' : 'unknown'}`), node('span', h.name));
    return row;
  }));
  $('active-count').textContent = tasks.filter(t => statusOf(t, now) === 'active').length;
  $('attention-count').textContent = tasks.filter(t => ['waiting', 'failed'].includes(statusOf(t, now)) && now / 1000 - t.lastActivity < 86400).length;
  $('completed-count').textContent = tasks.filter(t => statusOf(t, now) === 'completed' && t.completedAt && new Date(t.completedAt * 1000).toDateString() === new Date(now).toDateString()).length;
  $('source-count').textContent = `${hosts.filter(h => h.connected).length} / ${hosts.length} 台机器可读取`;
  const readAge = data.readAt ? (now - Date.parse(data.readAt)) / 1000 : Infinity;
  const stale = !state.connected || readAge > Math.max(35, (data.refreshSeconds || 10) * 3);
  const partial = hosts.some(h => !h.connected);
  $('connection-status').classList.toggle('warning', stale || partial);
  $('connection-status').textContent = !state.connected ? '与监控台连接中断，显示上次读取的内容。正在重试…'
    : stale ? '数据已过期，桌面监控程序可能已暂停或离线。'
    : partial ? '部分机器连接异常；相应任务显示上次成功读取的内容。'
    : '已连接监控台。任务状态依据会话记录，长时间无更新会标为待确认。';
  $('updated').textContent = data.readAt ? `最近读取 ${new Date(data.readAt).toLocaleTimeString()}` : '等待首次采集';
  const visible = selectTasks(tasks, state, now);
  $('task-count').textContent = `${visible.length} 个任务`;
  $('empty').hidden = visible.length > 0;
  if (!visible.some(t => t.key === state.selected)) state.selected = visible[0]?.key || '';
  const focusedKey = document.activeElement?.dataset?.key;
  $('tasks').replaceChildren(...visible.map(task => {
    const button = node('button', '', `task-row${task.key === state.selected ? ' selected' : ''}`);
    button.dataset.key = task.key;
    button.setAttribute('aria-pressed', String(task.key === state.selected));
    const top = node('div', '', 'task-top');
    top.append(node('span', `${task.project} / ${task.host}`, 'task-project'), statusBadge(task));
    const preview = node('p', overviewOf(task), 'task-preview');
    preview.title = overviewOf(task);
    button.append(top, node('div', task.title, 'task-title'), preview, node('div', age(task.lastActivity), 'task-age'));
    button.onclick = () => { state.selected = task.key; document.querySelector('.content').classList.add('show-detail'); render(); };
    return button;
  }));
  if (focusedKey) Array.from($('tasks').children).find(el => el.dataset.key === focusedKey)?.focus({ preventScroll: true });
  renderDetail(visible.find(t => t.key === state.selected));
}
let detailSignature = '';
function renderDetail(task) {
  const signature = JSON.stringify([task, task ? statusOf(task) : null, task ? age(task.lastActivity) : null]);
  if (signature === detailSignature) return;
  const oldScroll = $('detail').scrollTop;
  const sameTask = $('detail').dataset.key === task?.key;
  detailSignature = signature;
  if (!task) { $('detail').replaceChildren(node('p', '选择任务查看进展', 'empty')); return; }
  const back = node('button', '← 返回列表', 'back-button');
  back.onclick = () => document.querySelector('.content').classList.remove('show-detail');
  const meta = node('div', '', 'detail-meta');
  meta.append(statusBadge(task), node('span', age(task.lastActivity), 'caption'));
  const overview = node('section', '', 'overview-card');
  overview.append(node('h3', '一句话概览'), node('p', overviewOf(task), 'summary'), node('p', task.summarySource || '依据会话记录整理', 'caption overview-source'));
  const original = node('details', '', 'original-message');
  original.append(node('summary', '最近回复原文'), node('p', task.summary || '本轮尚未记录进展说明。', 'summary'));
  original.open = sameTask && Boolean($('detail').querySelector('.original-message')?.open);
  const planSection = node('section');
  const progress = planProgress(task.plan);
  planSection.append(node('h3', progress ? `当前计划 · ${progress.done} / ${progress.total} 步` : '当前计划'));
  if (progress) {
    const bar = document.createElement('progress'); bar.max = progress.total; bar.value = progress.done;
    bar.setAttribute('aria-label', `当前计划完成 ${progress.done} 步，共 ${progress.total} 步`);
    const list = node('ul', '', 'plan');
    for (const step of task.plan) {
      const row = node('li'); row.append(node('span', step.status === 'completed' ? '✓' : step.status === 'in_progress' ? '●' : '○', 'completed-mark'), node('span', step.step)); list.append(row);
    }
    planSection.append(bar, list);
  } else planSection.append(node('p', '暂无结构化计划', 'caption'));
  const timelineSection = node('section'); timelineSection.append(node('h3', '进展时间线'));
  const timeline = node('ol', '', 'timeline');
  for (const entry of (task.timeline || []).slice().reverse()) {
    const row = node('li'); row.append(node('time', clock(entry.time)), node('p', entry.text)); timeline.append(row);
  }
  timelineSection.append(timeline);
  if (!timeline.children.length) timelineSection.append(node('p', '本轮尚无可显示的执行记录', 'caption'));
  $('detail').dataset.key = task.key;
  $('detail').replaceChildren(back, node('p', `${task.project} / ${task.host}`, 'eyebrow'), node('h2', task.title), meta, overview, original, planSection, timelineSection,
    node('p', '来源：会话记录。未写入记录的审批或输入请求可能无法识别。', 'caption'));
  $('detail').scrollTop = sameTask ? oldScroll : 0;
}
function disconnectedLogin(message = '') {
  state.key = ''; state.connected = false; state.snapshot = null;
  try { sessionStorage.removeItem('codex-monitor-key'); } catch { }
  $('workspace').hidden = true; $('login').hidden = false; $('login-error').textContent = message;
  $('access-key').value = '';
  try { $('access-key').focus(); } catch { }
}
async function refresh(fromLogin = false) {
  if (state.busy || !state.key) return;
  state.busy = true;
  const requestKey = state.key;
  $('connect').disabled = true;
  const controller = new AbortController();
  const timeout = setTimeout(() => controller.abort(), 8000);
  try {
    const response = await fetch('/api/snapshot', { headers: { Authorization: `Bearer ${state.key}` }, cache: 'no-store', credentials: 'omit', signal: controller.signal });
    if (state.key !== requestKey) return;
    if (response.status === 401) { disconnectedLogin('访问密钥不正确或已在桌面端更新，请重新输入。'); return; }
    if (!response.ok) throw new Error(`连接失败（${response.status}）`);
    const data = await response.json();
    if (state.key !== requestKey) return;
    if (data.schemaVersion !== 1 || !Array.isArray(data.tasks) || !Array.isArray(data.hosts)) throw new Error('数据格式不兼容');
    state.snapshot = data; state.connected = true;
    try { sessionStorage.setItem('codex-monitor-key', state.key); } catch { }
    $('access-key').value = ''; $('login').hidden = true; $('workspace').hidden = false;
    render();
  } catch (error) {
    state.connected = false;
    if (fromLogin || !state.snapshot) $('login-error').textContent = error.name === 'AbortError' ? '连接超时，请检查桌面程序和网络。' : error.message;
    if (state.snapshot) render();
  } finally { clearTimeout(timeout); state.busy = false; $('connect').disabled = false; }
}
$('login-form').onsubmit = e => { e.preventDefault(); state.key = $('access-key').value.trim(); refresh(true); };
$('logout').onclick = () => disconnectedLogin();
$('refresh').onclick = () => refresh();
$('search').oninput = e => { state.query = e.target.value; render(); };
$('host-filter').onchange = e => { state.host = e.target.value; render(); };
$('status-filter').onchange = e => { state.status = e.target.value; render(); };
document.addEventListener('visibilitychange', () => { if (!document.hidden) refresh(); });
setInterval(() => { if (!document.hidden) refresh(); }, 5000);
if (state.key) refresh(true);
