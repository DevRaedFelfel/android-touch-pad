"""Optional HTTPS (--https): a self-signed certificate for the laptop's LAN addresses.

HTTPS lets the phone use the Screen Wake Lock API and encrypts traffic on the
network. Because the certificate is self-signed, the phone shows a warning once;
choose "Advanced" and then "Proceed".
"""

from __future__ import annotations

import datetime as dt
import ipaddress
import os
import ssl
from pathlib import Path

CERT_NAME = "server.crt"
KEY_NAME = "server.key"
VALID_DAYS = 365
RENEW_BEFORE_DAYS = 30


def _san_ips(cert) -> set[str]:
    from cryptography import x509

    try:
        san = cert.extensions.get_extension_for_class(x509.SubjectAlternativeName).value
    except x509.ExtensionNotFound:
        return set()
    return {str(ip) for ip in san.get_values_for_type(x509.IPAddress)}


def _cert_is_usable(cert_path: Path, wanted_ips: set[str]) -> bool:
    from cryptography import x509

    try:
        cert = x509.load_pem_x509_certificate(cert_path.read_bytes())
    except (OSError, ValueError):
        return False
    expires = getattr(cert, "not_valid_after_utc", None) or cert.not_valid_after.replace(
        tzinfo=dt.timezone.utc
    )
    soon = dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=RENEW_BEFORE_DAYS)
    return expires > soon and wanted_ips <= _san_ips(cert)


def ensure_cert(directory: Path, ips: list[str]) -> tuple[Path, Path]:
    """Return (cert, key) paths, generating or renewing them when needed."""
    from cryptography import x509
    from cryptography.hazmat.primitives import hashes, serialization
    from cryptography.hazmat.primitives.asymmetric import ec
    from cryptography.x509.oid import ExtendedKeyUsageOID, NameOID

    cert_path = directory / CERT_NAME
    key_path = directory / KEY_NAME
    wanted = {str(ipaddress.ip_address(ip)) for ip in ips} | {"127.0.0.1"}
    if cert_path.exists() and key_path.exists() and _cert_is_usable(cert_path, wanted):
        return cert_path, key_path

    key = ec.generate_private_key(ec.SECP256R1())
    name = x509.Name([x509.NameAttribute(NameOID.COMMON_NAME, "Phone Touchpad")])
    now = dt.datetime.now(dt.timezone.utc)
    san = [x509.DNSName("localhost")] + [x509.IPAddress(ipaddress.ip_address(ip)) for ip in sorted(wanted)]
    cert = (
        x509.CertificateBuilder()
        .subject_name(name)
        .issuer_name(name)
        .public_key(key.public_key())
        .serial_number(x509.random_serial_number())
        .not_valid_before(now - dt.timedelta(days=1))
        .not_valid_after(now + dt.timedelta(days=VALID_DAYS))
        .add_extension(x509.SubjectAlternativeName(san), critical=False)
        .add_extension(x509.BasicConstraints(ca=False, path_length=None), critical=True)
        .add_extension(x509.ExtendedKeyUsage([ExtendedKeyUsageOID.SERVER_AUTH]), critical=False)
        .sign(key, hashes.SHA256())
    )
    directory.mkdir(parents=True, exist_ok=True)
    key_path.write_bytes(
        key.private_bytes(
            serialization.Encoding.PEM,
            serialization.PrivateFormat.PKCS8,
            serialization.NoEncryption(),
        )
    )
    if os.name != "nt":
        os.chmod(key_path, 0o600)
    cert_path.write_bytes(cert.public_bytes(serialization.Encoding.PEM))
    return cert_path, key_path


def make_ssl_context(cert_path: Path, key_path: Path) -> ssl.SSLContext:
    ctx = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    ctx.minimum_version = ssl.TLSVersion.TLSv1_2
    ctx.load_cert_chain(cert_path, key_path)
    return ctx
