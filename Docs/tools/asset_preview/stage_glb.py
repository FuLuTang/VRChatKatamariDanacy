"""Stage only allowlisted, hash-pinned GLBs from approved ZIP snapshots.

This reads selected members into memory; it does not extract the archive tree and
is not a decoder sandbox or a guarantee against GLB/PNG/Blender importer bugs.
"""
import hashlib
import io
import json
import struct
import zipfile
from pathlib import Path

from Docs.tools.offline_quality.archive_inventory import inspect_zip_bytes

ROOT = Path(__file__).resolve().parents[3]
MAX_ARCHIVE_BYTES = 75_000_000
MAX_MEMBER = 1_000_000
MAX_PALETTE_BYTES = 128_000
MAX_IMAGES = 4
MAX_TOTAL_TEXTURE_PIXELS = 16_777_216
MAX_ACCESSORS = 128
MAX_ACCESSOR_ELEMENTS = 500_000
MAX_NODES = 100
MAX_MESHES = 100

APPROVED_PACKAGES = {
    'KENNEY-FOOD-KIT-2.0': {
        'group': 'food',
        'path': 'Docs/AssetIntake/Downloads/kenney_food-kit.zip',
        'sha256': 'cdad90853682499b94c9fda2f87678b24bfd8f3264e0ed323f6b6a27fd7c6f6f',
    },
    'KENNEY-NATURE-KIT-2.1': {
        'group': 'nature',
        'path': 'Docs/AssetIntake/Downloads/kenney_nature-kit.zip',
        'sha256': 'fa7974a0d342bfe63c38664ba9f8ec1a4aab8ea25f099bdc56870e33588c4d9d',
    },
}
SELECTION = [
    ('food', n, 'Models/GLB format/' + n + '.glb')
    for n in ('apple', 'banana', 'strawberry', 'bread', 'croissant', 'cookie', 'cheese-cut', 'soda-can')
] + [('nature', n, 'Models/GLTF format/' + n + '.glb')
     for n in ('pot_small', 'rock_smallA', 'mushroom_red', 'flower_redA')]
PALETTE_MEMBER = 'Models/GLB format/Textures/colormap.png'


def _require_dict_list(doc, key):
    value = doc.get(key, [])
    if not isinstance(value, list) or any(not isinstance(item, dict) for item in value):
        raise ValueError(f'{key} must be a list of objects')
    return value


def _validate_glb(data):
    if not isinstance(data, bytes) or len(data) < 20 or len(data) > MAX_MEMBER:
        raise ValueError('GLB size limit')
    magic, version, length = struct.unpack_from('<4sII', data)
    if magic != b'glTF' or version != 2 or length != len(data):
        raise ValueError('invalid GLB header')
    cursor, chunks = 12, []
    while cursor < len(data):
        if cursor + 8 > len(data):
            raise ValueError('truncated GLB chunk')
        count, kind = struct.unpack_from('<II', data, cursor)
        cursor += 8
        if count % 4 or cursor + count > len(data):
            raise ValueError('invalid GLB chunk length')
        chunks.append((kind, data[cursor:cursor + count]))
        cursor += count
    if (len(chunks) != 2 or chunks[0][0] != 0x4E4F534A or
            len(chunks[0][1]) > 128_000 or chunks[1][0] != 0x004E4942):
        raise ValueError('require one JSON and one binary chunk')
    doc = json.loads(chunks[0][1].decode('utf-8'))
    if not isinstance(doc, dict) or not isinstance(doc.get('asset'), dict):
        raise ValueError('GLB root and asset must be objects')
    if doc['asset'].get('version') != '2.0':
        raise ValueError('require glTF 2.0')
    if doc.get('extensionsRequired', []):
        raise ValueError('unsupported required extensions')
    # Deliberately reject every URI, including data URIs and nested extensions.
    def check(value):
        if isinstance(value, dict):
            if 'uri' in value:
                raise ValueError('URI resources forbidden; embedded bufferViews only')
            for child in value.values():
                check(child)
        elif isinstance(value, list):
            for child in value:
                check(child)
    check(doc)

    buffers = _require_dict_list(doc, 'buffers')
    binary = chunks[1][1]
    if (len(buffers) != 1 or isinstance(buffers[0].get('byteLength'), bool) or
            not isinstance(buffers[0].get('byteLength'), int)):
        raise ValueError('require one embedded buffer')
    buffer_length = buffers[0]['byteLength']
    if not 0 <= buffer_length <= len(binary) or len(binary) - buffer_length > 3:
        raise ValueError('embedded buffer out of bounds/padding')

    views = _require_dict_list(doc, 'bufferViews')
    for view in views:
        offset, size, buffer_index = view.get('byteOffset', 0), view.get('byteLength', -1), view.get('buffer', 0)
        if (any(isinstance(v, bool) or not isinstance(v, int) for v in (offset, size, buffer_index)) or
                buffer_index != 0 or offset < 0 or size < 0 or offset + size > buffer_length):
            raise ValueError('invalid bufferView')

    accessors = _require_dict_list(doc, 'accessors')
    if len(accessors) > MAX_ACCESSORS:
        raise ValueError('accessor count limit')
    accessor_elements = 0
    for accessor in accessors:
        count = accessor.get('count')
        if isinstance(count, bool) or not isinstance(count, int) or not 0 <= count <= MAX_MEMBER:
            raise ValueError('accessor element count limit')
        accessor_elements += count
    if accessor_elements > MAX_ACCESSOR_ELEMENTS:
        raise ValueError('total accessor element limit')

    nodes = _require_dict_list(doc, 'nodes')
    meshes = _require_dict_list(doc, 'meshes')
    if len(nodes) > MAX_NODES or len(meshes) > MAX_MESHES:
        raise ValueError('node/mesh count limit')

    images = _require_dict_list(doc, 'images')
    if len(images) > MAX_IMAGES:
        raise ValueError('image count limit')
    dimensions, total_pixels = [], 0
    for image in images:
        index = image.get('bufferView')
        if (image.get('mimeType') != 'image/png' or isinstance(index, bool) or
                not isinstance(index, int) or not 0 <= index < len(views)):
            raise ValueError('require embedded PNG image')
        view = views[index]
        offset = view.get('byteOffset', 0)
        payload = binary[offset:offset + view['byteLength']]
        if len(payload) < 24 or payload[:8] != b'\x89PNG\r\n\x1a\n' or payload[12:16] != b'IHDR':
            raise ValueError('invalid PNG header')
        width, height = struct.unpack_from('>II', payload, 16)
        if not (0 < width <= 4096 and 0 < height <= 4096):
            raise ValueError('texture dimension limit')
        total_pixels += width * height
        if total_pixels > MAX_TOTAL_TEXTURE_PIXELS:
            raise ValueError('total texture pixel limit')
        dimensions.append([width, height])
    return {'generator': doc['asset'].get('generator', 'unknown'),
            'nodes': len(nodes), 'meshes': len(meshes),
            'image_dimensions_from_header': dimensions, 'external_resources': False}


