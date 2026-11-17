"""Pairing tokens and failed-login rate limiting."""

from __future__ import annotations

import hashlib
import hmac
import secrets
import time
from collections import defaultdict, deque
from pathlib import Path
from typing import Callable

from .config import load_json, save_json


def _hash(token: str) -> str:
    return hashlib.sha256(token.encode("utf-8")).hexdigest()


class TokenStore:
    """Stores only SHA-256 hashes of pairing tokens, never the tokens themselves."""

    def __init__(self, path: Path):
        self.path = path
        self._hashes: list[str] = list(load_json(path).get("tokens", []))

    def has_tokens(self) -> bool:
        return bool(self._hashes)

    def create(self) -> str:
        token = secrets.token_urlsafe(16)  # 128 bits
        self._hashes.append(_hash(token))
        self._save()
        return token

    def verify(self, token: object) -> bool:
        if not isinstance(token, str) or not token:
            return False
        h = _hash(token)
        ok = False
        for stored in self._hashes:  # no early exit: constant-time over the list
            ok |= hmac.compare_digest(h, stored)
        return ok

    def revoke_all(self) -> None:
        self._hashes = []
        self._save()

    def _save(self) -> None:
        save_json(self.path, {"tokens": self._hashes})


class RateLimiter:
    """Allows at most `max_failures` failed logins per `window` seconds per IP."""

    def __init__(
        self,
        max_failures: int = 5,
        window: float = 60.0,
        clock: Callable[[], float] = time.monotonic,
    ):
        self.max_failures = max_failures
        self.window = window
        self._clock = clock
        self._failures: dict[str, deque[float]] = defaultdict(deque)

    def _prune(self, ip: str) -> deque[float]:
        q = self._failures[ip]
        cutoff = self._clock() - self.window
        while q and q[0] <= cutoff:
            q.popleft()
        return q

    def allow(self, ip: str) -> bool:
        return len(self._prune(ip)) < self.max_failures

    def record_failure(self, ip: str) -> None:
        self._prune(ip).append(self._clock())
