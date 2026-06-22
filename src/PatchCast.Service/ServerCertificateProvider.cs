using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PatchCast.Service;

public sealed class ServerCertificateProvider(ILogger<ServerCertificateProvider> logger)
{
    private const string CertificateSubject = "CN=PatchCast Audio Service";
    private readonly object syncRoot = new();
    private X509Certificate2? cachedCertificate;

    public X509Certificate2 GetCertificate()
    {
        lock (syncRoot)
            return cachedCertificate ??= FindOrCreateCertificate();
    }

    private X509Certificate2 FindOrCreateCertificate()
    {
        using var store = new X509Store(StoreName.My, StoreLocation.CurrentUser);
        store.Open(OpenFlags.ReadWrite);

        var existing = store.Certificates
            .Find(X509FindType.FindBySubjectDistinguishedName, CertificateSubject, validOnly: false)
            .OfType<X509Certificate2>()
            .Where(certificate => certificate.HasPrivateKey && certificate.NotAfter > DateTime.UtcNow.AddDays(30))
            .OrderByDescending(certificate => certificate.NotAfter)
            .FirstOrDefault();
        if (existing is not null)
        {
            logger.LogInformation("Using TLS certificate {Thumbprint}.", existing.GetCertHashString(HashAlgorithmName.SHA256));
            return existing;
        }

        using var rsa = RSA.Create(3072);
        var request = new CertificateRequest(
            CertificateSubject,
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        // serverAuth EKU and a Subject Alternative Name are both required for
        // browsers to accept the certificate for the HTTPS/WSS endpoint, even when
        // it is manually trusted. The TCP client validates by SHA-256 pin, so these
        // additions are compatible with both transports.
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        var subjectAlternativeName = new SubjectAlternativeNameBuilder();
        subjectAlternativeName.AddDnsName("localhost");
        subjectAlternativeName.AddIpAddress(IPAddress.Loopback);
        subjectAlternativeName.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(subjectAlternativeName.Build());

        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        var persisted = X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pfx),
            (string?)null,
            X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.UserKeySet);
        persisted.FriendlyName = "PatchCast automatic TLS certificate";
        store.Add(persisted);
        logger.LogInformation("Generated TLS certificate {Thumbprint}.", persisted.GetCertHashString(HashAlgorithmName.SHA256));
        return persisted;
    }
}