def validate_glb(data):
    try:
        return _validate_glb(data)
    except ValueError:
        raise
    except Exception as exc:
        # Malformed JSON/schema/chunk data must fail closed through one public error type.
        raise ValueError('malformed GLB document or chunk structure') from exc


def _embed_approved_palette(data, palette):
    if len(data) < 20 or len(data) > MAX_MEMBER:
        raise ValueError('GLB size limit')
    magic, version, length = struct.unpack_from('<4sII', data)
    if magic != b'glTF' or version != 2 or length != len(data):
        raise ValueError('invalid GLB header')
    count, kind = struct.unpack_from('<II', data, 12)
    cursor = 20 + count
    if kind != 0x4E4F534A or count > 128_000 or count % 4 or cursor + 8 > len(data):
        raise ValueError('invalid GLB JSON chunk')
    doc = json.loads(data[20:cursor].decode('utf-8'))
    if not isinstance(doc, dict):
        raise ValueError('GLB root must be object')
    binary_size, binary_kind = struct.unpack_from('<II', data, cursor)
    if binary_kind != 0x004E4942 or cursor + 8 + binary_size != len(data):
        raise ValueError('invalid GLB binary chunk')
    binary = data[cursor + 8:]
    images = _require_dict_list(doc, 'images')
    changed = False
    for image in images:
        if 'uri' not in image:
            continue
        if image['uri'] != 'Textures/colormap.png':
            raise ValueError('unapproved image URI')
        if 'bufferView' in image:
            raise ValueError('URI image cannot also have bufferView')
        if image.get('mimeType', 'image/png') != 'image/png':
            raise ValueError('unapproved palette MIME type')
        if len(palette) > MAX_PALETTE_BYTES or len(palette) < 24 or palette[:8] != b'\x89PNG\r\n\x1a\n':
            raise ValueError('unapproved palette bytes/size')
        if 'bufferViews' not in doc:
            doc['bufferViews'] = []
        views = _require_dict_list(doc, 'bufferViews')
        index = len(views)
        views.append({'buffer': 0, 'byteOffset': len(binary), 'byteLength': len(palette)})
        binary += palette
        binary += b'\x00' * ((-len(binary)) % 4)
        image.pop('uri')
        image['bufferView'], image['mimeType'] = index, 'image/png'
        changed = True
    if not changed:
        validate_glb(data)
        return data
    buffers = _require_dict_list(doc, 'buffers')
    if len(buffers) != 1:
        raise ValueError('require one source buffer')
    buffers[0]['byteLength'] = len(binary)
    encoded = json.dumps(doc, separators=(',', ':')).encode('utf-8')
    encoded += b' ' * ((-len(encoded)) % 4)
    body = struct.pack('<II', len(encoded), 0x4E4F534A) + encoded
    body += struct.pack('<II', len(binary), 0x004E4942) + binary
    rewritten = struct.pack('<4sII', b'glTF', 2, len(body) + 12) + body
    validate_glb(rewritten)
    return rewritten


