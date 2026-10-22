"""Isolated Windows console-shutdown/restart test of Agent dispatcher."""
import json
import os
from pathlib import Path
import shutil
import signal
import socket
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parent.parent
CORE = ROOT / 'src/Mezhs.Api/bin/Release/net10.0/Mezhs.Api.dll'
AGENT = ROOT / 'src/Mezhs.Agent.Api/bin/Release/net10.0/Mezhs.Agent.Api.dll'
TMP = Path(tempfile.mkdtemp(prefix='mezhs-t6-graceful-'))
RUNNING = []


def port():
    with socket.socket() as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]


def request(url, data=None):
    body = None if data is None else json.dumps(data).encode('utf-8')
    req = urllib.request.Request(url, data=body, headers={'Content-Type': 'application/json'})
    with urllib.request.urlopen(req, timeout=3) as res:
        return json.loads(res.read()) if res.headers.get('Content-Type', '').startswith('application/json') else True


def await_condition(what, predicate, timeout=30):
    end = time.monotonic() + timeout
    last = None
    while time.monotonic() < end:
        try:
            result = predicate()
            if result:
                return result
        except (urllib.error.URLError, ConnectionError, TimeoutError, ValueError) as exc:
            last = str(exc)
        time.sleep(0.15)
    raise RuntimeError(f'timed out waiting for {what}; last error: {last}')


def start(dll, config, label):
    path = TMP / (label + '.log')
    log = open(path, 'wb')
    try:
        p = subprocess.Popen(['dotnet', str(dll), '--config', str(config)],
                             cwd=ROOT, stdin=subprocess.DEVNULL, stdout=log,
                             stderr=subprocess.STDOUT,
                             creationflags=subprocess.CREATE_NEW_PROCESS_GROUP)
    finally:
        log.close()
    RUNNING.append(p)
    return p


def status(base, execution):
    return request(f'{base}/v1/executions/{execution}')


def shells(base, chat):
    return [r for r in request(f'{base}/v1/agent-chats/{chat}/executions') if r['kind'] == 'Shell']


def main():
    if os.name != 'nt':
        raise RuntimeError('requires Windows CTRL_BREAK_EVENT')
    core_port, agent_port = port(), port()
    if core_port == agent_port:
        agent_port = port()
    core_url = f'http://127.0.0.1:{core_port}'
    agent_url = f'http://127.0.0.1:{agent_port}'
    electron = ROOT / 'electron'
    workspace = TMP / 'workspace'
    workspace.mkdir()

    core_yml = (ROOT / 'tests/mezhs.test.yaml').read_text(encoding='utf-8')
    core_yml = core_yml.replace('http://127.0.0.1:5198', core_url).replace(
        'root: data', f'root: {TMP / "core"}').replace('electronDirectory: ../electron', f'electronDirectory: {electron}')
    agent_yml = (ROOT / 'tests/mezhs.agent.test.yaml').read_text(encoding='utf-8')
    agent_yml = agent_yml.replace('http://127.0.0.1:5199', agent_url).replace(
        'root: data-agent-test/core', f'root: {TMP / "agent-core"}').replace(
        'agentStorage: data-agent-test/agent.sqlite', f'agentStorage: {TMP / "agent.sqlite"}').replace(
        'workspace: ..', f'workspace: {workspace}').replace(
        'electronDirectory: ../electron', f'electronDirectory: {electron}')
    core_cfg = TMP / 'core.yaml'
    agent_cfg = TMP / 'agent.yaml'
    core_cfg.write_text(core_yml, encoding='utf-8')
    agent_cfg.write_text(agent_yml, encoding='utf-8')

    core = start(CORE, core_cfg, 'core')
    await_condition('core health', lambda: request(f'{core_url}/health'), 25)
    agent = start(AGENT, agent_cfg, 'agent-first')
    await_condition('agent health', lambda: request(f'{agent_url}/health'), 25)

    long_task = '<SH>\nping -n 22 127.0.0.1 >nul\n</SH>'
    first = request(f'{agent_url}/v1/executions', {'policyId': 'test-cancel', 'input': long_task})
    eid = first['executionId']
    running = await_condition('root Running with chatId', lambda: (r if (r := status(agent_url, eid))['status'] == 'Running' and r.get('chatId') else None), 15)
    chat_id = running['chatId']
    shell = await_condition('shell Running', lambda: next((r for r in shells(agent_url, chat_id) if r['status'] == 'Running'), None), 15)
    successor = request(f'{agent_url}/v1/executions', {'policyId': 'test-cancel', 'input': 'queued after shutdown', 'chatId': chat_id})
    sid = successor['executionId']
    queued = await_condition('same-chat successor Queued', lambda: (r if (r := status(agent_url, sid))['status'] == 'Queued' else None), 5)
    if queued.get('startedAt'):
        raise RuntimeError('same-chat successor started prematurely')

    agent.send_signal(signal.CTRL_BREAK_EVENT)
    try:
        exit_code = agent.wait(timeout=12)
    except subprocess.TimeoutExpired as exc:
        raise RuntimeError('graceful shutdown hung waiting for detached shell') from exc
    if exit_code != 0:
        raise RuntimeError(f'Agent exited with nonzero status {exit_code} instead of completing graceful shutdown')
    log = (TMP / 'agent-first.log').read_text(encoding='utf-8', errors='replace')
    if 'Application is shutting down' not in log:
        raise RuntimeError('No ASP.NET graceful-shutdown event in Agent host log')
    # Main host process has stopped but the executor's detached child can finish and be recovered.
    if core.poll() is not None:
        raise RuntimeError('Agent shutdown terminated the independent core API')
    print('GRACEFUL_SHUTDOWN exit=0 during_running_shell=1 successor_queued=1')

    agent = start(AGENT, agent_cfg, 'agent-restarted')
    await_condition('restarted Agent health', lambda: request(f'{agent_url}/health'), 25)
    completed = await_condition('restored root Completed', lambda: (r if (r := status(agent_url, eid))['status'] == 'Completed' else None), 45)
    if not completed.get('completedAt'):
        raise RuntimeError('recovered root has no completed timestamp')
    shell_rows = shells(agent_url, chat_id)
    if len(shell_rows) != 1 or shell_rows[0]['executionId'] != shell['executionId']:
        raise RuntimeError(f'recovery duplicated detached Executor shell: {shell_rows}')
    successor_done = await_condition('queued successor Completed', lambda: (r if (r := status(agent_url, sid))['status'] == 'Completed' else None), 18)
    if not successor_done.get('startedAt') or not successor_done.get('completedAt'):
        raise RuntimeError('queued successor lacks durable timestamps')
    print('GRACEFUL_RECOVERY recovered_root=Completed shell_rows=1 successor=Completed')
    print('PASS: graceful Agent shutdown drains tasks without waiting for detached shell, and restart restores queued and running work without duplicate shell.')


if __name__ == '__main__':
    try:
        main()
    except BaseException:
        print('GRACEFUL_TEST_LOGS', str(TMP), file=sys.stderr)
        for log_path in TMP.glob('*.log'):
            print(f'---- {log_path.name} (last 25 lines) ----', file=sys.stderr)
            for line in log_path.read_text(encoding='utf-8', errors='replace').splitlines()[-25:]:
                print(line, file=sys.stderr)
        raise
    finally:
        for process in RUNNING:
            if process.poll() is None:
                process.kill()
                process.wait(timeout=6)
        # Close child log handles before deleting temporary storage; keep logs on failure.
        if sys.exc_info()[0] is None:
            shutil.rmtree(TMP, ignore_errors=True)
