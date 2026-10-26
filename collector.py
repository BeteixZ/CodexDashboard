"""Read-only Codex history adapter. Emits only allowlisted, user-visible fields.

No credentials, reasoning, shell output, or model calls are read into the result.
The adapter deliberately reports recorded state, not app-server liveness.
"""
import argparse
import datetime as dt
import hashlib
import json
import os
import pathlib
import re
import sqlite3
import stat
import sys
import time
from typing import Any, Dict, Iterator, List, Optional, Set, Tuple, Union

MAX_TAIL = 1024 * 1024
MAX_REPORT = 64000
ITEM_TYPES = ('agentMessage', 'plan', 'commandExecution', 'fileChange',
              'mcpToolCall', 'dynamicToolCall', 'webSearch', 'collabToolCall')
LABELS = {'commandExecution': '执行命令', 'fileChange': '修改文件',
          'mcpToolCall': '调用工具', 'dynamicToolCall': '调用工具',
          'webSearch': '检索资料', 'collabToolCall': '协调子任务', 'plan': '更新计划'}


def linux_directory_identity(path: str) -> Tuple[str, str]:
    """Resolve the opened directory itself, including symlinks, without listing it."""
    if not path.startswith('/') or any(ord(c) < 32 for c in path):
        raise ValueError('directory_path_invalid')
    fd = os.open(path, os.O_PATH | os.O_DIRECTORY | os.O_CLOEXEC)
    try:
        info = os.fstat(fd)
        canonical = os.readlink('/proc/self/fd/' + str(fd))
        current = os.stat(path)
        if (not stat.S_ISDIR(info.st_mode) or not canonical.startswith('/')
                or canonical.endswith(' (deleted)') or any(ord(c) < 32 for c in canonical)
                or (current.st_dev, current.st_ino) != (info.st_dev, info.st_ino)):
            raise ValueError('directory_changed')
        return canonical, format(info.st_dev, 'x') + ':' + format(info.st_ino, 'x')
    finally:
        os.close(fd)


def project_source_identity(home: pathlib.Path) -> Optional[str]:
    if sys.platform != 'linux':
        return None
    try:
        machine = pathlib.Path('/etc/machine-id').read_text(encoding='ascii').strip()
        if not re.fullmatch('[0-9a-f]{32}', machine) or machine == '0' * 32:
            return None
        directory, identity = linux_directory_identity(str(home.absolute()))
        # No raw machine id, uid or Codex configuration directory is exported.
        source = json.dumps([machine, os.getuid(), directory, identity], separators=(',', ':'))
        return hashlib.sha256(source.encode('utf-8')).hexdigest()
    except (OSError, ValueError, UnicodeError):
        return None


def project_directory_evidence(cwd: str, source: Optional[str]) -> Optional[Dict[str, Any]]:
    if source is None:
        return None
    try:
        canonical, identity = linux_directory_identity(cwd)
        return {'version': 1, 'kind': 'linux-directory/v1', 'sourceIdentity': source,
                'requestedDirectory': cwd, 'canonicalDirectory': canonical, 'directoryIdentity': identity}
    except (OSError, ValueError):
        return None


def collect_project_directories(home: str, directories: List[str], request_id: str,
                                now: Optional[float] = None) -> Dict[str, Any]:
    """Read configured directory identities without opening history databases or logs."""
    if not home or not request_id or not directories:
        raise ValueError('explicit_project_probe_scope_required')
    source = project_source_identity(pathlib.Path(home))
    results = []
    for directory in dict.fromkeys(directories):
        proof = project_directory_evidence(directory, source)
        results.append({'requestedDirectory': directory, 'status': 'observed' if proof else 'unavailable',
                        'evidence': proof})
    return {'schemaVersion': 1, 'kind': 'project-directory-snapshot/v1', 'requestId': request_id,
            'observedAt': time.time() if now is None else now, 'directories': results}


def ro_db(path: Union[pathlib.Path, str]) -> sqlite3.Connection:
    target = pathlib.Path(path)
    connection = sqlite3.connect(target.resolve().as_uri() + '?mode=ro', uri=True, timeout=2)
    connection.row_factory = sqlite3.Row
    connection.execute('PRAGMA query_only = ON')
    return connection


