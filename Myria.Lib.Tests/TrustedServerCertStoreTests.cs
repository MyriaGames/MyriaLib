using System.Net.Security;
using Myria.Lib.Core.Utils;
using Xunit;

namespace Myria.Lib.Tests;

/// <summary>
/// Security review 2026-11-26, A04 F1: trust-on-first-use is only a fallback for self-signed servers - never for
/// a host with a publicly trusted certificate, and never for a certificate issued for another name.
/// </summary>
public class TrustedServerCertStoreTests : IDisposable
{
    private const SslPolicyErrors Untrusted = SslPolicyErrors.RemoteCertificateChainErrors;
    private static readonly string[] Official = ["myria.myria-games.duckdns.org"];
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "myria-tofu-" + Guid.NewGuid().ToString("N"));

    public TrustedServerCertStoreTests()
    {
        TrustedServerCertStore.PathFile = Path.Combine(_dir, "trusted_certs.json");
        TrustedServerCertStore.ResetCache();
    }

    public void Dispose()
    {
        TrustedServerCertStore.ResetCache();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
    }

    private static bool Check(string host, string thumb, SslPolicyErrors errors) =>
        TrustedServerCertStore.IsAcceptable(host + ":443", host, thumb, errors, Official);

    [Fact]
    public void OfficialHost_NeverAcceptsAnUntrustedCertificate_EvenOnFirstStart() =>
        Assert.False(Check("myria.myria-games.duckdns.org", "AA", Untrusted));

    [Fact]
    public void OfficialHost_IsMatchedCaseInsensitively() =>
        Assert.False(Check("MYRIA.Myria-Games.DuckDNS.org", "AA", Untrusted));

    [Fact]
    public void HostThatOnceHadACaCertificate_RefusesASelfSignedOneLater()
    {
        Assert.True(Check("realm.example", "CA", SslPolicyErrors.None));
        Assert.False(Check("realm.example", "EVIL", Untrusted));
    }

    [Fact]
    public void CaMark_SurvivesARestart()
    {
        Assert.True(Check("realm.example", "CA", SslPolicyErrors.None));
        TrustedServerCertStore.ResetCache();                        // fresh process: read from disk
        Assert.False(Check("realm.example", "EVIL", Untrusted));
    }

    [Fact]
    public void SelfHostedServer_StillWorksWithTofu_AndDetectsAChangedCertificate()
    {
        Assert.True(Check("home.example", "AA", Untrusted));        // first use: trusted and pinned
        Assert.True(Check("home.example", "aa", Untrusted));        // same certificate again
        Assert.False(Check("home.example", "BB", Untrusted));       // changed: refused
    }

    [Theory]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch)]
    [InlineData(SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateChainErrors)]
    [InlineData(SslPolicyErrors.RemoteCertificateNotAvailable)]
    public void NameMismatchOrNoCertificate_IsNeverAccepted(SslPolicyErrors errors) =>
        Assert.False(Check("unknown.example", "AA", errors));

    [Fact]
    public void MissingThumbprint_IsRefused() =>
        Assert.False(TrustedServerCertStore.IsAcceptable("x.example:443", "x.example", null, Untrusted, Official));

    [Fact]
    public void LegacyTrustOnFirstUse_RefusesCaMarkedHosts()
    {
        TrustedServerCertStore.RememberCaValidated("realm.example:443");
        Assert.False(TrustedServerCertStore.TrustOnFirstUse("realm.example:443", "EVIL"));
    }
}
