"""Regenerate SHA256SUMS for the current deliverables without rebuilding.

  blender --background --factory-startup --disable-autoexec --python sign.py
"""

import os
import sys

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import build_all  # noqa: E402

entries = build_all.write_sums()
print('SHA256SUMS %d entries' % len(entries))
print('SIGN_OK', flush=True)
