import json

from agent.auth import RateLimiter, TokenStore


def test_token_create_verify_revoke(tmp_path):
    path = tmp_path / "config.json"
    store = TokenStore(path)
    assert not store.has_tokens()
    token = store.create()
    assert store.verify(token)
    assert not store.verify("wrong")
    assert not store.verify("")
    assert not store.verify(None)
    assert not store.verify(123)
    store.revoke_all()
    assert not store.verify(token)


def test_only_hashes_are_persisted(tmp_path):
    path = tmp_path / "config.json"
    token = TokenStore(path).create()
    raw = path.read_text()
    assert token not in raw
    assert len(json.loads(raw)["tokens"][0]) == 64  # sha256 hex
    assert TokenStore(path).verify(token)  # survives a restart


def test_multiple_phones_can_be_paired(tmp_path):
    store = TokenStore(tmp_path / "c.json")
    a, b = store.create(), store.create()
    assert store.verify(a) and store.verify(b)


def test_rate_limiter_blocks_then_recovers():
    now = [0.0]
    rl = RateLimiter(max_failures=5, window=60, clock=lambda: now[0])
    for _ in range(5):
        assert rl.allow("1.2.3.4")
        rl.record_failure("1.2.3.4")
    assert not rl.allow("1.2.3.4")
    assert rl.allow("5.6.7.8")  # per-IP
    now[0] = 61
    assert rl.allow("1.2.3.4")
