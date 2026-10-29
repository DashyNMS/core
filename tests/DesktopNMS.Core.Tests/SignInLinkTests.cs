using DesktopNMS.Core.Api;
using DesktopNMS.Core.SignIn;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>The link desktop shows as a QR code for DashyNMS Mobile to sign in with (desktop#263).</summary>
public sealed class SignInLinkTests
{
    private const string SanctumToken = "12|AbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcd";
    private const string LegacyToken = "0123456789abcdef0123456789ABCDEF";
    private static readonly string Fingerprint = string.Join(':', Enumerable.Range(0, 32).Select(i => i.ToString("X2")));
    private static readonly string FingerprintHex = Fingerprint.Replace(":", string.Empty);

    private static SignInLink Parse(string text)
    {
        Assert.True(SignInLink.TryParse(text, out var link, out var error), error);
        return link!;
    }

    private static string Rejection(string? text)
    {
        Assert.False(SignInLink.TryParse(text, out var link, out var error));
        Assert.Null(link);
        Assert.False(string.IsNullOrEmpty(error));
        return error!;
    }

    [Fact]
    public void Writes_a_versioned_link_with_the_token_escaped()
    {
        var link = SignInLink.Create(new Uri("https://nms.example.com/"), SanctumToken);

        Assert.Equal("dashynms://sign-in?v=1&server=https%3A%2F%2Fnms.example.com%2F&token=12%7CAbCdEfGhIjKlMnOpQrStUvWxYz0123456789abcd", link.ToString());
    }

    [Fact]
    public void Reads_back_everything_desktop_puts_in()
    {
        var connection = new LibreNmsConnection(
            new Uri("https://example.com:8443/librenms/"), SanctumToken, allowUntrustedCertificate: true,
            backupWebRoot: new Uri("http://192.0.2.20:8080/librenms/"), trustedCertificates: [Fingerprint.ToLowerInvariant()]);

        var read = Parse(SignInLink.Create(connection).ToString());

        Assert.Equal("https://example.com:8443/librenms/", read.Server.AbsoluteUri);
        Assert.Equal(SanctumToken, read.Token);
        Assert.Equal("http://192.0.2.20:8080/librenms/", read.Backup!.AbsoluteUri);
        Assert.Equal([Fingerprint], read.TrustedCertificates);
        Assert.True(read.AllowUntrustedCertificate);
    }

    [Fact]
    public void Leaves_out_fingerprints_the_connection_no_longer_uses()
    {
        var connection = new LibreNmsConnection(new Uri("https://nms.example.com/"), LegacyToken, allowUntrustedCertificate: false, trustedCertificates: [Fingerprint]);

        var link = SignInLink.Create(connection);

        Assert.Empty(link.TrustedCertificates);
        Assert.DoesNotContain("cert=", link.ToString());
        Assert.False(Parse(link.ToString()).AllowUntrustedCertificate);
    }

    [Fact]
    public void Reads_several_fingerprints_written_either_way()
    {
        var other = new string('a', 64);
        var read = Parse($"dashynms://sign-in?v=1&server=https%3A%2F%2Fnms.example.com%2F&token={LegacyToken}&cert={FingerprintHex}&cert={Uri.EscapeDataString(Fingerprint)}&cert={other}");

        Assert.Equal(2, read.TrustedCertificates.Count);
        Assert.Equal(Fingerprint, read.TrustedCertificates[0]);
        Assert.StartsWith("AA:AA:", read.TrustedCertificates[1]);
    }

    [Theory]
    [InlineData("DASHYNMS://SIGN-IN?v=1&server=https://nms.example.com&token=" + LegacyToken)]
    [InlineData("dashynms://sign-in/?v=1&server=https://nms.example.com/&token=" + LegacyToken + "#ignored")]
    [InlineData("  dashynms://sign-in?token=" + LegacyToken + "&server=https%3A%2F%2Fnms.example.com&v=1&later=extra  ")]
    public void Reads_links_written_slightly_differently(string text)
    {
        var read = Parse(text);

        Assert.Equal("https://nms.example.com/", read.Server.AbsoluteUri);
        Assert.Equal(LegacyToken, read.Token);
        Assert.Null(read.Backup);
    }

    [Fact]
    public void Tidies_a_server_address_given_as_the_API_url() =>
        Assert.Equal("https://nms.example.com/", Parse($"dashynms://sign-in?v=1&server=https://nms.example.com/api/v0&token={LegacyToken}").Server.AbsoluteUri);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://nms.example.com/")]
    [InlineData("dashynms://widget?v=1&server=https://nms.example.com/&token=" + LegacyToken)]
    [InlineData("dashynms://sign-in-now?v=1&server=https://nms.example.com/&token=" + LegacyToken)]
    [InlineData("dashynms://sign-in")]
    [InlineData("dashynms://sign-in?server=https://nms.example.com/&token=" + LegacyToken)]
    [InlineData("dashynms://sign-in?v=1&v=1&server=https://nms.example.com/&token=" + LegacyToken)]
    [InlineData("dashynms://sign-in?v=1&server=https://nms.example.com/&token=" + LegacyToken + "&token=" + LegacyToken)]
    [InlineData("dashynms://sign-in?v=1&server=https://nms.example.com/&token")]
    [InlineData("dashynms://sign-in?v=1&server=https://nms.example.com/&=x&token=" + LegacyToken)]
    public void Turns_down_anything_that_isnt_a_sign_in_link(string? text) =>
        Assert.Equal("That isn't a DashyNMS sign-in link.", Rejection(text));

    [Fact]
    public void Turns_down_a_link_too_long_to_be_genuine() =>
        Rejection($"dashynms://sign-in?v=1&server=https://nms.example.com/&token={LegacyToken}&pad={new string('x', 5000)}");

    [Theory]
    [InlineData("2")]
    [InlineData("0")]
    [InlineData("one")]
    public void Turns_down_another_version(string version) =>
        Assert.Contains("newer DashyNMS", Rejection($"dashynms://sign-in?v={version}&server=https://nms.example.com/&token={LegacyToken}"));

    [Theory]
    [InlineData("")]
    [InlineData("nms.example.com")]
    [InlineData("ftp://nms.example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@nms.example.com/")]
    [InlineData("https:///librenms")]
    public void Turns_down_an_unusable_server_address(string server) =>
        Assert.Contains("server address", Rejection($"dashynms://sign-in?v=1&server={Uri.EscapeDataString(server)}&token={LegacyToken}"));

    [Fact]
    public void Turns_down_a_missing_server_address() =>
        Assert.Contains("server address", Rejection($"dashynms://sign-in?v=1&token={LegacyToken}"));

    [Theory]
    [InlineData("")]
    [InlineData("not-a-token")]
    [InlineData("Bearer 0123456789abcdef0123456789ABCDEF")]
    [InlineData("12|short")]
    public void Turns_down_an_unusable_token(string token) =>
        Assert.Contains("API token", Rejection($"dashynms://sign-in?v=1&server=https://nms.example.com/&token={Uri.EscapeDataString(token)}"));

    [Fact]
    public void Turns_down_a_missing_token() =>
        Assert.Contains("API token", Rejection("dashynms://sign-in?v=1&server=https://nms.example.com/"));

    [Theory]
    [InlineData("")]
    [InlineData("backup.example.net")]
    [InlineData("file:///etc/passwd")]
    public void Turns_down_an_unusable_backup_address(string backup) =>
        Assert.Contains("backup address", Rejection($"dashynms://sign-in?v=1&server=https://nms.example.com/&token={LegacyToken}&backup={Uri.EscapeDataString(backup)}"));

    [Theory]
    [InlineData("")]
    [InlineData("AB:CD")]
    [InlineData("zz")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000FF")]
    public void Turns_down_an_unusable_fingerprint(string cert) =>
        Assert.Contains("fingerprint", Rejection($"dashynms://sign-in?v=1&server=https://nms.example.com/&token={LegacyToken}&cert={Uri.EscapeDataString(cert)}"));

    [Fact]
    public void Turns_down_bad_escaping() =>
        Rejection($"dashynms://sign-in?v=1&server=https://nms.example.com/&token={LegacyToken}&backup=%E0%A4%A");

    [Fact]
    public void Asks_by_the_servers_name()
    {
        var link = Parse($"dashynms://sign-in?v=1&server=https://example.com:8443/librenms/&token={LegacyToken}&backup=https://192.0.2.20/&cert={FingerprintHex}");

        Assert.Equal("Sign in to example.com:8443/librenms?", link.ConfirmationTitle);
        Assert.Contains("https://example.com:8443/librenms/", link.ConfirmationDetail);
        Assert.Contains("https://192.0.2.20/ as the backup address", link.ConfirmationDetail);
        Assert.Contains("trust the server certificate", link.ConfirmationDetail);
        Assert.Equal("Sign in to nms.example.com?", Parse($"dashynms://sign-in?v=1&server=https://nms.example.com/&token={LegacyToken}").ConfirmationTitle);
    }

    [Fact]
    public void Wont_create_a_link_it_couldnt_read()
    {
        Assert.Throws<ArgumentException>(() => SignInLink.Create(new Uri("ftp://nms.example.com/"), LegacyToken));
        Assert.Throws<ArgumentException>(() => SignInLink.Create(new Uri("https://nms.example.com/"), "nope"));
        Assert.Throws<ArgumentException>(() => SignInLink.Create(new Uri("https://nms.example.com/"), LegacyToken, trustedCertificates: ["AB:CD"]));
    }
}
