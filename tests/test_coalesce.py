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
