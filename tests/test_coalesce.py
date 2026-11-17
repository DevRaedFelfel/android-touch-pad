import asyncio

from agent.inject_win import DryRunInjector
from agent.server import MAX_STEP, InputController


def test_consecutive_moves_merge_into_one_op():
    ctrl = InputController(DryRunInjector())
    for dx, dy in [(1, 2), (3, -4), (5, 6)]:
        ctrl.move(dx, dy)
    ops = ctrl.take_ops()
    assert ops == [["move", 9, 4]]
    assert ctrl.packets == 3


def test_apply_clamps_runaway_movement():
    inj = DryRunInjector()
    ctrl = InputController(inj)
    ctrl.apply(["move", 10**6, -(10**6)])
    assert (inj.total_dx, inj.total_dy) == (MAX_STEP, -MAX_STEP)


def test_zero_movement_is_not_injected():
    inj = DryRunInjector()
    ctrl = InputController(inj)
    ctrl.move(5, 5)
    ctrl.move(-5, -5)
    ctrl.apply(ctrl.take_ops()[0])
    assert inj.move_calls == 0


def test_injector_task_batches_a_burst_into_few_calls():
    async def scenario():
        inj = DryRunInjector()
        ctrl = InputController(inj)
        task = asyncio.create_task(ctrl.run())
        for _ in range(500):  # a burst with no await in between
            ctrl.move(1, 1)
        await asyncio.sleep(0.05)
        task.cancel()
        return inj, ctrl

    inj, ctrl = asyncio.run(scenario())
    assert (inj.total_dx, inj.total_dy) == (500, 500)
    assert inj.move_calls == 1
    assert ctrl.packets == 500


def test_buttons_and_keys_are_barriers_that_keep_order():
    ctrl = InputController(DryRunInjector())
    ctrl.move(1, 1)
    ctrl.move(2, 2)
    ctrl.button(0, 1)
    ctrl.move(3, 3)
    ctrl.key(0x41, 1)
    ctrl.key(0x41, 0)
    ctrl.button(0, 0)
    assert ctrl.take_ops() == [
        ["move", 3, 3],
        ["button", 0, 1],
        ["move", 3, 3],
        ["key", 0x41, 1],
        ["key", 0x41, 0],
        ["button", 0, 0],
    ]


def test_scroll_and_zoom_merge_with_their_own_kind_only():
    ctrl = InputController(DryRunInjector())
    ctrl.scroll(0, 10)
    ctrl.scroll(0, 20)
    ctrl.move(1, 1)
    ctrl.scroll(5, 5)
    ctrl.zoom(100)
    ctrl.zoom(-30)
    assert ctrl.take_ops() == [["scroll", 0, 30], ["move", 1, 1], ["scroll", 5, 5], ["zoom", 70, 0]]


def test_apply_routes_every_op_to_the_injector():
    inj = DryRunInjector()
    ctrl = InputController(inj)
    for op in (["move", 4, 5], ["scroll", 0, 120], ["zoom", 240, 0], ["button", 2, 1], ["key", 0x11, 1]):
        ctrl.apply(op)
    assert list(inj.events) == [
        ("move", 4, 5),
        ("scroll", 0, 120),
        ("zoom", 240),
        ("button", 2, True),
        ("key", 0x11, True),
    ]


def test_wheel_values_are_clamped():
    from agent.server import MAX_WHEEL

    inj = DryRunInjector()
    ctrl = InputController(inj)
    ctrl.apply(["scroll", 0, 10**6])
    ctrl.apply(["zoom", -(10**6), 0])
    assert inj.scroll_dy == MAX_WHEEL and inj.zoom_total == -MAX_WHEEL


def test_release_all_drops_queue_and_releases_held_input():
    inj = DryRunInjector()
    ctrl = InputController(inj)
    ctrl.apply(["button", 0, 1])
    ctrl.apply(["key", 0x5B, 1])
    ctrl.move(9, 9)
    ctrl.release_all()
    assert ctrl.take_ops() == []
    assert inj.buttons_down == set() and inj.keys_down == set()
    assert list(inj.events)[-2:] == [("button", 0, False), ("key", 0x5B, False)]


def test_text_is_an_ordered_barrier_and_reaches_the_injector():
    inj = DryRunInjector()
    ctrl = InputController(inj)
    ctrl.move(1, 1)
    ctrl.text("hi")
    ctrl.move(2, 2)
    assert ctrl.take_ops() == [["move", 1, 1], ["text", "hi"], ["move", 2, 2]]
    ctrl.apply(["text", "hi"])
    assert list(inj.events) == [("text", "hi")]
