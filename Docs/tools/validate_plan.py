#!/usr/bin/env python3
"""Validate planning data only, never Unity, gameplay or human gates."""
import argparse
import hashlib
import json
import re
from pathlib import Path, PurePosixPath

ROOT = Path(__file__).resolve().parents[2]
STATUSES = {'TODO', 'READY', 'RUNNING', 'REVIEW', 'DONE', 'BLOCKED'}
LIST_FIELDS = ('dependencies', 'write_scope', 'exclusive_locks',
               'external_requirements', 'steps', 'acceptance_criteria', 'source_ids')


def validate_plan(data, profile='free_launch'):
    errors = []
    if not isinstance(data, dict):
        return ['Plan must be an object']
    if data.get('schemaVersion') != 1:
        errors.append('Unsupported schemaVersion')
    tasks = data.get('tasks')
    if not isinstance(tasks, list) or not tasks:
        return errors + ['tasks must be a non-empty array']
    by_id = {}
    for index, task in enumerate(tasks):
        if not isinstance(task, dict):
            errors.append(f'Task {index} must be an object')
            continue
        ident = task.get('id')
        if not isinstance(ident, str) or not re.fullmatch(r'T\d{2}', ident):
            errors.append(f'Task {index}: invalid id')
            continue
        if ident in by_id:
            errors.append(f'Duplicate task: {ident}')
        by_id[ident] = task
        for field in ('title', 'phase', 'implementer', 'reviewer', 'priority'):
            if not isinstance(task.get(field), str) or not task[field].strip():
                errors.append(f'{ident}: missing {field}')
        if task.get('status') not in STATUSES:
            errors.append(f'{ident}: invalid status')
        if task.get('implementer') not in {'SOL', 'LUNA', 'HUMAN'}:
            errors.append(f'{ident}: invalid implementer')
        if task.get('priority') not in {'P0', 'P1'}:
            errors.append(f'{ident}: invalid priority')
        for field in LIST_FIELDS:
            value = task.get(field)
            if not isinstance(value, list) or any(not isinstance(v, str) or not v.strip() for v in value):
                errors.append(f'{ident}: {field} must be a string array')
        for field in ('write_scope', 'steps', 'acceptance_criteria'):
            if not task.get(field):
                errors.append(f'{ident}: empty {field}')
        scopes = task.get('write_scope', [])
        if isinstance(scopes, list):
            for scope in scopes:
                if not isinstance(scope, str):
                    continue
                path = PurePosixPath(scope)
                if path.is_absolute() or '..' in path.parts or '\\' in scope or scope.startswith(('.git/', './')):
                    errors.append(f'{ident}: unsafe write scope {scope}')
                if scope.startswith('UnityProject/'):
                    errors.append(f'{ident}: stale Unity root; use VRChatKatamariDanacyUnity/')
                elif scope.startswith(('Assets/', 'Packages/', 'ProjectSettings/')):
                    errors.append(f'{ident}: Unity scope missing VRChatKatamariDanacyUnity/ prefix')
    edges = {}
    for ident, task in by_id.items():
        dependencies = task.get('dependencies', [])
        conditional = task.get('conditional_dependencies', {})
        if not isinstance(conditional, dict):
            errors.append(f'{ident}: conditional_dependencies must be an object')
            conditional = {}
        extra = conditional.get(profile, [])
        if not isinstance(extra, list) or any(not isinstance(v, str) for v in extra):
            errors.append(f'{ident}: invalid conditional dependencies for {profile}')
            extra = []
        # Validate references for ALL profiles, even when inactive.
        for mode, refs in conditional.items():
            if not isinstance(refs, list) or any(not isinstance(v, str) or v not in by_id for v in refs):
                errors.append(f'{ident}: invalid conditional references for {mode}')
        refs = dependencies if isinstance(dependencies, list) else []
        refs = [v for v in refs if isinstance(v, str)] + extra
        edges[ident] = refs
        for ref in refs:
            if ref not in by_id:
                errors.append(f'{ident}: unknown dependency {ref}')
        if task.get('status') in {'READY', 'RUNNING', 'REVIEW', 'DONE'}:
            for ref in refs:
                if ref in by_id and by_id[ref].get('status') != 'DONE':
                    errors.append(f'{ident}: dependency {ref} is not DONE')
    visiting, visited = set(), set()

    def visit(ident):
        if ident in visiting:
            errors.append(f'Dependency cycle at {ident} ({profile})')
            return
        if ident in visited or ident not in by_id:
            return
        visiting.add(ident)
        for ref in edges[ident]:
            visit(ref)
        visiting.remove(ident)
        visited.add(ident)

    for ident in by_id:
        visit(ident)
    profiles = data.get('releaseProfiles', {})
    if not isinstance(profiles, dict) or profile not in profiles:
        errors.append(f'Missing release profile {profile}')
    else:
        settings = profiles[profile]
        if not isinstance(settings, dict):
            return errors + [f'Invalid release profile {profile}']
        if settings.get('target') not in by_id:
            errors.append('Release target does not exist')
        if settings.get('paid_store_enabled') is not (profile == 'paid_launch'):
            errors.append(f'Incorrect paid store flag for {profile}')
        if profile == 'paid_launch':
            if 'T41' not in settings.get('additional_required', []):
                errors.append('Paid launch must require T41')
            if 'T41' not in edges.get('T42', []):
                errors.append('Paid T42 must depend on T41')
    return errors


def verify_sources(root):
    errors = []
    source_root = root / 'Docs/Source'
    manifest = json.loads((source_root / 'manifest.json').read_text(encoding='utf-8'))
    for source in manifest['sources']:
        name = source['file']
        if PurePosixPath(name).name != name:
            errors.append(f'Unsafe source filename: {name}')
            continue
        path = source_root / name
        if not path.is_file() and source.get('publicRepository') is False:
            continue
        if not path.is_file():
            errors.append(f'Missing public source: {name}')
            continue
        content = path.read_bytes()
        if len(content) != source['bytes'] or hashlib.sha256(content).hexdigest() != source['sha256']:
            errors.append(f'Source differs from attachment: {name}')
    return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--plan', type=Path, default=ROOT / 'tasks.json')
    options = parser.parse_args()
    try:
        data = json.loads(options.plan.read_text(encoding='utf-8'))
        errors = []
        for profile in ('free_launch', 'paid_launch'):
            errors.extend(validate_plan(data, profile))
        errors.extend(verify_sources(ROOT))
    except (OSError, ValueError, KeyError, TypeError) as exc:
        parser.exit(1, f'ERROR: {exc}\n')
    if errors:
        parser.exit(1, '\n'.join('ERROR: ' + error for error in errors) + '\n')
    roots = [t['id'] for t in data['tasks'] if not t['dependencies']]
    print(f'PASS: {len(data["tasks"])} tasks; both release DAGs acyclic; source hashes verified.')
    print('Dependency roots (external requirements NOT checked): ' + ', '.join(roots))
    print('NOT_RUN: Unity/Udon compilation, VR comfort, multiplayer, performance, commerce.')


if __name__ == '__main__':
    main()
