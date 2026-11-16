"""Derive each prop's support pointer from geometry - the import-side half of the rule.

Why this exists
---------------
The retail levels do not ship a support table: of 987 measured item-on-item contacts in
the dump, only 28% carry a ``parentProp`` pointer, and 72% of the 322 stacks have none at
all.  The original therefore resolves "what is holding this up" from geometry at run time.
Our runtime does the same per tick (``PROP_BEHAVIOURS`` section 10.9), but it needs a
*starting* answer for two things the per-tick re-check cannot invent:

* the ledger/absorb path must know, at import time, which prop carries which, so that
  eating a parent can take its children with it in the same frame (not one tick later);
* a level author must be able to see and fix a contact the geometry got wrong.

So the pointer is **derived here, once, and stored** (``parent`` + ``parentDerived``), and
the runtime still re-checks "is anything left under me" every tick - the stored pointer is
an optimisation and a review artefact, never the sole source of truth.

The criterion (calibrated on the retail dump)
--------------------------------------------
``Docs/Research/KatamariBallRules/STACKS_IN_ORIGINAL.md`` section 2: a prop B sits on a prop
A when, using **rotation-aware world AABBs**,

* the horizontal footprints overlap (touching counts) - ``|dx| <= (wA+wB)/2``, same in z;
* ``gap = bottom(B) - top(A)`` lies in ``[-0.30, +0.50] * min(height(B), height(A))``:
  mildly embedded or hovering slightly is normal (median gap -0.03 m, 82% inside +-0.25 of
  a min height), and the window scales with the smaller item so a 3 cm eraser is not
  allowed the slack of a 2 m box;
* B's top is above A's top (the supports form a forest, not a graph).

``min height`` is the smaller of the two *world* heights, i.e. after the prop's own
rotation, so a prop lying on its side is measured as it stands, not as its model AABB says.
The single best support is the candidate with the **highest top surface** below the item;
ties go to the larger footprint overlap, then to the lower itemId, so the result is
deterministic and does not depend on the order of ``props[]``.

What is deliberately *not* a parent: the ground and the level's structures (walls, ramps,
shelves).  They are not props, so they cannot be eaten, and the runtime keeps them as
static support surfaces - a prop standing on the floor gets no pointer at all.

One implementation: the window lives in ``placement_core.PARENT_*_FRACTION``, is computed by
``placement_core.parent_gap_window`` (used here and by the prefilter below with a
deliberately wider bound), and is what ``_check_parents`` / ``_check_support_and_overlap``
validate a stored link against - so a link this module produces cannot fail the gate.
"""

from Tools.levelbuild import placement_core as core

#: Contact window, in units of the smaller world height (single source: the validator).
CONTACT_EMBED_FRACTION = core.PARENT_EMBED_FRACTION
CONTACT_GAP_FRACTION = core.PARENT_GAP_FRACTION

#: Side of one bucket of the XZ grid used to keep the scan near-linear.  Only a lookup
#: accelerator: the contact test itself is exact, so changing this cannot change a result.
GRID_CELL_M = 1.0

#: A prop whose footprint covers more cells than this is kept in one list that every item
#: scans (a 9 m building would otherwise be inserted into hundreds of buckets).
GRID_MAX_CELLS = 1024


#: Rotation-aware world AABB of one prop, or ``None`` when its fields do not parse.
#: It lives in ``placement_core`` because the validator, the derivation and the runtime
#: reference model all need the same box; this name is kept for the existing callers.
prop_box = core.prop_box


def _footprint_overlap(child, supporter):
    """Overlap area of two world-AABB footprints on XZ; ``None`` when they do not touch."""
    dx = min(child[1][0], supporter[1][0]) - max(child[0][0], supporter[0][0])
    dz = min(child[1][2], supporter[1][2]) - max(child[0][2], supporter[0][2])
    if dx < -1e-9 or dz < -1e-9:
        return None
    return dx * dz


