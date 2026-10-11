import hashlib
import json
import struct
import tempfile
import unittest
import warnings
import zipfile
from pathlib import Path
from unittest.mock import patch

from Docs.tools.asset_preview import stage_glb
from Docs.tools.asset_preview.stage_glb import embed_approved_palette, validate_glb

ROOT = Path(__file__).resolve().parents[2]


def glb(doc=None, binary=b'\x00\x00\x00\x00'):
    if doc is None:
        doc = {'asset': {'version': '2.0'}, 'buffers': [{'byteLength': len(binary)}]}
    encoded = json.dumps(doc).encode('utf-8')
    encoded += b' ' * (-len(encoded) % 4)
    binary += b'\x00' * (-len(binary) % 4)
    body = struct.pack('<II', len(encoded), 0x4E4F534A) + encoded
    body += struct.pack('<II', len(binary), 0x004E4942) + binary
    return struct.pack('<4sII', b'glTF', 2, len(body) + 12) + body


def png_header(width=512, height=512):
    # Deliberately header-only fixture. The preview gate does not decode/CRC-check PNG.
    return b'\x89PNG\r\n\x1a\n' + struct.pack('>I', 13) + b'IHDR' + struct.pack('>II', width, height)


def build_test_archives(root):
    downloads = root / 'Docs/AssetIntake/Downloads'
    downloads.mkdir(parents=True)
    rules = {
        'KENNEY-FOOD-KIT-2.0': ('food', 'Docs/AssetIntake/Downloads/kenney_food-kit.zip'),
        'KENNEY-NATURE-KIT-2.1': ('nature', 'Docs/AssetIntake/Downloads/kenney_nature-kit.zip'),
    }
    approved, records = {}, []
    for package_id, (group, relative) in rules.items():
        archive_path = root / relative
        entries = []
        if group == 'food':
            source = glb({'asset': {'version': '2.0'}, 'buffers': [{'byteLength': 4}],
                          'images': [{'uri': 'Textures/colormap.png'}]})
            for _, name, member in stage_glb.SELECTION:
                if _ == 'food':
                    entries.append((member, source))
            entries.append((stage_glb.PALETTE_MEMBER, png_header()))
        else:
            source = glb()
            for selected_group, name, member in stage_glb.SELECTION:
                if selected_group == 'nature':
                    entries.append((member, source))
        with warnings.catch_warnings():
            warnings.simplefilter('ignore', UserWarning)
            with zipfile.ZipFile(archive_path, 'w', compression=zipfile.ZIP_STORED) as archive:
                for member, payload in entries:
                    archive.writestr(member, payload)
        snapshot = archive_path.read_bytes()
        digest = hashlib.sha256(snapshot).hexdigest()
        approved[package_id] = {'group': group, 'path': relative, 'sha256': digest}
        records.append({'packageId': package_id, 'downloadedPath': relative,
                        'sha256': digest, 'archiveBytes': len(snapshot)})
    (root / 'Docs/AssetIntake/DirectDownload').mkdir(parents=True)
    manifest_path = root / 'Docs/AssetIntake/DirectDownload/manifest.json'
    manifest_path.write_text(json.dumps({'packages': records}), encoding='utf-8')
    return approved, manifest_path