def timestamp(value: Any, milliseconds: bool = False) -> float:
    if isinstance(value, (int, float)):
        return value / 1000 if milliseconds or value > 100000000000 else float(value)
    try:
        return dt.datetime.fromisoformat(str(value).replace('Z', '+00:00')).timestamp()
    except (ValueError, AttributeError, TypeError):
        return 0.0


def clean(value: Any, limit: int = 4000) -> str:
    return str(value or '').replace('\x00', '')[:limit]


def report_fields(text: Any, turn_id: str, when: float, source: str) -> Dict[str, Any]:
    """Only call for an explicitly public final/completed-turn reply."""
    value = text.replace('\x00', '') if isinstance(text, str) else ''
    return {'finalReport': value[:MAX_REPORT], 'finalReportTurnId': turn_id,
            'finalReportAt': when, 'finalReportSource': source,
            'finalReportTruncated': len(value) > MAX_REPORT}


def summary_sentences(value: Any) -> List[str]:
    """Extract complete prose units; never turn a character prefix into a summary."""
    text = clean(value)
    text = re.sub(r'```[\s\S]*?(?:```|$)|~~~[\s\S]*?(?:~~~|$)', '', text)
    text = re.sub(r'<oai-mem-citation>[\s\S]*?(?:</oai-mem-citation>|$)', '', text)
    text = re.sub(r':{1,2}[\w-]+\{[^}]*\}', '', text)
    text = re.sub(r'!?\[([^\]]*)\]\((?:<[^>]*>|[^)])*\)', r'\1', text)
    text = re.sub(r'https?://\S+', '', text)
    text = re.sub(r'\*\*|__|`', '', text)
    result = []
    for line in text.splitlines():
        if re.match(r'^\s*(?:#{1,6}\s|\||---+$)', line):
            continue
        line = re.sub(r'^\s*(?:[-*+]\s+(?:\[[ xX]\]\s*)?|\d+[.)、]\s*|>\s*)', '', line)
        for sentence in re.split(r'(?<=[。！？!?])\s*|(?<=[.!?])\s+(?=[A-Z])', line):
            sentence = re.sub(r'\s+', ' ', sentence).strip(' \t。！？!?；;')
            if not sentence or re.fullmatch(r'(?:好的?|可以|没问题|收到|完成|已完成|是的|Okay|OK|Done)[,.，。!！ ]*', sentence, re.I):
                continue
            if re.match(r'^(?:[A-Za-z]:[/\\]|[/\\](?:home|Users|tmp|mnt)[/\\]|(?:打印版|下载|文件|来源|参考链接|Sources?)\s*[:：])', sentence, re.I):
                continue
            if re.fullmatch(r'[\w.-]+\.(?:cs|py|js|ts|tsx|md|pdf|tex|xaml|json|toml)', sentence, re.I):
                continue
            if len(sentence) >= 4:
                result.append(sentence)
    return result


