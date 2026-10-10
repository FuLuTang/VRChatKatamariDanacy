#!/usr/bin/env python3
"""One-time, non-overwriting import of the three supplied v0.3 attachments."""
import argparse
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SOURCES = {
    'ChatGPT-VRC块魂-20261011-0332.txt': ('f4/f4db0dc497909a97225c95373c272d59d8f47f0a0ccdf86aef0bf43a7c2a08ae', 'f4db0dc497909a97225c95373c272d59d8f47f0a0ccdf86aef0bf43a7c2a08ae'),
    'VRC_Katamari_Design_and_Execution_v0.3.md': ('ca/ca2dc40d175964a10bdba12fc7fda017b90ae9d79821f276b968e5a2fc6e96c1', 'ca2dc40d175964a10bdba12fc7fda017b90ae9d79821f276b968e5a2fc6e96c1'),
    'VRC_Katamari_Agent_Tasks_v0.3.json': ('8f/8f0b33231b3770633cd77a71b4dc30b2bf7d8bd7320ff9d44a56852fe85b4a0e', '8f0b33231b3770633cd77a71b4dc30b2bf7d8bd7320ff9d44a56852fe85b4a0e'),
}
DOC_NAMES = [
    '00_SCOPE_DECISIONS', '01_GAME_DESIGN', '02_TECHNICAL_CONTRACTS',
    '03_MAPS_ASSETS', '04_EXECUTION_PLAN', '05_ACCEPTANCE_TESTS',
    '06_MONETIZATION_LAUNCH', '07_AGENT_OPERATING_RULES', '08_SOURCES_AND_OPEN_QUESTIONS',
]
PATH_MAP = {
    'Assets/': 'VRChatKatamariDanacyUnity/Assets/', 'Packages/': 'VRChatKatamariDanacyUnity/Packages/',
    'ProjectSettings/': 'VRChatKatamariDanacyUnity/ProjectSettings/',
    **{prefix + '/': 'Docs/' + prefix + '/' for prefix in
       ['Research', 'AssetIntake', 'Contracts', 'Evidence', 'Commerce', 'Integration', 'Release', 'Art', 'Tests']},
}


def create(relative, data):
    target = ROOT / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    with target.open('xb') as stream:
        stream.write(data if isinstance(data, bytes) else data.encode('utf-8'))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('attachments_root', type=Path, help='Directory containing the hash-addressed attachment subfolders')
    parser.add_argument('--include-private-chat', action='store_true', help='Copy the private conversation export (never use for a public repository)')
    options = parser.parse_args()
    raw = {}
    manifest = []
    private_chat = 'ChatGPT-VRC块魂-20261011-0332.txt'
    for name, (directory, digest) in SOURCES.items():
        is_public = name != private_chat
        if not is_public and not options.include_private_chat:
            manifest.append({'file': name, 'sha256': digest, 'bytes': 96104,
                             'publicRepository': False,
                             'reason': 'Private conversation export; excluded from public GitHub push by user choice.'})
            continue
        source = options.attachments_root / directory / name
        content = source.read_bytes()
        assert hashlib.sha256(content).hexdigest() == digest, name
        raw[name] = content
        create('Docs/Source/' + name, content)
        manifest.append({'file': name, 'sha256': digest, 'bytes': len(content),
                         **({'publicRepository': False, 'reason': 'Private conversation export; excluded from public GitHub push by user choice.'} if not is_public else {})})
    design = raw['VRC_Katamari_Design_and_Execution_v0.3.md'].decode('utf-8')
    headings = list(re.finditer(r'^# (0[0-8]) .+$', design, re.MULTILINE))
    assert len(headings) == 9
    create('Docs/00_PACKAGE_ENTRY.md', design[:headings[0].start()])
    for index, heading in enumerate(headings):
        end = headings[index + 1].start() if index + 1 < len(headings) else len(design)
        section = design[heading.start():end].rstrip()
        section = re.sub(r'\n\n---\s*$', '', section)
        create('Docs/' + DOC_NAMES[index] + '.md', section + '\n')
    for name, block in zip(['dependencies', 'phases'], re.findall(r'```mermaid\n(.*?)\n```', design, re.DOTALL), strict=True):
        create('Docs/diagrams/' + name + '.mmd', block + '\n')
    tasks = json.loads(raw['VRC_Katamari_Agent_Tasks_v0.3.json'])
    for task in tasks['tasks']:
        task['write_scope'] = [next((new + scope[len(old):] for old, new in PATH_MAP.items() if scope.startswith(old)), scope) for scope in task['write_scope']]
    tasks['repositoryLayoutVersion'] = 1
    tasks['repositoryLayoutNote'] = 'Unity工程在VRChatKatamariDanacyUnity/；研究、证据及工程辅助文件在Docs/；Marketing/保持原路径。原始任务包保留在Docs/Source/。未改变任务依赖或验收状态。'
    create('tasks.json', json.dumps(tasks, ensure_ascii=False, indent=2) + '\n')
    create('Docs/Source/manifest.json', json.dumps({'importedAt': '2026-10-11', 'sources': manifest}, ensure_ascii=False, indent=2) + '\n')
    print('Imported 2 public source attachments; private chat export excluded unless explicitly opted in.')


if __name__ == '__main__':
    main()
