// Optional HTTPS (--https): a self-signed certificate for the laptop's LAN addresses (agent/tls.py).
// HTTPS lets the phone use the Screen Wake Lock API and encrypts traffic on the network. Because the
// certificate is self-signed, the phone shows a warning once: choose "Advanced", then "Proceed".
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace TouchpadAgent.Core;

public static class Tls
{
    public const string PfxName = "server-cs.pfx";
    public const string IpsName = "server-cs.ips"; // the addresses the certificate covers, one per line
    private const int ValidDays = 365, RenewBeforeDays = 30;

    public static X509Certificate2 EnsureCert(string directory, IEnumerable<string> ips)
    {
        var wanted = new SortedSet<string>(ips.Select(i => IPAddress.Parse(i).ToString())) { "127.0.0.1" };
        string pfx = Path.Combine(directory, PfxName), ipsFile = Path.Combine(directory, IpsName);

        if (File.Exists(pfx) && File.Exists(ipsFile))
        {
            try
            {
                var existing = new X509Certificate2(File.ReadAllBytes(pfx));
                var covered = File.ReadAllLines(ipsFile).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet();
                if (existing.NotAfter.ToUniversalTime() > DateTime.UtcNow.AddDays(RenewBeforeDays) && wanted.IsSubsetOf(covered))
                    return existing;
                existing.Dispose();
            }
            catch (Exception e) when (e is CryptographicException or IOException) { } // unreadable: regenerate
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest("CN=Phone Touchpad", key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        foreach (var ip in wanted) san.AddIpAddress(IPAddress.Parse(ip));
        req.CertificateExtensions.Add(san.Build());
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(ValidDays));

        Directory.CreateDirectory(directory);
        byte[] pfxBytes = cert.Export(X509ContentType.Pfx);
        File.WriteAllBytes(pfx, pfxBytes);
        File.WriteAllLines(ipsFile, wanted);
        // Re-import from bytes: a certificate made in memory has an ephemeral key that Windows' TLS stack cannot use.
        return new X509Certificate2(pfxBytes);
    }
}
