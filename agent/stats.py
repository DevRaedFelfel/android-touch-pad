"""Small statistics helpers shared by the server probe and tools/latency_test.py."""

from __future__ import annotations

import math


def percentile(values: list[float], p: float) -> float:
    """Nearest-rank percentile (p in 0..100). Returns 0.0 for an empty list."""
    if not values:
        return 0.0
    ordered = sorted(values)
    rank = max(1, math.ceil(p / 100 * len(ordered)))
    return ordered[min(rank, len(ordered)) - 1]
