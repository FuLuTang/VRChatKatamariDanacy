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
    # Remaining StoryLevel placeholders with a real size source (2026-11-15
    # fourth batch): the food / tableware / garden pieces that had no model yet.
    # Every one of them comes from PROP_SET_PLAN.md 3.3-3.4 with a real three
    # axis, and every one keeps its catalog id.
    'bread_slice',
    'cheese_slice',
    'fallen_leaf',
    'sandwich',
    'spoon',
    'small_potted_plant',
]


def load(pid):
    if pid not in ORDER:
        raise KeyError('unknown prop %r (known: %s)' % (pid, ', '.join(ORDER)))
    return importlib.import_module('models.' + pid)
