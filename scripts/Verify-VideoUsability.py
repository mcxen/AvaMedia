#!/usr/bin/env python3
"""Scoped UI ablation: baseline, each independent change, and their combination."""
import argparse
import hashlib
import json
import shutil
import subprocess
from datetime import datetime, timezone
from pathlib import Path

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--baseline', required=True)
parser.add_argument('--change', action='append', required=True, metavar='GROUP=COMMIT')
parser.add_argument('--output')
parser.add_argument('--dotnet', default=shutil.which('dotnet') or str(Path.home() / '.dotnet/dotnet'))
args = parser.parse_args()
repo = Path(__file__).resolve().parent.parent
changes = dict(value.split('=', 1) for value in args.change)
if set(changes) != set('abcd'):
    parser.error('Specify exactly one commit for each group a, b, c and d.')
output = Path(args.output or repo / 'artifacts/video-usability-ablation' / datetime.now(timezone.utc).strftime('run-%Y%m%d-%H%M%S')).resolve()
output.mkdir(parents=True, exist_ok=False)
source = output / 'source'
source.mkdir()

def git(*values):
    return subprocess.check_output(['git', *values], cwd=repo)

archive = output / 'baseline.tar'
archive.write_bytes(git('archive', '--format=tar', args.baseline))
subprocess.run(['tar', '-xf', str(archive), '-C', str(source)], check=True)
archive.unlink()
# Give the snapshot its own Git root so applying a patch cannot skip paths relative to the enclosing checkout.
subprocess.run(['git', 'init', '--quiet', str(source)], check=True)
project = 'tests/AvaMedia.VideoUsabilityChecks'
shutil.copytree(repo / project, source / project, ignore=shutil.ignore_patterns('bin', 'obj'))
patches = {group: git('show', '--format=', '--binary', commit) for group, commit in changes.items()}
paths = set()
for commit in changes.values():
    paths.update(git('diff-tree', '--no-commit-id', '--name-only', '-r', commit).decode().splitlines())
originals = {path: (source / path).read_bytes() if (source / path).exists() else None for path in paths}
report = {'baseline': git('rev-parse', args.baseline).decode().strip(),
          'changes': {group: git('rev-parse', commit).decode().strip() for group, commit in changes.items()}, 'variants': {}}
for variant in ['baseline', 'a', 'b', 'c', 'd', 'combined']:
    for path, content in originals.items():
        target = source / path
        if content is None:
            target.unlink(missing_ok=True)
        else:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_bytes(content)
    active = 'abcd' if variant == 'combined' else variant if variant != 'baseline' else ''
    for group in active:
        subprocess.run(['git', 'apply', '-'], input=patches[group], cwd=source, check=True)
    result = output / variant
    result.mkdir()
    print('RUN ' + variant, flush=True)
    with (result / 'build-and-interaction.log').open('w') as log:
        run = subprocess.run([args.dotnet, 'run', '--project', project, '-p:NuGetAudit=false', '--', variant, str(result)],
                             cwd=source, stdout=log, stderr=subprocess.STDOUT)
    if run.returncode:
        print((result / 'build-and-interaction.log').read_text()[-6000:], flush=True)
        raise SystemExit(run.returncode)
    report['variants'][variant] = json.loads((result / 'results.json').read_text())
    report['variants'][variant]['core_sha256'] = hashlib.sha256((source / 'src/AvaMedia.Core/bin/Debug/net8.0/AvaMedia.Core.dll').read_bytes()).hexdigest()
    report['variants'][variant]['desktop_sha256'] = hashlib.sha256((source / 'src/AvaMedia.Desktop/bin/Debug/net8.0/AvaMedia.Desktop.dll').read_bytes()).hexdigest()
    print(f"PASS {variant}: {len(report['variants'][variant]['checks'])} checks", flush=True)
(output / 'report.json').write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n')
print('RESULT ' + str(output), flush=True)
