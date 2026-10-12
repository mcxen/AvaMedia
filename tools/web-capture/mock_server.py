#!/usr/bin/env python3
"""Local, deterministic AvaMedia MCP + WebAiProgress fixture (no real media jobs)."""
import argparse
import json
import math
from functools import partial
from http.server import SimpleHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import parse_qs, urlsplit

FOLDER = '/Users/julie/Movies'
NAMES = ['视频访谈_0312.mp4', '播客_第12期.mp4', '发布会回放.mp4']
IDS = [f'03120000-0000-4000-8000-{i:012d}' for i in range(1, 4)]
DURATION = 1600
TEXTS = [
    '我们从城市生活的变化开始聊起。', '过去几年，大家对工作和生活有了新的理解。',
    '对我来说，最重要的是找到适合自己的节奏。', '这也让我们重新思考人与城市之间的关系。',
    '很多细节，只有真正走进社区之后才能发现。', '比如街角的小店，还有每天遇见的邻居。',
    '这些看起来普通的事情，其实构成了生活的温度。', '所以我们希望把这些真实的声音记录下来。',
]
CUES = [{'start': round(i * 3.96, 2), 'end': round(i * 3.96 + 3.4, 2), 'text': TEXTS[i % len(TEXTS)]} for i in range(186)]
SPEECH = dict(durationSeconds=DURATION, segmentSeconds=300, currentSegment=2,
              cueCount=186, subStage='识别中',
              segments=[dict(index=i, startSeconds=i*300, endSeconds=min((i+1)*300, DURATION),
                             state='done' if i < 2 else 'running' if i == 2 else 'pending',
                             recognizedSeconds=300 if i < 2 else 136 if i == 2 else 0,
                             voiceIntervalCount=38 if i < 5 else 14,
                             cueCount=[76, 76, 34, 0, 0, 0][i]) for i in range(6)],
              voiceIntervals=[dict(startSeconds=i*7.9+0.6, endSeconds=min(i*7.9+6.9, DURATION)) for i in range(202)])
NODES = [dict(index=i, title=title, state='done' if i < 3 else 'running' if i == 3 else 'pending',
              elapsedSeconds=[1.2, 3.8, 2.4, 42.6, None][i],
              subSteps=['分段识别', '合并时间戳', '生成字幕'] if i == 3 else [], detail='')
         for i, title in enumerate(['准备模型', '提取音频', '检测语音', '识别字幕', '保存结果'])]
PROGRESS = dict(jobId=IDS[0], stage='识别字幕', detail='Whisper 正在识别语音', percent=46,
                backend='CPU · 4 线程', currentNode=3, nodes=NODES, recentResults=TEXTS[-3:],
                recentStages=['模型已就绪', '音频提取完成', '开始语音识别'], hasPreview=False,
                speech=SPEECH, queue=dict(subtitleJobsRunning=1, subtitleJobsWaiting=2))
TASKS = [dict(id=id_, name=name, featureId='auto-subtitle', state='Running' if i == 0 else 'Waiting',
              progress=46 if i == 0 else 0, detail='识别中 · 第 3/6 段' if i == 0 else f'排队第 {i} 位',
              submittedBy='网页 · MacBook', inputs=[f'{FOLDER}/{name}'], output='', error=None,
              createdUtc='2026-10-12T06:02:00Z', outputIsPartial=i == 0,
              activity=dict(model='Whisper · Base', stage='识别字幕', detail='第 3/6 段', backend='CPU'))
         for i, (id_, name) in enumerate(zip(IDS, NAMES))]
LOG = '\n'.join(['14:02:11  模型已就绪：Whisper Base', '14:02:12  提取音频 · 16 kHz 单声道',
                 '14:02:16  检测到 202 个语音区间', '14:02:18  第 1/6 段识别完成',
                 '14:02:35  第 2/6 段识别完成', '14:02:54  正在识别第 3/6 段',
                 '14:02:55  已识别 186 条字幕'])


def choices(path, values):
    return dict(path=path, kind='choice', choices=[dict(value=v, label=str(v)) for v in values])


FEATURE = dict(id='auto-subtitle', name='自动字幕', category='视频', canCreateTask=True, format='srt',
               form=dict(defaults=dict(format='srt', subtitleFontSize=0,
                                       transcription=dict(language='zh', model=0, recognitionOnly=True)),
                         fields=[choices('transcription.language', ['auto', 'zh', 'en', 'ja', 'ko', 'fr', 'de', 'es', 'ru']),
                                 choices('transcription.model', [1, 0, 2]), choices('format', ['mp4', 'mkv', 'srt', 'ass'])]))
RUNTIME = dict(platform='macOS · Apple Silicon', version='1.0.0', outputFolder=FOLDER+'/AvaMedia',
               background=True, nsfwEnabled=False, resources=dict(cpuReservePercent=20, cpuThreads=4, multiThread=True))


