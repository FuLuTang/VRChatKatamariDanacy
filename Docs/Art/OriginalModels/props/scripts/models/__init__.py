"""Prop registry for the original prop batches."""

import importlib

ORDER = [
    'seed',
    'acorn',
    'pebble',
    'bottle_cap',
    'paper_scrap',
    'toy_ball',
    'napkin',
    'watering_can',
    # fruit / snack set, real-world sizes (2026-11-15 plan)
    'strawberry',
    'grapes',
    'cookie',
    'apple',
    'orange',
    'pear',
    'banana',
    'watermelon',
]


def load(pid):
    if pid not in ORDER:
        raise KeyError('unknown prop %r (known: %s)' % (pid, ', '.join(ORDER)))
    return importlib.import_module('models.' + pid)