def task_overview(title: str, message: str, plan: List[Dict[str, Any]], timeline: List[Dict[str, Any]], status: str) -> Tuple[str, str]:
    """Locally compose an attributed, one-sentence overview from public records."""
    title_parts = summary_sentences(title)
    subject = next((s.rstrip('.:：') for s in title_parts if len(s) <= 56), '该任务')
    if len(title_parts) > 1:  # A pasted opening request is not a curated task name.
        subject = '该任务'
    source = '最近回复'
    candidates = summary_sentences(message)
    if not candidates:
        # Only current-turn progress is supplied here. Tool metadata is not prose.
        for event in reversed(timeline):
            if event.get('kind') == '进展':
                candidates = summary_sentences(event.get('text'))
                if candidates:
                    source = '本轮进展记录'
                    break
    # Keep short sentences whole. In particular, do not cut away a later negation
    # or qualifier just to satisfy a cosmetic character limit.
    def score(pair):
        index, text = pair
        blocked = bool(re.search(r'未通过|尚未|仍未|仍需|还需|暂不能|无法|受阻|失败|超时|等待|待确认|需要重启|重启.*(?:生效|加载)|blocked|failed|not yet|still need', text, re.I))
        current = bool(re.search(r'正在|目前|现在|当前|下一步|接下来|currently|now |next ', text, re.I))
        concrete = bool(re.search(r'修复|验证|测试|通过|完成|实现|发现|确认|检查|训练|诊断|支持|已|fixed|passed|verified|implemented', text, re.I))
        topic = {subject[i:i + 2] for i in range(len(subject) - 1) if re.fullmatch(r'[\u4e00-\u9fff]{2}', subject[i:i + 2])}
        overlap = min(3, sum(part in text for part in topic)) if subject != '该任务' else 0
        auxiliary = bool(re.search(r'^(?:原配置|备份|打印版|下载链接|参考资料|来源标记)', text))
        return (4 if blocked else 0) + (3 if current and status in ('active', 'waiting') else 0) + (2 if concrete else 0) + overlap * .7 - (3 if text.endswith((':', '：')) or auxiliary else 0) - index * .15
    ranked = sorted(((i, s) for i, s in enumerate(candidates) if len(s) <= 180), key=score, reverse=True)
    detail = ranked[0][1] if ranked else ''
    # Include a nearby caveat if a preceding completion claim would otherwise be
    # presented alone. Sentence separators become a single compound sentence.
    if ranked and ranked[0][0] > 0:
        previous = candidates[ranked[0][0] - 1]
        if len(previous) + len(detail) < 120 and re.match(r'^(?:但|不过|然而|仍|还|尚|But\b|However\b)', detail, re.I):
            detail = previous + '；' + detail
    active_step = next((s for p in plan if p.get('status') == 'in_progress'
                        for s in summary_sentences(p.get('step')) if len(s) <= 100), '')
    if status in ('active', 'waiting') and active_step:
        step_detail = '当前计划步骤：' + active_step
        if not detail:
            detail, source = step_detail, '结构化计划'
        elif active_step not in detail and len(detail) + len(step_detail) < 125:
            detail += '；' + step_detail
            source += ' + 结构化计划'
    if not detail:
        detail = '最近回复较长，请展开原文查看具体进展' if candidates else {
            'active': '本轮运行中，尚无可概括的进展记录',
            'waiting': '等待输入，尚无更具体的进展记录',
            'completed': '本轮已结束，尚无可概括的结果记录',
            'failed': '本轮失败，记录中暂无具体原因',
            'interrupted': '本轮已中断',
        }.get(status, '进展待确认，尚无可概括的记录')
        source = '最近回复' if candidates else '任务名称与状态'
    # Express only recorded facts; never equate a finished turn with completion
    # of the whole task, and never present stale records as current execution.
    prefix = '最近记录：' if status == 'unknown' else '本轮已中断，最近记录：' if status == 'interrupted' and candidates else '本轮失败，最近记录：' if status == 'failed' and candidates else ''
    detail = detail.rstrip('.。！？!?；;')
    text = ('' if detail.startswith(subject) else subject + '：') + prefix + detail + '。'
    return text, source


def tail_events(path: pathlib.Path) -> Iterator[Dict[str, Any]]:
    try:
        with path.open('rb') as stream:
            size = os.fstat(stream.fileno()).st_size
            stream.seek(max(0, size - MAX_TAIL))
            lines = stream.read(MAX_TAIL).splitlines()
        for line in lines:
            try:
                obj = json.loads(line)
                if isinstance(obj, dict):
                    yield obj
            except (ValueError, UnicodeError):
                continue  # A concurrently written last line or truncated first line.
    except OSError:
        return


def steps_from(value: Any) -> List[Dict[str, str]]:
    if isinstance(value, list):
        return [{'step': clean(s.get('step'), 300), 'status': s.get('status', 'pending')}
                for s in value if isinstance(s, dict) and s.get('step')][:50]
    if isinstance(value, str):
        return [{'step': clean(m.group(2), 300),
                 'status': 'completed' if m.group(1).lower() == 'x' else 'pending'}
                for m in re.finditer(r'^\s*[-*]\s+\[([ xX])\]\s+(.+)$', value, re.M)][:50]
    return []


