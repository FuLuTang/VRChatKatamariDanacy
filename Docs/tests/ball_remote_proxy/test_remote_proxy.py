import hashlib
import unittest
from dataclasses import replace

from Docs.Design.BallLayers.RemoteProxy.remote_proxy import (
    ProxyCapacityError, RemoteProxyCache, SummaryConflictError, SummaryError,
    SyntheticConfirmedSummary,
)


def _digest(fields):
    # Synthetic fixture digest only. It is not a T51 event, authentication, or wire format.
    return hashlib.sha256(repr(fields).encode("utf-8")).hexdigest()


def summary(instance="cup", generation=1, attach_sequence=7, event_id="evt-7",
            revision=0, context="round-A", ball="ball-1", position=(1.0, 0.0, 0.0),
            priority=1, visible=True, buried=False, removed=False, variant="cup-proxy",
            seed=31):
    fields = (context, ball, instance, generation, attach_sequence, event_id, revision,
              variant, seed, position, (0.0, 0.0, 0.0, 1.0), visible, buried, removed, priority)
    return SyntheticConfirmedSummary(
        context_id=context, ball_id=ball, instance_id=instance,
        generation=generation, attach_sequence=attach_sequence, event_id=event_id,
        revision=revision, summary_digest=_digest(fields), variant_id=variant,
        variant_seed=seed, position=position, presentation_priority=priority,
        visible=visible, buried=buried, removed=removed)


