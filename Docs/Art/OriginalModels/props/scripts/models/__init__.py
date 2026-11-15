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
    # tableware / container set, real-world sizes (2026-11-15 plan).
    # The plan asked for a baguette, but prop_catalog.csv has no baguette row
    # (its bread entry is bread_slice, and the CSV note records that the whole
    # loaf was dropped); croissant is the catalog's whole baked good with a
    # real three-axis size, so it fills that slot.
    'cup',
    'soda_can',
    'juice_carton',
    'glass_bottle',
    'plate',
    'bowl',
    'thermos',
    'croissant',
]


def load(pid):
    if pid not in ORDER:
        raise KeyError('unknown prop %r (known: %s)' % (pid, ', '.join(ORDER)))
    return importlib.import_module('models.' + pid)
