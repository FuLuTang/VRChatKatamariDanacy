import unittest

from Docs.Design.ControlIntent.intent import (
    InputFrame, IntentAdapter, IntentKind, adapt_source, prompt_visible,
)


class IntentTests(unittest.TestCase):
    def test_three_sources_same_script_produce_identical_intent_sequence(self):
        script = (
            InputFrame(0.0),
            InputFrame(0.1, move=(0.2, 1.4), look=(-0.5, 0.0)),
            InputFrame(0.2, jump=True),
            InputFrame(0.3, jump=True),  # held: no repeated edge
            InputFrame(0.4, jump=False),
            InputFrame(0.5, jump=True),  # within debounce window
            InputFrame(0.7, jump=False),
            InputFrame(0.8, jump=True),
            InputFrame(1.0, interact=True, comfort_turn=True),
        )
        outputs = [adapt_source(source, script) for source in ("A", "B", "Desktop")]
        self.assertEqual(outputs[0], outputs[1])
        self.assertEqual(outputs[1], outputs[2])
        self.assertEqual(outputs[0][0].kind, IntentKind.MOVE2D)
        self.assertEqual((outputs[0][0].x, outputs[0][0].y), (0.2, 1.0))

    def test_rising_edge_once_held_button_not_repeated(self):
        adapter = IntentAdapter(debounce_seconds=0.2)
        results = [adapter.process(InputFrame(t, jump=down)) for t, down in
                   ((0.0, True), (0.05, True), (0.1, True), (0.15, False))]
        emitted = [i for batch in results for i in batch]
        self.assertEqual([i.kind for i in emitted], [IntentKind.USE_ITEM_OR_JUMP])

    def test_debounce_suppresses_quick_repress_and_allows_later_press(self):
        adapter = IntentAdapter(debounce_seconds=0.2)
        script = ((0.0, True), (0.01, False), (0.1, True), (0.11, False),
                  (0.21, True))
        count = sum(1 for t, down in script for i in adapter.process(InputFrame(t, jump=down))
                    if i.kind == IntentKind.USE_ITEM_OR_JUMP)
        self.assertEqual(count, 2)

    def test_seated_jump_routes_only_to_item_intent_and_prompt_hides(self):
        adapter = IntentAdapter()
        output = adapter.process(InputFrame(0.0, jump=True), seated=True)
        self.assertEqual([intent.kind for intent in output], [IntentKind.USE_ITEM_OR_JUMP])
        self.assertNotIn("Jump", [intent.kind.value for intent in output])
        self.assertFalse(prompt_visible(seated=True))
        self.assertTrue(prompt_visible(seated=False))

    def test_menu_suppresses_every_game_intent_and_clears_edge(self):
        adapter = IntentAdapter()
        output = adapter.process(InputFrame(0.0, move=(1, 0), look=(0, 1), jump=True,
                                            interact=True, exit_station=True,
                                            respawn=True, comfort_turn=True,
                                            menu_open=True))
        self.assertEqual(output, ())
        # Still-held input on menu close does not create a synthetic action edge.
        closed = adapter.process(InputFrame(1.0, jump=True, interact=True))
        self.assertEqual([i.kind for i in closed], [])
        self.assertFalse(prompt_visible(seated=False, menu_open=True))

    def test_bounds_and_invalid_axis_shape(self):
        adapter = IntentAdapter()
        with self.assertRaises(ValueError):
            adapter.process(InputFrame(0.0, move=(1.0,)))
        with self.assertRaises(ValueError):
            adapter.process(InputFrame(-0.1))


if __name__ == "__main__":
    unittest.main()