def call_tool(name, args):
    name = name.removeprefix('avamedia_')
    if name == 'capabilities':
        return dict(features=[FEATURE], classificationPresets=[])
    if name in ('get_runtime', 'server_info'):
        return RUNTIME
    if name == 'file_locations':
        return dict(locations=[dict(name='影片', path=FOLDER)])
    if name in ('browse_directory', 'list_files'):
        folder = args.get('folder', FOLDER)
        entries = [] if args.get('foldersOnly') else [dict(name=n, path=folder+'/'+n, isDirectory=False,
                    size=s, modifiedUtc='2026-10-12T05:00:00Z', hidden=False)
                    for n, s in zip(NAMES, [428700000, 916400000, 507200000])]
        offset, limit = args.get('offset', 0), args.get('limit', 200)
        return dict(path=folder, parent='/Users/julie', entries=entries[offset:offset+limit],
                    total=len(entries), nextOffset=offset+limit if offset+limit < len(entries) else None)
    if name == 'list_tasks':
        items = [t for t in TASKS if not args.get('state') or t['state'] == args['state']]
        offset, limit = args.get('offset', 0), args.get('limit', 100)
        return dict(tasks=items[offset:offset+limit], offset=offset, total=len(items),
                    nextOffset=offset+limit if offset+limit < len(items) else None,
                    counts=dict(All=3, Running=1, Waiting=2, Paused=0, Completed=0, Failed=0, Cancelled=0, Stopping=0))
    if name == 'get_task':
        task = next(t for t in TASKS if t['id'] == args['taskId'])
        return dict(task=task, result=None, outputIsDirectory=False)
    if name == 'get_task_log':
        return dict(text=LOG[-args.get('maxCharacters', 6000):])
    if name == 'list_models':
        return dict(models=[dict(name='Whisper Base', available=True)])
    raise ValueError(f'Unmocked tool: {name}')


class Handler(SimpleHTTPRequestHandler):
    extensions_map = {**SimpleHTTPRequestHandler.extensions_map, '.wasm': 'application/wasm', '.js': 'text/javascript'}

    def end_headers(self):
        self.send_header('Cache-Control', 'no-store')
        super().end_headers()

    def send_json(self, value, status=200):
        data = json.dumps(value, ensure_ascii=False).encode()
        self.send_response(status)
        self.send_header('Content-Type', 'application/json; charset=utf-8')
        self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_POST(self):
        if urlsplit(self.path).path != '/mcp':
            self.send_error(404)
            return
        request = {}
        try:
            request = json.loads(self.rfile.read(int(self.headers.get('Content-Length', '0'))))
            method, params = request['method'], request.get('params', {})
            if method == 'initialize':
                result = dict(protocolVersion='2026-07-28', capabilities=dict(tools={}),
                              serverInfo=dict(name='AvaMedia capture fixture', version='1.0'))
            elif method.startswith('notifications/'):
                self.send_response(202)
                self.end_headers()
                return
            elif method == 'ping':
                result = {}
            elif method == 'tools/list':
                result = dict(tools=[dict(name='avamedia_'+n, description=n, inputSchema=dict(type='object'))
                                    for n in ['capabilities', 'get_runtime', 'file_locations', 'browse_directory',
                                              'list_tasks', 'get_task', 'get_task_log', 'list_models']])
            elif method == 'tools/call':
                name = params['name']
                self.log_message('MCP %s', name)
                payload = call_tool(name, params.get('arguments', {}))
                result = dict(structuredContent=payload, content=[dict(type='text', text=json.dumps(payload, ensure_ascii=False))])
            else:
                raise ValueError(f'Unmocked method: {method}')
            self.send_json(dict(jsonrpc='2.0', id=request.get('id'), result=result))
        except (ValueError, KeyError, StopIteration) as error:
            self.log_error('%s', error)
            self.send_json(dict(jsonrpc='2.0', id=request.get('id'), error=dict(code=-32602, message=str(error))))

    def do_GET(self):
        url = urlsplit(self.path)
        if url.path == '/__capture/health':
            self.send_json(dict(ready=True, taskId=IDS[0]))
            return
        parts = url.path.strip('/').split('/')
        if len(parts) == 5 and parts[:2] == ['web', 'tasks'] and parts[3] == 'ai':
            if parts[2] != IDS[0]:
                self.send_error(404)
                return
            endpoint = parts[4]
            query = parse_qs(url.query)
            try:
                if endpoint == 'progress':
                    self.send_json(PROGRESS)
                elif endpoint == 'segments':
                    self.send_json(SPEECH)
                elif endpoint == 'results':
                    after = int(query.get('after', ['0'])[0])
                    if after < 0:
                        raise ValueError('after must be nonnegative')
                    self.send_json(dict(total=len(CUES), cues=CUES[after:]))
                elif endpoint == 'waveform':
                    buckets = int(query.get('buckets', ['2000'])[0])
                    if not 1 <= buckets <= 10000:
                        raise ValueError('buckets must be 1..10000')
                    peaks = [round((0.15 + 0.7*abs(math.sin(i*1.73))*abs(math.cos(i*0.17)))
                                   * (0.12 if i % 19 < 3 else 1), 4) for i in range(buckets)]
                    self.send_json(dict(durationSeconds=DURATION, bucketCount=buckets, peaks=peaks))
                else:
                    # Subtitle recognition has no activity image: matches hasPreview=false.
                    self.send_error(404)
            except ValueError as error:
                self.send_json(dict(error=str(error)), 400)
            return
        super().do_GET()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--root', type=Path, default=Path('/tmp/avam-web/wwwroot'))
    parser.add_argument('--port', type=int, default=8765)
    args = parser.parse_args()
    if not (args.root / 'index.html').is_file():
        parser.error(f'Published index.html not found in {args.root}')
    server = ThreadingHTTPServer(('127.0.0.1', args.port), partial(Handler, directory=str(args.root)))
    print(f'AvaMedia capture fixture: http://127.0.0.1:{server.server_port}/?lang=zh-CN', flush=True)
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass
    finally:
        server.server_close()


if __name__ == '__main__':
    main()
