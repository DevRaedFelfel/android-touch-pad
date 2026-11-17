import pytest

from agent.netinfo import is_allowed_ip


@pytest.mark.parametrize(
    "ip",
    [
        "192.168.1.20",
        "10.0.0.5",
        "172.16.0.1",
        "172.31.255.254",
        "169.254.10.10",
        "127.0.0.1",
        "::1",
        "fe80::1",
        "fd12:3456::1",
        "::ffff:192.168.0.9",
        "fe80::1%wlan0",
    ],
)
def test_private_addresses_allowed(ip):
    assert is_allowed_ip(ip)


@pytest.mark.parametrize(
    "ip",
    ["8.8.8.8", "1.1.1.1", "172.32.0.1", "11.0.0.1", "2001:4860:4860::8888", "::ffff:8.8.8.8", "", None, "nonsense"],
)
def test_public_or_invalid_addresses_rejected(ip):
    assert not is_allowed_ip(ip)
