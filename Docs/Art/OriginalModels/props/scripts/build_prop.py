"""Rebuild one prop only (no contact sheet / aggregate manifest).

  blender --background --factory-startup --disable-autoexec \
      --python build_prop.py -- seed
"""

import os
import sys

sys.dont_write_bytecode = True
HERE = os.path.dirname(os.path.abspath(__file__))
if HERE not in sys.path:
    sys.path.insert(0, HERE)

import build_all  # noqa: E402

argv = sys.argv
if '--' in argv:
    argv = argv[argv.index('--') + 1:]
pid = argv[0] if argv else os.environ.get('PROP_ID', 'seed')
build_all.run_single(pid)
