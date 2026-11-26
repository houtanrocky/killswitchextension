using System.Security.Cryptography.X509Certificates;
using KillSwitch.Core;

namespace KillSwitch.Companion.Tests;

[TestClass]
public sealed class TlsCertificateAuthorityTests
{
	[TestMethod]
	public void TrustedRootCertificateCanBeInstalledWithoutCopyingTheSigningKey()
	{
		using var authority = TlsCertificateAuthority.Create("Test Kill Switch CA");
		Assert.IsTrue(authority.Certificate.HasPrivateKey, "The in-process authority needs its private key to sign leaf certificates.");
		using var trustedRoot = X509CertificateLoader.LoadCertificate(authority.Certificate.RawData);
		Assert.IsFalse(trustedRoot.HasPrivateKey, "Clients only need the public root certificate to validate intercepted TLS.");
		Assert.AreEqual(authority.Certificate.Thumbprint, trustedRoot.Thumbprint);
	}
}