class _Grid:
    """XZ bucket index over supporter boxes (``payload`` is the itemId)."""

    def __init__(self, cell=GRID_CELL_M):
        self.cell = cell
        self.buckets = {}
        self.oversized = []

    def _range(self, low, high):
        return (int(low // self.cell), int(high // self.cell))

    def insert(self, item_id, box):
        x0, z0 = self._range(box[0][0], box[0][2])
        x1, z1 = self._range(box[1][0], box[1][2])
        if (x1 - x0 + 1) * (z1 - z0 + 1) > GRID_MAX_CELLS:
            self.oversized.append((item_id, box))
            return
        for ix in range(x0, x1 + 1):
            for iz in range(z0, z1 + 1):
                self.buckets.setdefault((ix, iz), []).append((item_id, box))

    def near(self, box):
        x0, z0 = self._range(box[0][0], box[0][2])
        x1, z1 = self._range(box[1][0], box[1][2])
        seen = {}
        for item_id, other in self.oversized:
            seen[item_id] = other
        for ix in range(x0, x1 + 1):
            for iz in range(z0, z1 + 1):
                for item_id, other in self.buckets.get((ix, iz), ()):
                    seen[item_id] = other
        return seen


def derive_supports(doc, ids=None):
    """``{itemId: parentItemId}`` - one best support per prop that rests on another prop.

    ``doc`` is a placement document (anything with a ``props`` list).  ``ids`` restricts
    which props may *receive* a parent (every prop is still a candidate supporter, so
    ``derive_supports(doc, {"crate_3"})`` answers "what holds crate_3 up").  Props whose
    geometry does not parse are skipped, never linked.
    """
    props = [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]
    wanted = None if ids is None else set(ids)

    items = []
    for prop in props:
        item_id = prop.get("itemId")
        if not isinstance(item_id, str) or not item_id:
            continue
        box = prop_box(prop)
        if box is None:
            continue
        items.append((item_id, box))
    boxes = dict(items)

    grid = _Grid()
    for item_id, box in items:
        grid.insert(item_id, box)

    links = {}
    for item_id, box in items:
        if wanted is not None and item_id not in wanted:
            continue
        best = _best_support(item_id, box, grid, boxes)
        if best is not None:
            links[item_id] = best
    return _break_cycles(links)


def _best_support(item_id, box, grid, boxes):
    """Highest top surface below this prop whose footprint it stands on."""
    bottom = box[0][1]
    height = box[1][1] - box[0][1]
    if height <= 0.0:
        return None
    # A candidate top must lie in [bottom - gap*m, bottom + embed*m] for
    # m = min(height, that top's height) <= height, so this window is a superset:
    # hovering (gap > 0) puts the top *below* the bottom, sinking puts it above.
    low = bottom - CONTACT_GAP_FRACTION * height
    high = bottom + CONTACT_EMBED_FRACTION * height
    best = None
    best_key = None
    for other_id, other in grid.near(box).items():
        if other_id == item_id:
            continue
        top = other[1][1]
        if top < low or top > high:
            continue
        if top >= box[1][1]:
            continue                     # a support cannot start above the item's own top
        gap = bottom - top
        pair_low, pair_high = core.parent_gap_window(box, other)
        if gap < pair_low or gap > pair_high:
            continue
        overlap = _footprint_overlap(box, other)
        if overlap is None:
            continue
        key = (top, overlap, _descending_id(other_id))
        if best_key is None or key > best_key:
            best, best_key = other_id, key
    return best


class _descending_id(str):
    """Sort key that makes the *lower* itemId win a tie (``max`` picks the largest)."""

    def __lt__(self, other):
        return str.__gt__(self, other)

    def __gt__(self, other):
        return str.__lt__(self, other)


def _break_cycles(links):
    """Enforce the forest property; the derivation cannot create a ring, but be explicit.

    A ring would only appear if two boxes' tops were equal (the "strictly below" test
    allows ``top >= child top`` for numerically equal boxes).  The link dropped is the one
    from the lowest prop of the ring, so the highest item keeps its support.
    """
    changed = True
    while changed:
        changed = False
        for start in sorted(links):
            chain, seen = [start], {start}
            while True:
                nxt = links.get(chain[-1])
                if nxt is None:
                    break
                if nxt in seen:
                    ring = chain[chain.index(nxt):]
                    victim = min(ring)
                    del links[victim]
                    changed = True
                    break
                chain.append(nxt)
                seen.add(nxt)
            if changed:
                break
    return links


def propose(doc, ids=None):
    """Derivation *as a reviewable diff* - what ``apply`` would change, and what it would not.

    Returns::

        {"counts":  {...},
         "new":     [{"itemId", "parent", "gap", "declared"}],   # no pointer yet
         "agreed":  [...],                                       # already says the same
         "conflict":[{"itemId", "declared", "proposed"}],        # declared, but elsewhere
         "orphaned":[{"itemId", "declared"}],                    # declared, derivation disagrees
         "noSupport": [itemId...]}                              # on the ground / structures

    ``apply_supports`` writes ``new`` only: it never rewrites or clears an existing
    pointer (a hand-authored attachment must survive a re-run), and it never writes a null.
    """
    props = [prop for prop in doc.get("props") or [] if isinstance(prop, dict)]
    by_id = {}
    for prop in props:
        item_id = prop.get("itemId")
        if isinstance(item_id, str) and item_id:
            by_id.setdefault(item_id, prop)
    links = derive_supports(doc, ids)
    measurable = {item_id: prop_box(prop) for item_id, prop in by_id.items()}

    new, agreed, conflict, orphaned, no_support = [], [], [], [], []
    for item_id in sorted(by_id):
        prop = by_id[item_id]
        declared = core.prop_parent(prop)
        proposed = links.get(item_id)
        if proposed is None:
            if declared:
                orphaned.append({"itemId": item_id, "declared": declared})
            else:
                no_support.append(item_id)
            continue
        gap = None
        child, parent = measurable.get(item_id), measurable.get(proposed)
        if child is not None and parent is not None:
            gap = round(child[0][1] - parent[1][1], 4)
        if declared is None:
            new.append({"itemId": item_id, "parent": proposed, "gap": gap, "declared": None})
        elif declared == proposed:
            agreed.append({"itemId": item_id, "parent": proposed, "gap": gap})
        else:
            conflict.append({"itemId": item_id, "declared": declared, "proposed": proposed})
            # A conflicting declaration is also a link the file has and derivation does
            # not reproduce; the caller can see both sides in one list.
    counts = {
        "props": len(by_id),
        "measurable": sum(1 for box in measurable.values() if box is not None),
        "supports": len(links),
        "new": len(new),
        "agreed": len(agreed),
        "conflict": len(conflict),
        "orphaned": len(orphaned),
        "noSupport": len(no_support),
        "declared": sum(1 for prop in by_id.values() if core.prop_parent(prop)),
    }
    return {"counts": counts, "new": new, "agreed": agreed, "conflict": conflict,
            "orphaned": orphaned, "noSupport": no_support}


def apply_supports(doc, ids=None):
    """Write the derived pointers into ``doc`` (in place); returns :func:`propose`'s diff.

    Only ``new`` links are written, as ``parent`` + ``parentDerived: true``.  Nothing is
    cleared: a pointer the geometry no longer reproduces is reported (``orphaned``) and
    left for a human, because dropping it silently would turn a stacking bug into a
    floating prop.
    """
    diff = propose(doc, ids)
    by_id = {}
    for prop in doc.get("props") or []:
        if isinstance(prop, dict) and isinstance(prop.get("itemId"), str):
            by_id.setdefault(prop["itemId"], prop)
    written = []
    for entry in diff["new"]:
        prop = by_id.get(entry["itemId"])
        if prop is None:
            continue
        prop["parent"] = entry["parent"]
        prop["parentDerived"] = True
        written.append(entry["itemId"])
    diff["written"] = written
    return diff


def count_derived(doc):
    """Number of props whose stored pointer is a derived one (the report line's number)."""
    return sum(1 for prop in doc.get("props") or []
               if isinstance(prop, dict) and core.prop_parent(prop)
               and prop.get("parentDerived") is True)


def parents_from_document(doc):
    """``{itemId: parentItemId}`` of the pointers a document already declares."""
    links = {}
    for prop in doc.get("props") or []:
        if not isinstance(prop, dict):
            continue
        item_id = prop.get("itemId")
        parent = core.prop_parent(prop)
        if isinstance(item_id, str) and item_id and parent:
            links[item_id] = parent
    return links