def read_tail(path: pathlib.Path, wanted_turn: Optional[str] = None) -> Dict[str, Any]:
    result = {'turnId': '', 'status': '', 'startedAt': 0, 'completedAt': 0,
              'lastActivity': 0, 'message': '', 'messageAt': 0, 'plan': [], 'planAt': 0, 'timeline': [],
              **report_fields('', '', 0, '')}
    current_turn = None
    for event in tail_events(path):
        payload = event.get('payload') or {}
        if not isinstance(payload, dict):
            continue
        kind = payload.get('type')
        when = timestamp(event.get('timestamp'))
        if event.get('type') == 'event_msg' and kind == 'task_started':
            current_turn = payload.get('turn_id')
            if wanted_turn is None or current_turn == wanted_turn:
                result.update(turnId=current_turn or '', status='inProgress',
                              startedAt=timestamp(payload.get('started_at')) or when,
                              completedAt=0, message='', messageAt=0, plan=[], planAt=0, timeline=[],
                              **report_fields('', '', 0, ''))
        if wanted_turn and (payload.get('turn_id') or current_turn) not in (None, wanted_turn):
            continue
        result['lastActivity'] = max(result['lastActivity'], when)
        if event.get('type') == 'event_msg':
            if kind in ('task_complete', 'turn_aborted'):
                result['turnId'] = payload.get('turn_id') or result['turnId']
                result['status'] = 'interrupted' if kind == 'turn_aborted' else ('failed' if payload.get('error') else 'completed')
                result['completedAt'] = timestamp(payload.get('completed_at')) or when
                if payload.get('last_agent_message'):
                    result['message'] = clean(payload['last_agent_message'])
                    result['messageAt'] = when
                    if kind == 'task_complete' and not payload.get('error') and result['turnId'] and not (
                            result['finalReport'] and result['finalReportTurnId'] == result['turnId']):
                        result.update(report_fields(payload['last_agent_message'], result['turnId'], when, '本轮完成记录中的回复'))
            elif kind == 'agent_message':
                result['message'] = clean(payload.get('message'))
                result['messageAt'] = when
            elif kind in ('plan_update', 'plan_updated'):
                result['plan'] = steps_from(payload.get('plan'))
                result['planAt'] = when
        elif event.get('type') == 'response_item':
            if kind == 'message' and payload.get('role') == 'assistant':
                # Explicitly exclude analysis-channel content.
                if payload.get('phase') in ('commentary', 'final_answer', 'final'):
                    message = '\n'.join(x.get('text', '') for x in payload.get('content', [])
                                        if isinstance(x, dict) and x.get('type') in ('output_text', 'text'))
                    if message:
                        result['message'] = clean(message)
                        result['messageAt'] = when
                        result['timeline'].append({'time': when, 'kind': '进展', 'text': clean(message, 1600)})
                        report_turn = payload.get('turn_id') or current_turn
                        if payload.get('phase') in ('final_answer', 'final') and report_turn and (
                                wanted_turn is None or report_turn == wanted_turn):
                            result.update(report_fields(message, report_turn, when, '本轮最终回复'))
            elif kind == 'function_call' and payload.get('name', '').split('.')[-1] == 'update_plan':
                try:
                    result['plan'] = steps_from(json.loads(payload.get('arguments', '{}')).get('plan'))
                    result['planAt'] = when
                except (ValueError, AttributeError):
                    pass
    result['timeline'] = result['timeline'][-16:]
    return result


def summarize_item(item: Dict[str, Any], created_at: float) -> Optional[Dict[str, Any]]:
    kind = item.get('type')
    if kind == 'agentMessage':
        return {'time': created_at, 'kind': '进展', 'text': clean(item.get('text'), 1600)}
    if kind in LABELS:
        status = item.get('status', '')
        suffix = {'completed': '已完成', 'inProgress': '进行中', 'failed': '失败',
                  'declined': '已拒绝'}.get(status, '')
        text = LABELS[kind] + (' · ' + suffix if suffix else '')
        if kind == 'fileChange':
            files = [clean(x.get('path'), 180) for x in item.get('changes', []) if isinstance(x, dict)]
            text += '\n' + '\n'.join(files[:6])
        return {'time': created_at, 'kind': LABELS[kind], 'text': text}
    return None


def classify(status: str, last_activity: float, now: float, waiting: bool = False) -> str:
    if status in ('completed', 'failed', 'interrupted'):
        return {'completed': 'completed', 'failed': 'failed', 'interrupted': 'interrupted'}[status]
    if status == 'inProgress':
        if now - last_activity > 300:
            return 'unknown'
        return 'waiting' if waiting else 'active'
    return 'unknown'