def embed_approved_palette(data, palette):
    """Public fail-closed wrapper; palette must come from the fixed approved ZIP snapshot."""
    try:
        return _embed_approved_palette(data, palette)
    except ValueError:
        raise
    except Exception as exc:
        raise ValueError('malformed GLB palette rewrite input') from exc


def _safe_package_path(root: Path, relative: str, expected: str) -> Path:
    if relative != expected or Path(relative).is_absolute() or '\\' in relative:
        raise ValueError('archive path is not the fixed approved relative path')
    if any(part in ('', '.', '..') for part in relative.split('/')):
        raise ValueError('unsafe archive path components')
    root = root.resolve()
    downloads = root / 'Docs/AssetIntake/Downloads'
    candidate = root / relative
    checked = (root / 'Docs', root / 'Docs/AssetIntake', downloads, candidate)
    if any(part.is_symlink() for part in checked):
        raise ValueError('approved archive path contains a symlink')
    if not candidate.is_file():
        raise ValueError('approved archive missing')
    resolved_downloads, resolved = downloads.resolve(), candidate.resolve()
    try:
        resolved_downloads.relative_to(root)
        resolved.relative_to(resolved_downloads)
    except ValueError as exc:
        raise ValueError('archive escaped repository/download root') from exc
    if resolved != (resolved_downloads / Path(expected).name):
        raise ValueError('archive resolved to unexpected path')
    return resolved


def read_approved_snapshots(root: Path, manifest_path: Path, approved=None):
    """Validate exact package identity/path/fixed digest and return immutable ZIP bytes."""
    root = Path(root).resolve()
    approved = APPROVED_PACKAGES if approved is None else approved
    manifest_path = Path(manifest_path)
    expected_manifest = root / 'Docs/AssetIntake/DirectDownload/manifest.json'
    if manifest_path != expected_manifest:
        raise ValueError('manifest must be the fixed repository intake manifest')
    if any(part.is_symlink() for part in (root / 'Docs', root / 'Docs/AssetIntake',
                                          root / 'Docs/AssetIntake/DirectDownload', manifest_path)):
        raise ValueError('manifest path contains a symlink')
    try:
        manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
        packages = manifest['packages']
    except (OSError, ValueError, KeyError, TypeError) as exc:
        raise ValueError('invalid approved-package manifest') from exc
    if not isinstance(packages, list):
        raise ValueError('manifest packages must be a list')
    by_id = {}
    for pack in packages:
        if not isinstance(pack, dict) or not isinstance(pack.get('packageId'), str):
            raise ValueError('invalid package record')
        package_id = pack['packageId']
        if package_id in by_id:
            raise ValueError('duplicate package ID')
        by_id[package_id] = pack
    if set(by_id) != set(approved):
        raise ValueError('manifest package IDs do not match the approved set')

    snapshots = {}
    for package_id, rule in approved.items():
        record = by_id[package_id]
        if record.get('downloadedPath') != rule['path']:
            raise ValueError(f'unapproved path for {package_id}')
        if record.get('sha256') != rule['sha256']:
            raise ValueError(f'unapproved manifest hash for {package_id}')
        path = _safe_package_path(root, record['downloadedPath'], rule['path'])
        size_before = path.stat().st_size
        if size_before > MAX_ARCHIVE_BYTES:
            raise ValueError('approved archive exceeds snapshot byte limit')
        with path.open('rb') as stream:
            snapshot = stream.read(MAX_ARCHIVE_BYTES + 1)
        if len(snapshot) > MAX_ARCHIVE_BYTES or len(snapshot) != size_before:
            raise ValueError('archive changed or exceeded limit while snapshotting')
        digest = hashlib.sha256(snapshot).hexdigest()
        if digest != rule['sha256']:
            raise ValueError(f'archive bytes do not match fixed approved hash for {package_id}')
        if record.get('archiveBytes') != len(snapshot):
            raise ValueError(f'archive size disagrees with manifest for {package_id}')
        limits = {'max_archive_bytes': MAX_ARCHIVE_BYTES, 'max_entries': 5000,
                  'max_total_uncompressed': 100_000_000, 'max_file_uncompressed': 20_000_000,
                  'max_compression_ratio': 200.0}
        inventory = inspect_zip_bytes(snapshot, source_name=rule['path'], limits=limits)
        snapshots[rule['group']] = {'package_id': package_id, 'path': rule['path'],
                                    'sha256': digest, 'bytes': snapshot, 'inventory': inventory}
    return snapshots


