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
    # Structural pieces for the PicnicLawn corner map (2026-11-15 fifth batch).
    # Only ids that exist in prop_catalog.csv with a real three-axis are here:
    # picnic_blanket, picnic_basket, cooler_box, folding_stool, flower_pot_large
    # and collapsed_bench.  The plan's storage_crate / folding_table / tray /
    # plank have no catalog row (or no real three-axis), so they are not
    # invented.
    'picnic_blanket',
    'picnic_basket',
    'cooler_box',
    'folding_stool',
    'flower_pot_large',
    'collapsed_bench',
    'wood_plank',
    'serving_tray',
    'folding_table',
    # Remaining absorbable structural pieces of the first level (2026-11-15
    # sixth batch).  PICNIC_CORNER_LAYOUT.md 4.1 uses 13 structure defs; nine of
    # them had no model, so they are built here with the layout's real
    # three-axis.  None of them has a prop_catalog.csv row (the four U3 defs are
    # new, the other five were cut from the fifth batch), so each model module
    # declares DOC_ROW.
    'book_stack',
    'beer_crate',
    'crate',
    'storage_bin',
    'crate_large',
    'cooler_box_large',
    'picnic_bench',
    'lounge_chair',
    'stroller',
    # Picnic lawn catalogue extension, batch 1: paper / tableware flatware.
    # 2026-11-15; 40 new defs across four shape classes, 10 here.  None has a
    # prop_catalog.csv row, so every module declares DOC_ROW.
    'stamp',
    'sugar_packet',
    'coin',
    'ticket_stub',
    'paper_doily',
    'butter_pat',
    'postcard',
    'pocket_notebook',
    'cutting_board',
    'table_knife',
    # Picnic lawn catalogue extension, batch 2: round small goods and jars.
    'button',
    'marble',
    'hazelnut',
    'jar_lid',
    'tape_roll',
    'cookie_cutter_small',
    'salt_shaker',
    'jam_jar',
    'candle',
    'rolling_pin_small',
    # Picnic lawn catalogue extension, batch 3: long thin cutlery and sticks.
    'toothpick',
    'twig',
    'grass_blade',
    'straw_short',
    'popsicle_stick',
    'pencil_stub',
    'bottle_opener',
    'pen_ballpoint',
    'fork',
    'chopstick',
    # Picnic lawn catalogue extension, batch 4: blocky packets and small gear.
    'die',
    'eraser',
    'matchbox',
    'pill_box',
    'candy_bar',
    'toy_car_small',
    'glasses_case',
    'stapler',
    'soap_bar',
    'sauce_bottle',
    # Dung beetle meadow pilot, batch A: small round farm-yard pieces.
    'grain_kernel',
    'acorn_cap',
    'snail_shell',
    'farm_egg',
    'sheep_bell',
    'smooth_pebble',
    'flint_stone',
    'dandelion_puff',
    'wool_tuft',
    'beetle_figurine',
    # Messy room pilot, batch A: small room clutter round and blocky.
    'toy_brick',
    'battery_aa',
    'crumpled_paper',
    'earbuds_case',
    'ceramic_mug',
    'game_controller',
    'cereal_bowl',
    'tissue_box',
    'slipper',
    'hairbrush',
    # Dung beetle meadow pilot, batch B: straw, feathers, dry leaves, dung balls.
    'hay_wisp',
    'straw_tuft',
    'feather',
    'leather_strap',
    'clover_sprig',
    'dry_leaf_curl',
    'wheat_stalk',
    'fence_peg',
    'dung_ball_small',
    'dung_ball_medium',
    # Messy room pilot, batch B: soft goods, electronics, packaging.
    'sticky_note',
    'keychain',
    'phone_charger',
    'sunglasses',
    'book_paperback',
    'remote_control',
    'chip_bag',
    'sock_single',
    't_shirt',
    'pizza_box',
]


def load(pid):
    if pid not in ORDER:
        raise KeyError('unknown prop %r (known: %s)' % (pid, ', '.join(ORDER)))
    return importlib.import_module('models.' + pid)