class AssetPreviewTests(unittest.TestCase):
    def test_minimal_embedded_glb(self):
        self.assertFalse(validate_glb(glb())['external_resources'])

    def test_rejects_header_truncation_and_chunk_bounds(self):
        for data in (b'', glb()[:-1], b'nope' + glb()[4:], glb() + b'\x00'):
            with self.subTest(size=len(data)), self.assertRaises(ValueError):
                validate_glb(data)
        data = bytearray(glb())
        struct.pack_into('<I', data, 12, 1_000_000)
        with self.assertRaises(ValueError):
            validate_glb(bytes(data))

    def test_malformed_glb_shapes_use_public_value_error(self):
        malformed_docs = ([1], {'asset': []}, {'asset': {'version': '2.0'}, 'buffers': [None]},
                          {'asset': {'version': '2.0'}, 'buffers': [{'byteLength': True}]},
                          {'asset': {'version': '2.0'}, 'buffers': [{'byteLength': 4}], 'images': [None]})
        for doc in malformed_docs:
            with self.subTest(doc=doc), self.assertRaises(ValueError):
                validate_glb(glb(doc))
        bad_json = bytearray(glb())
        bad_json[20:24] = b'nope'
        with self.assertRaises(ValueError):
            validate_glb(bytes(bad_json))

    def test_rejects_resource_uris_and_extensions(self):
        for uri in ('http://host/file.png', 'file:///C:/secret', '../texture.png', 'data:image/png;base64,abc'):
            doc = {'asset': {'version': '2.0'}, 'buffers': [{'byteLength': 4}], 'images': [{'uri': uri}]}
            with self.subTest(uri=uri), self.assertRaisesRegex(ValueError, 'URI'):
                validate_glb(glb(doc))
        doc = {'asset': {'version': '2.0'}, 'buffers': [{'byteLength': 4}],
               'extensionsRequired': ['UNKNOWN']}
        with self.assertRaisesRegex(ValueError, 'extensions'):
            validate_glb(glb(doc))

    def test_rejects_buffer_bounds_accessor_count_and_bool_indexes(self):
        invalid_docs = (
            {'bufferViews': [{'buffer': 0, 'byteOffset': 3, 'byteLength': 8}]},
            {'accessors': [{'count': 1_000_000}]},
            {'bufferViews': [{'buffer': True, 'byteOffset': 0, 'byteLength': 4}]},
            {'images': [{'bufferView': True, 'mimeType': 'image/png'}]},
        )
        for extra in invalid_docs:
            doc = {'asset': {'version': '2.0'}, 'buffers': [{'byteLength': 4}], **extra}
            with self.subTest(extra=extra), self.assertRaises(ValueError):
                validate_glb(glb(doc))

    def test_embeds_only_exact_allowlisted_palette_and_preserves_source(self):
        source = glb({'asset': {'version': '2.0'}, 'buffers': [{'byteLength': 4}],
                      'images': [{'uri': 'Textures/colormap.png'}]})
        source_hash = hashlib.sha256(source).hexdigest()
        result = embed_approved_palette(source, png_header())
        self.assertEqual(hashlib.sha256(source).hexdigest(), source_hash)
        self.assertNotEqual(hashlib.sha256(result).hexdigest(), source_hash)
        self.assertEqual(validate_glb(result)['image_dimensions_from_header'], [[512, 512]])
        for uri in ('../Textures/colormap.png', 'https://example.com/colormap.png',
                    'textures/colormap.png', 'Textures\\colormap.png'):
            doc = {'asset': {'version': '2.0'}, 'buffers': [{'byteLength': 4}], 'images': [{'uri': uri}]}
            with self.subTest(uri=uri), self.assertRaisesRegex(ValueError, 'unapproved'):
                embed_approved_palette(glb(doc), png_header())

    def test_palette_size_and_inconsistent_image_shape_rejected(self):
        doc = {'asset': {'version': '2.0'}, 'buffers': [{'byteLength': 4}],
               'images': [{'uri': 'Textures/colormap.png', 'bufferView': 0}]}
        with self.assertRaisesRegex(ValueError, 'bufferView'):
            embed_approved_palette(glb(doc), png_header())
        doc['images'] = [{'uri': 'Textures/colormap.png'}]
        with patch.object(stage_glb, 'MAX_PALETTE_BYTES', 23):
            with self.assertRaisesRegex(ValueError, 'size'):
                embed_approved_palette(glb(doc), png_header())
        with self.assertRaisesRegex(ValueError, 'dimension'):
            embed_approved_palette(glb(doc), png_header(100_000, 100_000))

    def test_texture_count_and_aggregate_pixels_bounded(self):
        binary = bytearray(b'\x00' * 4)
        views, images = [], []
        for _ in range(2):
            png = png_header(4096, 2048)
            offset = len(binary)
            binary.extend(png)
            views.append({'buffer': 0, 'byteOffset': offset, 'byteLength': len(png)})
            images.append({'bufferView': len(views) - 1, 'mimeType': 'image/png'})
        doc = {'asset': {'version': '2.0'}, 'buffers': [{'byteLength': len(binary)}],
               'bufferViews': views, 'images': images}
        self.assertEqual(len(validate_glb(glb(doc, bytes(binary)))['image_dimensions_from_header']), 2)
        images.append({'bufferView': 0, 'mimeType': 'image/png'})
        with self.assertRaisesRegex(ValueError, 'pixel limit'):
            validate_glb(glb({**doc, 'images': images}, bytes(binary)))

    def test_manifest_package_id_path_hash_and_root_constraints(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            approved, manifest_path = build_test_archives(root)
            snapshots = stage_glb.read_approved_snapshots(root, manifest_path, approved)
            self.assertEqual(set(snapshots), {'food', 'nature'})
            records = json.loads(manifest_path.read_text())
            records['packages'][0]['downloadedPath'] = '../../outside.zip'
            manifest_path.write_text(json.dumps(records))
            with self.assertRaisesRegex(ValueError, 'path'):
                stage_glb.read_approved_snapshots(root, manifest_path, approved)
            records['packages'][0]['downloadedPath'] = approved[records['packages'][0]['packageId']]['path']
            records['packages'][0]['sha256'] = '0' * 64
            manifest_path.write_text(json.dumps(records))
            with self.assertRaisesRegex(ValueError, 'hash'):
                stage_glb.read_approved_snapshots(root, manifest_path, approved)

    def test_manifest_order_irrelevant_and_archive_snapshot_digest_pinned(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            approved, manifest_path = build_test_archives(root)
            manifest = json.loads(manifest_path.read_text())
            manifest['packages'].reverse()
            manifest_path.write_text(json.dumps(manifest))
            with patch.object(stage_glb, 'inspect_zip_bytes', wraps=stage_glb.inspect_zip_bytes) as inspect:
                snapshots = stage_glb.read_approved_snapshots(root, manifest_path, approved)
            self.assertEqual(set(snapshots), {'food', 'nature'})
            for snapshot in snapshots.values():
                self.assertEqual(hashlib.sha256(snapshot['bytes']).hexdigest(), snapshot['sha256'])
            self.assertEqual(inspect.call_count, 2)

    def test_staging_reads_only_allowlisted_members_never_archive_extract(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            approved, _ = build_test_archives(root)
            with patch.object(stage_glb, 'APPROVED_PACKAGES', approved), \
                    patch.object(zipfile.ZipFile, 'extract', side_effect=AssertionError('extract forbidden')), \
                    patch.object(zipfile.ZipFile, 'extractall', side_effect=AssertionError('extractall forbidden')):
                output, evidence, prepared, result = stage_glb.prepare_batch(root)
            self.assertEqual(len(prepared), 12)
            self.assertFalse(output.exists())
            self.assertFalse(evidence.exists())
            food = next(row for _, _, row in prepared if row['group'] == 'food')
            nature = next(row for _, _, row in prepared if row['group'] == 'nature')
            self.assertNotEqual(food['source_sha256'], food['sha256'])
            self.assertEqual(nature['source_sha256'], nature['sha256'])
            self.assertEqual(result['archive_inventory_mode'], 'metadata only; selected allowlisted members streamed in memory')

    def test_main_refuses_any_existing_output_before_reading_sources(self):
        for existing_relative in (
                'Docs/AssetIntake/Downloads/preview_staging/batch2/food_apple.glb',
                'Docs/AssetIntake/Preview/selection_batch2.json'):
            with self.subTest(existing=existing_relative), tempfile.TemporaryDirectory() as temp:
                root = Path(temp)
                existing = root / existing_relative
                existing.parent.mkdir(parents=True)
                existing.write_bytes(b'keep')
                with patch.object(stage_glb, 'read_approved_snapshots',
                                  side_effect=AssertionError('source read too early')):
                    with self.assertRaises(FileExistsError):
                        stage_glb.prepare_batch(root)
                self.assertEqual(existing.read_bytes(), b'keep')

    def test_fixed_package_identity_and_hash_constants(self):
        self.assertEqual(stage_glb.APPROVED_PACKAGES['KENNEY-FOOD-KIT-2.0']['path'],
                         'Docs/AssetIntake/Downloads/kenney_food-kit.zip')
        self.assertEqual(stage_glb.APPROVED_PACKAGES['KENNEY-FOOD-KIT-2.0']['sha256'],
                         'cdad90853682499b94c9fda2f87678b24bfd8f3264e0ed323f6b6a27fd7c6f6f')
        self.assertEqual(stage_glb.APPROVED_PACKAGES['KENNEY-NATURE-KIT-2.1']['path'],
                         'Docs/AssetIntake/Downloads/kenney_nature-kit.zip')
        self.assertEqual(stage_glb.APPROVED_PACKAGES['KENNEY-NATURE-KIT-2.1']['sha256'],
                         'fa7974a0d342bfe63c38664ba9f8ec1a4aab8ea25f099bdc56870e33588c4d9d')

    def test_main_writes_preview_hashes_and_repeat_run_refuses_overwrite(self):
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            approved, _ = build_test_archives(root)
            with patch.object(stage_glb, 'APPROVED_PACKAGES', approved), patch('builtins.print'):
                stage_glb.main(root)
                output = root / 'Docs/AssetIntake/Downloads/preview_staging/batch2'
                evidence = root / 'Docs/AssetIntake/Preview/selection_batch2.json'
                selection = json.loads((output / 'selection.json').read_text())
                self.assertEqual(len(selection['assets']), 12)
                self.assertTrue(evidence.is_file())
                for asset in selection['assets']:
                    data = (output / asset['filename']).read_bytes()
                    self.assertEqual(hashlib.sha256(data).hexdigest(), asset['sha256'])
                    if asset['group'] == 'food':
                        self.assertNotEqual(asset['source_sha256'], asset['sha256'])
                    else:
                        self.assertEqual(asset['source_sha256'], asset['sha256'])
                original = (output / 'food_apple.glb').read_bytes()
                with self.assertRaises(FileExistsError):
                    stage_glb.main(root)
                self.assertEqual((output / 'food_apple.glb').read_bytes(), original)

    def test_renderer_guard_precedes_factory_reset_and_records_units_limits(self):
        import ast
        source = (ROOT / 'Docs/tools/asset_preview/render_windows.py').read_text(encoding='utf-8')
        compile(source, 'render_windows.py', 'exec')
        self.assertIn("if not bpy.app.background:", source)
        self.assertIn("'--factory-startup' not in startup_args", source)
        self.assertIn("'--disable-autoexec' not in startup_args", source)
        self.assertLess(source.index('if not bpy.app.background:'), source.index('read_factory_settings'))
        self.assertLess(source.index('if any(contains_unity_project_component'), source.index('if not source.is_dir()'))
        tree = ast.parse(source)
        helper = next(node for node in tree.body if isinstance(node, ast.FunctionDef)
                      and node.name == 'contains_unity_project_component')
        isolated = ast.Module(body=[helper], type_ignores=[])
        namespace = {'Path': Path, 'PureWindowsPath': __import__('pathlib').PureWindowsPath}
        exec(compile(isolated, 'path_guard_only', 'exec'), namespace)
        guard = namespace['contains_unity_project_component']
        self.assertFalse(guard('D:/VRChatKatamariPreview/20261011Batch2Verified/input/script.py'))
        self.assertFalse(guard('D:/VRChatKatamariPreview/20261011Batch2Verified/output'))
        self.assertTrue(guard('D:/VRChatKatamariDanacy/VRChatKatamariDanacyUnity/Assets'))
        self.assertTrue(guard('d:/elsewhere/vrchatkatamaridanacyunity/Assets'))
        self.assertFalse(guard('D:/elsewhere/VRChatKatamariDanacyUnity-old'))
        self.assertIn('any(not math.isfinite(v) for v in (*lo, *hi, *dims))', source)
        self.assertIn('not math.isfinite(scale)', source)
        self.assertIn('Blender import units; not asserted real-world meters or pickup diameter', source)
        self.assertIn("'DISPLAY_ONLY_normalization'", source)
        self.assertIn('MAX_TRIANGLES', source)
        self.assertIn('MAX_TEXTURE_PIXELS', source)

    @unittest.skipUnless((ROOT / 'Docs/AssetIntake/Preview/selection.json').is_file() and
                         (ROOT / 'Docs/AssetIntake/Preview/renders/render_report.json').is_file(),
                         'Generated review-render evidence is absent; this test is optional/non-hermetic')
    def test_real_preview_artifact_consistency_when_present(self):
        selection = json.loads((ROOT / 'Docs/AssetIntake/Preview/selection.json').read_text())
        report = json.loads((ROOT / 'Docs/AssetIntake/Preview/renders/render_report.json').read_text())
        self.assertEqual(len(selection['assets']), 12)
        self.assertEqual({a['name'] for a in selection['assets']}, {a['name'] for a in report['assets']})
        for asset in selection['assets']:
            self.assertEqual(len(asset['sha256']), 64)
            self.assertEqual(len(asset['source_sha256']), 64)
            if asset['group'] == 'food':
                self.assertNotEqual(asset['source_sha256'], asset['sha256'])
            else:
                self.assertEqual(asset['source_sha256'], asset['sha256'])
        for asset in report['assets']:
            self.assertGreater(asset['preview_uniform_scale'], 0)
            self.assertTrue(asset['coordinate_status'].startswith('OPEN'))
            self.assertIn('not asserted real-world meters', asset['units_note'])
            self.assertTrue(all(mesh['triangles'] > 0 for mesh in asset['imported_meshes']))
            self.assertTrue((ROOT / 'Docs/AssetIntake/Preview/renders' /
                             f"{asset['group']}_{asset['name']}.png").is_file())


if __name__ == '__main__':
    unittest.main()
