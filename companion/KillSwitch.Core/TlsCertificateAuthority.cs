using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace KillSwitch.Core;

public sealed class TlsCertificateAuthority(X509Certificate2 certificate) : IDisposable
{
	public X509Certificate2 Certificate { get; } = certificate;

	public static TlsCertificateAuthority Create(string subjectName = "Kill Switch Local Inspection")
	{
		var key = RSA.Create(3072);
		var request = new CertificateRequest($"CN={subjectName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
		request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
		request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
		var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(10));
		return new TlsCertificateAuthority(certificate);
	}

	public X509Certificate2 CreateServerCertificate(string host)
	{
		if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("A host name is required.", nameof(host));
		using var key = RSA.Create(2048);
		var request = new CertificateRequest($"CN={host}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
		request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
		request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
		request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
		var san = new SubjectAlternativeNameBuilder();
		if (IPAddress.TryParse(host, out var address)) san.AddIpAddress(address);
		else san.AddDnsName(host);
		request.CertificateExtensions.Add(san.Build());
		var publicCertificate = request.Create(Certificate, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(7), RandomNumberGenerator.GetBytes(16));
		using var certificateWithKey = publicCertificate.CopyWithPrivateKey(key);
		var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
		var pfx = certificateWithKey.Export(X509ContentType.Pfx, password);
		return X509CertificateLoader.LoadPkcs12(pfx, password, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
	}

	public void Dispose() => Certificate.Dispose();
}
