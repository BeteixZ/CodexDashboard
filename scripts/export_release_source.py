"""Export the reviewed source surface without local evidence or user state.

Usage: python scripts/export_release_source.py NEW_DIRECTORY
The destination must not exist. This does not read credential stores or copy Git history.
"""
from __future__ import annotations

import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
PUBLIC_DOCS = {
    'getting-started.md', 'known-limitations.md', 'releasing.md',
    'product-installation.md', 'development-history.md', 'assistant-backup.md',
}
PUBLIC_IMAGES = {'docs/images/dashboard.png'}
SOURCE_DIRS = {'src', 'tests', 'web', 'packaging', 'scripts', '.github'}
SOURCE_SUFFIXES = {'.cs', '.csproj', '.props', '.targets', '.xaml', '.manifest',
                   '.py', '.ps1', '.cmd', '.mjs', '.js', '.html', '.css', '.iss',
                   '.json', '.yml', '.yaml', '.md', '.txt', '.isl'}
ROOT_FILES = {'LICENSE', '.gitignore', '.editorconfig', '.gitattributes', 'NuGet.Config',
              'README.md', 'CHANGELOG.md', 'CONTRIBUTING.md', 'SECURITY.md'}
SECRET_PATTERNS = [
    re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    re.compile(r'\bgh[pousr]_[A-Za-z0-9]{30,}\b'),
    re.compile(r'\bgithub_pat_[A-Za-z0-9_]{35,}\b'),
    re.compile(r'\bsk-(?:proj-)?[A-Za-z0-9_-]{40,}\b'),
]


def selected_files() -> list[Path]:
    result = subprocess.run(['git', '-C', str(ROOT), 'ls-files', '--cached',
                             '--others', '--exclude-standard', '-z'],
                            check=True, stdout=subprocess.PIPE)
    selected = []
    for name in sorted(set(result.stdout.decode('utf-8').split('\0'))):
        if not name:
            continue
        relative = Path(name)
        source = ROOT / relative
        if not source.is_file():
            continue
        if source.is_symlink() or not source.resolve().is_relative_to(ROOT.resolve()):
            raise ValueError(f'Redirected source entry: {name}')
        if any(p in {'bin', 'obj', 'artifacts', '__pycache__', '.git', '.packages',
                     '.nuget-cache', '.agents', '.codex'} for p in relative.parts):
            continue
        if relative.name == 'VALIDATION.md':
            continue
        if relative.as_posix() in PUBLIC_IMAGES:
            allowed = True
        elif relative.parts[0] == 'docs':
            allowed = len(relative.parts) == 2 and relative.name in PUBLIC_DOCS
        elif len(relative.parts) == 1:
            allowed = relative.name in ROOT_FILES or (relative.suffix in SOURCE_SUFFIXES and relative.suffix != '.md')
        else:
            allowed = relative.parts[0] in SOURCE_DIRS and relative.suffix in SOURCE_SUFFIXES
        if not allowed:
            continue
        if re.search(r'(?i)(credentials|\.state$|^data-directory\.txt$|^settings\.json$|^dashboard\.settings\.json$)', relative.name):
            raise ValueError(f'User-state filename in source inventory: {name}')
        content = '' if relative.as_posix() in PUBLIC_IMAGES else source.read_text(encoding='utf-8-sig')
        if any(pattern.search(content) for pattern in SECRET_PATTERNS):
            raise ValueError(f'Potential credential material in {name}; review before export')
        selected.append(relative)
    return selected


def main() -> None:
    if len(sys.argv) != 2:
        raise SystemExit(__doc__)
    destination = Path(sys.argv[1]).resolve()
    if destination.exists():
        raise SystemExit('Destination exists; choose a new directory.')
    inventory = selected_files()
    destination.mkdir(parents=True)
    manifest = []
    for relative in inventory:
        source = ROOT / relative
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        content = target.read_bytes()
        manifest.append({'file': relative.as_posix(), 'bytes': len(content),
                         'sha256': hashlib.sha256(content).hexdigest()})
    report = destination.parent / (destination.name + '-inventory.json')
    report.write_text(json.dumps({'files': manifest}, indent=2), encoding='utf-8')
    print(json.dumps({'files': len(manifest), 'bytes': sum(x['bytes'] for x in manifest),
                      'destination': str(destination), 'inventory': str(report)}))


if __name__ == '__main__':
    main()