def collect(home: Optional[Union[str, pathlib.Path]] = None, limit: int = 120, now: Optional[float] = None) -> Dict[str, Any]:
    now = now or time.time()
    home = pathlib.Path(home or os.environ.get('CODEX_HOME') or pathlib.Path.home() / '.codex')
    project_source = project_source_identity(home)
    state_path = home / 'state_5.sqlite'
    if not state_path.exists():
        raise RuntimeError('未找到 Codex state_5.sqlite，请检查 Codex 数据目录')
    state = ro_db(state_path)
    history = None
    warnings = []
    try:
        cols = {r['name'] for r in state.execute('PRAGMA table_info(threads)')}
        required = {'id', 'cwd', 'title', 'rollout_path', 'updated_at', 'archived'}
        if not required.issubset(cols):
            raise RuntimeError('Codex 数据格式不兼容，需更新采集器')
        fields = ['id', 'cwd', 'title', 'rollout_path', 'updated_at']
        fields += [x for x in ('name', 'model', 'project_id', 'source', 'agent_path', 'history_mode') if x in cols]
        children = set()
        tables = {r[0] for r in state.execute("SELECT name FROM sqlite_master WHERE type='table'")}
        if 'thread_spawn_edges' in tables:
            children = {r[0] for r in state.execute('SELECT child_thread_id FROM thread_spawn_edges')}
        projects = {}
        if 'projects' in tables:
            projects = {r['id']: r['name'] for r in state.execute('SELECT id,name FROM projects')}
        candidates = state.execute('SELECT ' + ','.join(fields) + ' FROM threads WHERE archived=0 ORDER BY updated_at DESC').fetchall()
        rows = []
        for row in candidates:
            row = dict(row)
            source = str(row.get('source', '')).lower()
            if row['id'] in children or 'subagent' in source or 'sub_agent' in source or source == 'exec':
                continue
            rows.append(row)
            if len(rows) >= limit:
                break
        truncated = len(rows) == limit
        hp = home / 'thread_history_1.sqlite'
        if hp.exists():
            try:
                history = ro_db(hp)
                history.execute('SELECT thread_id, turn_id, status FROM thread_turns LIMIT 0')
                history.execute('SELECT item_type, item_json FROM thread_items LIMIT 0')
            except sqlite3.Error:
                if history:
                    history.close()
                history = None
                warnings.append('历史索引格式不可用，使用会话记录')
        tasks = []
        for row in rows:
            path = pathlib.Path(row['rollout_path'])
            turn = None
            if history:
                turn = history.execute('SELECT turn_id,status,started_at,completed_at FROM thread_turns WHERE thread_id=? ORDER BY rollout_ordinal DESC LIMIT 1', (row['id'],)).fetchone()
            tail = read_tail(path)
            if turn and tail['turnId'] and tail['turnId'] != turn['turn_id']:
                # New turns can reach the append-only log before the history projection.
                if max(tail['startedAt'], tail['completedAt']) > timestamp(turn['started_at']):
                    turn = {'turn_id': tail['turnId'], 'status': tail['status'],
                            'started_at': tail['startedAt'], 'completed_at': tail['completedAt']}
                else:
                    tail = read_tail(path, turn['turn_id'])
            # A tail may be ahead of SQLite while the projection is still catching up.
            if turn and tail['turnId'] == turn['turn_id'] and tail['status'] in ('completed', 'failed', 'interrupted'):
                turn = dict(turn)
                turn['status'] = tail['status']
                turn['completed_at'] = tail['completedAt']
            raw_status = turn['status'] if turn else tail['status']
            turn_id = turn['turn_id'] if turn else tail['turnId']
            started = timestamp(turn['started_at']) if turn else tail['startedAt']
            completed = timestamp(turn['completed_at']) if turn else tail['completedAt']
            last_activity = max(timestamp(row['updated_at']), tail['lastActivity'], started, completed)
            message = tail['message']
            report = {key: tail[key] for key in report_fields('', '', 0, '')}
            message_at = tail['messageAt']
            timeline = []
            plan = tail['plan']
            plan_at = tail['planAt']
            activity = ''
            waiting = False
            if turn and history:
                placeholders = ','.join('?' for _ in ITEM_TYPES)
                items = history.execute('SELECT item_json,created_at_ms FROM thread_items WHERE thread_id=? AND turn_id=? AND item_type IN (' + placeholders + ') ORDER BY rollout_ordinal DESC LIMIT 60', (row['id'], turn_id, *ITEM_TYPES)).fetchall()
                for itemrow in reversed(items):
                    try:
                        item = json.loads(itemrow['item_json'])
                    except (ValueError, TypeError):
                        continue
                    created = timestamp(itemrow['created_at_ms'], milliseconds=True)
                    last_activity = max(last_activity, created)
                    summary = summarize_item(item, created)
                    if summary:
                        timeline.append(summary)
                    if item.get('type') == 'agentMessage' and item.get('text') and created >= message_at:
                        message = clean(item['text'])
                        message_at = created
                    elif item.get('type') == 'plan':
                        parsed = steps_from(item.get('plan') or item.get('text'))
                        if parsed and created >= plan_at:
                            plan = parsed
                            plan_at = created
                    elif item.get('type') in LABELS:
                        activity = LABELS[item['type']]
                        if item.get('status') == 'inProgress':
                            tool = item.get('tool') or item.get('name') or ''
                            if 'request_user_input' in str(tool):
                                waiting = True
                latest_message = history.execute("SELECT item_json,created_at_ms FROM thread_items WHERE thread_id=? AND turn_id=? AND item_type='agentMessage' ORDER BY rollout_ordinal DESC LIMIT 1", (row['id'], turn_id)).fetchone()
                if latest_message:
                    try:
                        text = json.loads(latest_message['item_json']).get('text')
                        if text and timestamp(latest_message['created_at_ms'], milliseconds=True) >= message_at:
                            message = clean(text)
                    except (ValueError, TypeError, AttributeError):
                        pass
                # Read public final messages for this exact turn, independently of
                # the 4,000-character overview and the most recent commentary.
                final_items = history.execute("SELECT item_json,created_at_ms FROM thread_items WHERE thread_id=? AND turn_id=? AND item_type='agentMessage' ORDER BY rollout_ordinal DESC LIMIT 60", (row['id'], turn_id)).fetchall()
                for final_item in final_items:
                    try:
                        final = json.loads(final_item['item_json'])
                        if final.get('phase') in ('final', 'final_answer') and isinstance(final.get('text'), str) and final['text']:
                            created = timestamp(final_item['created_at_ms'], milliseconds=True)
                            if not report['finalReport'] or created > report['finalReportAt'] or report['finalReportSource'] != '本轮最终回复':
                                report = report_fields(final['text'], turn_id, created, '本轮最终回复')
                            break
                    except (ValueError, TypeError, AttributeError):
                        continue
            if raw_status not in ('completed', 'failed', 'interrupted') or report['finalReportTurnId'] != (turn_id or ''):
                report = report_fields('', '', 0, '')
            state_name = classify(raw_status, last_activity, now, waiting)
            project = projects.get(row.get('project_id')) or re.split(r'[/\\]', row['cwd'].rstrip('/\\'))[-1]
            title = clean(row.get('name') or row['title'], 500)
            merged_timeline = sorted(timeline + tail['timeline'], key=lambda e: e['time'])
            overview, overview_source = task_overview(title, message, plan, merged_timeline, state_name)
            tasks.append({'id': row['id'], 'title': title,
                          'project': project, 'cwd': row['cwd'], 'model': row.get('model') or '',
                          'projectEvidence': project_directory_evidence(row['cwd'], project_source),
                          'turnId': turn_id or '', 'status': state_name, 'recordedStatus': raw_status or 'unknown',
                          'lastActivity': last_activity, 'startedAt': started, 'completedAt': completed,
                          'summary': message, 'oneLineSummary': overview, 'summarySource': overview_source,
                          **report,
                          'activity': activity, 'plan': plan,
                          'timeline': (timeline or tail['timeline'])[-20:], 'source': '会话记录'})
        return {'schemaVersion': 1, 'collectedAt': now, 'tasks': tasks, 'warnings': warnings, 'truncated': truncated}
    finally:
        state.close()
        if history:
            history.close()


def main(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument('--home')
    parser.add_argument('--limit', type=int, default=120)
    parser.add_argument('--project-directory', action='append', default=[])
    parser.add_argument('--request-id')
    args = parser.parse_args(argv)
    if args.project_directory and (not args.home or not args.request_id):
        parser.error('--project-directory requires explicit --home and --request-id')
    try:
        result = (collect_project_directories(args.home, args.project_directory, args.request_id)
                  if args.project_directory else collect(args.home, max(1, min(args.limit, 500))))
    except Exception as error:
        result = {'schemaVersion': 1, 'collectedAt': time.time(), 'tasks': [],
                  'error': clean(str(error), 300)}
    print(json.dumps(result, ensure_ascii=True, separators=(',', ':')))


if __name__ == '__main__':
    main()