def _preflight_outputs(root: Path):
    output = root / 'Docs/AssetIntake/Downloads/preview_staging/batch2'
    evidence = root / 'Docs/AssetIntake/Preview/selection_batch2.json'
    expected = [output / (group + '_' + name + '.glb') for group, name, _ in SELECTION]
    expected.extend((output / 'selection.json', evidence))
    guarded_dirs = (root / 'Docs', root / 'Docs/AssetIntake', root / 'Docs/AssetIntake/Downloads',
                    root / 'Docs/AssetIntake/Downloads/preview_staging', output, evidence)
    if any(path.is_symlink() for path in guarded_dirs):
        raise ValueError('preview output path contains a symlink')
    for path in expected:
        if path.exists() or path.is_symlink():
            raise FileExistsError('Refuse to overwrite preview output: ' + str(path))
    return output, evidence


def prepare_batch(root: Path = ROOT, approved=None):
    root = Path(root).resolve()
    output, evidence = _preflight_outputs(root)
    snapshots = read_approved_snapshots(root, root / 'Docs/AssetIntake/DirectDownload/manifest.json', approved)
    prepared = []
    archives = {group: zipfile.ZipFile(io.BytesIO(row['bytes']), 'r') for group, row in snapshots.items()}
    try:
        for group, name, member in SELECTION:
            pack = snapshots[group]
            archive = archives[group]
            info = archive.getinfo(member)
            if info.file_size > MAX_MEMBER:
                raise ValueError('selected member too large')
            with archive.open(info) as stream:
                source_data = stream.read(MAX_MEMBER + 1)
            if len(source_data) != info.file_size or len(source_data) > MAX_MEMBER:
                raise ValueError('selected member exceeded or disagreed with declared size')
            palette = b''
            if group == 'food':
                palette_info = archive.getinfo(PALETTE_MEMBER)
                if palette_info.file_size > MAX_PALETTE_BYTES:
                    raise ValueError('approved palette exceeds byte limit')
                with archive.open(palette_info) as stream:
                    palette = stream.read(MAX_PALETTE_BYTES + 1)
                if len(palette) != palette_info.file_size or len(palette) > MAX_PALETTE_BYTES:
                    raise ValueError('palette exceeded or disagreed with declared size')
            data = embed_approved_palette(source_data, palette)
            metadata = validate_glb(data)
            filename = group + '_' + name + '.glb'
            prepared.append((filename, data, {
                'name': name, 'group': group, 'filename': filename,
                'source_archive': pack['path'], 'source_archive_sha256': pack['sha256'],
                'source_member': member, 'bytes': len(data),
                'sha256': hashlib.sha256(data).hexdigest(),
                'source_bytes': len(source_data),
                'source_sha256': hashlib.sha256(source_data).hexdigest(),
                'palette_embedded_for_preview': group == 'food',
                'palette_sha256': hashlib.sha256(palette).hexdigest() if palette else None,
                **metadata,
            }))
    finally:
        for archive in archives.values():
            archive.close()
    result = {'purpose': 'asset review only; not gameplay; independently normalized display scale',
              'license': 'Kenney CC0 package notices; project/third-party acceptance remains separate',
              'archive_inventory_mode': 'metadata only; selected allowlisted members streamed in memory',
              'assets': [row for _, _, row in prepared]}
    return output, evidence, prepared, result


def main(root: Path = ROOT):
    output, evidence, prepared, result = prepare_batch(root)
    # Preflight all intended paths again immediately before making directories/writes.
    for filename, _, _ in prepared:
        if (output / filename).exists():
            raise FileExistsError('Refuse to overwrite preview output: ' + filename)
    for path in (output / 'selection.json', evidence):
        if path.exists() or path.is_symlink():
            raise FileExistsError('Refuse to overwrite selection metadata: ' + str(path))
    output.mkdir(parents=True, exist_ok=True)
    evidence.parent.mkdir(parents=True, exist_ok=True)
    prepared_paths = [(output / filename, data, row) for filename, data, row in prepared]
    for target, data, _ in prepared_paths:
        with target.open('xb') as stream:
            stream.write(data)
    text = json.dumps(result, indent=2)
    with (output / 'selection.json').open('x', encoding='utf-8') as stream:
        stream.write(text)
    with evidence.open('x', encoding='utf-8') as stream:
        stream.write(text)
    print('Staged', len(prepared), 'self-contained GLBs;', sum(len(data) for _, data, _ in prepared), 'bytes')


if __name__ == '__main__':
    main()
