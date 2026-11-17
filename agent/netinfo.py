"""Network helpers: which remote addresses are allowed, and which LAN IP to show."""

from __future__ import annotations

import ipaddress
import socket

# Only local-network peers may talk to the agent. Loopback is allowed so the
# agent can be tested from the same machine.
_ALLOWED = [
    ipaddress.ip_network(n)
    for n in (
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "169.254.0.0/16",
        "127.0.0.0/8",
        "::1/128",
        "fe80::/10",
        "fc00::/7",
    )
]


def is_allowed_ip(ip: str | None) -> bool:
    if not ip:
        return False
    try:
        addr = ipaddress.ip_address(ip.split("%", 1)[0])  # drop IPv6 zone id
    except ValueError:
        return False
    if isinstance(addr, ipaddress.IPv6Address) and addr.ipv4_mapped:
        addr = addr.ipv4_mapped
    return any(addr in net for net in _ALLOWED)


def _is_lan(ip: str) -> bool:
    return is_allowed_ip(ip) and not ip.startswith("127.")


def private_ips() -> list[str]:
    """Private IPv4 addresses of this machine (best effort)."""
    found: list[str] = []
    try:
        for info in socket.getaddrinfo(socket.gethostname(), None, socket.AF_INET):
            ip = info[4][0]
            if _is_lan(ip) and ip not in found:
                found.append(ip)
    except OSError:
        pass
    return found


def lan_ip() -> str | None:
    """The IP the OS would use to reach the local network, or None."""
    try:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as s:
            s.connect(("8.8.8.8", 80))  # UDP connect: selects a route, sends nothing
            ip = s.getsockname()[0]
        if _is_lan(ip):
            return ip
    except OSError:
        pass
    ips = private_ips()
    return ips[0] if ips else None