class RemoteProxyTests(unittest.TestCase):
    def test_duplicate_confirmation_is_idempotent_and_identity_is_upstream(self):
        cache = RemoteProxyCache(max_records=8, renderer_budget=4)
        event = summary()
        self.assertEqual(cache.apply(event), "accepted")
        before = cache.summaries
        self.assertEqual(cache.apply(event), "duplicate")
        self.assertEqual(cache.summaries, before)
        self.assertEqual(cache.tracked_count, 1)
        self.assertEqual(cache.summaries[0].attach_sequence, 7)
        self.assertFalse(hasattr(cache, "_next_sequence"))

    def test_same_identity_revision_conflict_is_rejected_without_partial_update(self):
        cache = RemoteProxyCache(max_records=8, renderer_budget=4)
        original = summary()
        cache.apply(original)
        conflicting = summary(position=(9.0, 0.0, 0.0))
        self.assertEqual(conflicting.identity_key, original.identity_key)
        before = cache.summaries
        with self.assertRaises(SummaryConflictError):
            cache.apply(conflicting)
        self.assertEqual(cache.summaries, before)
        self.assertEqual(cache.renderer_count, 1)

    def test_revisions_update_atomically_and_older_revision_is_stale(self):
        cache = RemoteProxyCache(max_records=8, renderer_budget=4)
        rev2 = summary(revision=2, position=(2.0, 0.0, 0.0))
        rev3 = summary(revision=3, position=(3.0, 0.0, 0.0))
        rev1 = summary(revision=1, position=(1.0, 0.0, 0.0))
        self.assertEqual(cache.apply(rev2), "accepted")
        self.assertEqual(cache.apply(rev3), "updated")
        self.assertEqual(cache.apply(rev1), "stale_revision")
        self.assertEqual(cache.summaries[0].revision, 3)
        self.assertEqual(cache.summaries[0].position, (3.0, 0.0, 0.0))

    def test_new_generation_replaces_old_and_late_old_generation_cannot_resurrect(self):
        cache = RemoteProxyCache(max_records=8, renderer_budget=3)
        old = summary(generation=4, attach_sequence=11, event_id="evt-old")
        new = summary(generation=5, attach_sequence=12, event_id="evt-new")
        self.assertEqual(cache.apply(old), "accepted")
        self.assertEqual(cache.apply(new), "accepted")
        self.assertEqual(cache.tracked_count, 1)
        self.assertEqual(cache.summaries[0].generation, 5)
        self.assertEqual(cache.apply(old), "stale_generation")
        self.assertEqual(cache.tracked_count, 1)

    def test_tombstone_removal_and_replacement_sequence(self):
        cache = RemoteProxyCache(max_records=8, renderer_budget=3)
        active = summary()
        removed = summary(revision=1, removed=True)
        resurrect = summary(revision=2, removed=False, position=(5.0, 0.0, 0.0))
        self.assertEqual(cache.apply(active), "accepted")
        self.assertEqual(cache.apply(removed), "updated")
        self.assertEqual(cache.apply(removed), "duplicate")
        self.assertEqual(cache.renderer_count, 0)
        self.assertEqual(cache.apply(resurrect), "removed_tombstone")
        # A re-attachment must carry a new upstream sequence/identity.
        reattached = summary(attach_sequence=8, event_id="evt-8", revision=0)
        self.assertEqual(cache.apply(reattached), "accepted")
        self.assertEqual(cache.renderer_count, 1)

    def test_context_clear_prevents_old_packet_revival_but_new_round_is_allowed(self):
        cache = RemoteProxyCache(max_records=8, renderer_budget=4)
        old = summary()
        cache.apply(old)
        self.assertEqual(cache.clear_context("round-A"), 1)
        self.assertEqual(cache.apply(old), "cleared_context")
        self.assertEqual(cache.tracked_count, 0)
        new_round = summary(context="round-B", event_id="evt-new-round")
        self.assertEqual(cache.apply(new_round), "accepted")
        with self.assertRaises(SummaryError):
            cache.rebuild_context("round-A", ())

    def test_renderer_budget_is_deterministic_and_never_changes_summaries(self):
        events = (
            summary(instance="cup", attach_sequence=1, event_id="cup", priority=4),
            summary(instance="book", attach_sequence=2, event_id="book", priority=8),
            summary(instance="pin", attach_sequence=3, event_id="pin", priority=8),
            summary(instance="hidden", attach_sequence=4, event_id="hidden", priority=100, visible=False),
            summary(instance="buried", attach_sequence=5, event_id="buried", priority=90, buried=True),
        )
        first = RemoteProxyCache(max_records=8, renderer_budget=2)
        second = RemoteProxyCache(max_records=8, renderer_budget=2)
        for event in events:
            first.apply(event)
        for event in reversed(events):
            second.apply(event)
        expected = tuple(item.identity_key for item in first.rendered_summaries)
        self.assertEqual(expected, tuple(item.identity_key for item in second.rendered_summaries))
        self.assertEqual(len(expected), 2)
        self.assertEqual(first.renderer_count, 2)
        self.assertEqual(first.merged_buried_count, 1)
        self.assertEqual(first.tracked_count, len(events))
        self.assertEqual(first.summaries, second.summaries)
        self.assertEqual(expected, tuple(item.identity_key for item in sorted(
            (events[1], events[2]), key=lambda item: item.identity_key)))
        self.assertFalse(hasattr(first, "ledger"))
        self.assertFalse(hasattr(first, "colliders"))

    def test_zero_renderer_budget_keeps_confirmed_summary_without_rendering(self):
        cache = RemoteProxyCache(max_records=3, renderer_budget=0)
        cache.apply(summary())
        self.assertEqual(cache.tracked_count, 1)
        self.assertEqual(cache.renderer_count, 0)
        self.assertEqual(len(cache.summaries), 1)

    def test_invalid_summary_is_rejected_without_mutating_cache(self):
        cache = RemoteProxyCache(max_records=4, renderer_budget=2)
        valid = summary()
        cache.apply(valid)
        before = cache.summaries
        invalids = (
            replace(valid, position=(float("nan"), 0.0, 0.0)),
            replace(valid, rotation=(0.0, 0.0, 0.0, 0.0)),
            replace(valid, generation=-1),
            replace(valid, visible=1),
            replace(valid, summary_digest=""),
        )
        for invalid in invalids:
            with self.subTest(invalid=invalid), self.assertRaises(SummaryError):
                cache.apply(invalid)
            self.assertEqual(cache.summaries, before)

    def test_capacity_failure_is_atomic(self):
        cache = RemoteProxyCache(max_records=1, renderer_budget=1)
        only = summary()
        cache.apply(only)
        before = cache.summaries
        with self.assertRaises(ProxyCapacityError):
            cache.apply(summary(instance="book", attach_sequence=9, event_id="evt-book"))
        self.assertEqual(cache.summaries, before)
        self.assertEqual(cache.renderer_count, 1)

    def test_rebuild_snapshot_is_order_independent_and_atomic(self):
        entries = (
            summary(instance="cup", generation=1, attach_sequence=1, event_id="cup-old"),
            summary(instance="cup", generation=2, attach_sequence=2, event_id="cup-new", priority=8),
            summary(instance="book", generation=1, attach_sequence=3, event_id="book", priority=4),
        )
        first = RemoteProxyCache(max_records=8, renderer_budget=2)
        second = RemoteProxyCache(max_records=8, renderer_budget=2)
        first.rebuild_context("round-A", entries)
        second.rebuild_context("round-A", tuple(reversed(entries)))
        self.assertEqual(first.summaries, second.summaries)
        self.assertEqual(tuple(item.identity_key for item in first.rendered_summaries),
                         tuple(item.identity_key for item in second.rendered_summaries))
        self.assertEqual(first.tracked_count, 2)

        before = first.summaries
        conflict = summary(instance="book", generation=1, attach_sequence=3,
                           event_id="book", position=(99.0, 0.0, 0.0))
        with self.assertRaises(SummaryConflictError):
            first.rebuild_context("round-A", entries + (conflict,))
        self.assertEqual(first.summaries, before)

    def test_rebuild_preserves_other_context_and_revision_tombstone(self):
        cache = RemoteProxyCache(max_records=8, renderer_budget=5)
        other = summary(context="round-B", instance="other", event_id="other")
        cache.apply(other)
        a0 = summary(revision=0)
        a1 = summary(revision=1, removed=True)
        cache.rebuild_context("round-A", (a0, a1))
        self.assertEqual(cache.tracked_count, 2)
        self.assertEqual(cache.apply(summary(revision=2)), "removed_tombstone")
        self.assertIn(other, cache.summaries)
        self.assertEqual(cache.renderer_count, 1)

    def test_closed_context_tombstones_are_bounded_and_clear_is_atomic_at_capacity(self):
        cache = RemoteProxyCache(max_records=1, renderer_budget=1)
        first = summary(context="round-one", event_id="one")
        cache.apply(first)
        self.assertEqual(cache.clear_context("round-one"), 1)
        second = summary(context="round-two", event_id="two")
        cache.apply(second)
        before = cache.summaries
        with self.assertRaises(ProxyCapacityError):
            cache.clear_context("round-two")
        self.assertEqual(cache.summaries, before)
        self.assertEqual(cache.apply(second), "duplicate")
        self.assertEqual(cache.apply(first), "cleared_context")

    def test_same_sequence_different_event_id_is_a_conflict(self):
        cache = RemoteProxyCache(max_records=4, renderer_budget=2)
        cache.apply(summary())
        with self.assertRaises(SummaryConflictError):
            cache.apply(summary(event_id="different-event"))
        self.assertEqual(cache.tracked_count, 1)


if __name__ == "__main__":
    unittest.main()
