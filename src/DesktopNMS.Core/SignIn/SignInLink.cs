using System.Globalization;
using System.Text;
using DesktopNMS.Core.Api;

namespace DesktopNMS.Core.SignIn;

/// <summary>
/// A <c>dashynms://sign-in</c> link carrying everything a phone needs to sign
/// in as this computer does (desktop#263): the server address, the API token,
/// and the backup address and trusted certificate fingerprints if there are
/// any. Desktop shows it as a QR code; the phone's Camera app opens it.
/// </summary>
/// <remarks>
/// Core builds and reads the link, so the two apps can't drift apart. The
/// link is versioned (<c>v=1</c>): a reader turns down any other version
/// rather than guess, and ignores parameters it doesn't know so later
/// versions can add optional ones. Any web page can open a
/// <c>dashynms://</c> link, so a reader must always confirm with
/// <see cref="ConfirmationTitle"/> before using one, and never silently
/// replace a session.
/// </remarks>
public sealed class SignInLink
{
    public const string Scheme = "dashynms";
    public const string Action = "sign-in";
    public const int Version = 1;

    /// <summary>Far longer than any real link; anything bigger isn't one of ours.</summary>
    private const int MaxLength = 4096;

    private SignInLink(Uri server, string token, Uri? backup, IReadOnlyList<string> trustedCertificates)
    {
        Server = server;
        Token = token;
        Backup = backup;
        TrustedCertificates = trustedCertificates;
    }

    /// <summary>The LibreNMS web root, as <see cref="LibreNmsConnection.WebRoot"/>.</summary>
    public Uri Server { get; }

    public string Token { get; }

    /// <summary>The backup address, as <see cref="LibreNmsConnection.BackupWebRoot"/>.</summary>
    public Uri? Backup { get; }

    /// <summary>"AB:CD:..." SHA-256 fingerprints, as <see cref="Security.CertificateTrust.Fingerprint"/> writes them.</summary>
    public IReadOnlyList<string> TrustedCertificates { get; }

    /// <summary>
    /// The link trusts certificates by fingerprint, so the phone should turn
    /// on "Allow untrusted certificate" for exactly those.
    /// </summary>
    public bool AllowUntrustedCertificate => TrustedCertificates.Count > 0;

    /// <summary>The link for signing another device in as <paramref name="connection"/>.</summary>
    public static SignInLink Create(LibreNmsConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var certificates = connection.AllowUntrustedCertificate
            ? connection.TrustedCertificates.Select(NormaliseFingerprint).OfType<string>().Distinct(StringComparer.Ordinal).ToArray()
            : [];
        return Create(connection.WebRoot, connection.ApiToken, connection.BackupWebRoot, certificates);
    }

    /// <summary>
    /// The link for these details. Throws if any of them couldn't be read back
    /// by <see cref="TryParse(string?, out SignInLink?, out string?)"/>.
    /// </summary>
    public static SignInLink Create(Uri server, string token, Uri? backup = null, IEnumerable<string>? trustedCertificates = null)
    {
        var webRoot = ReadAddress(server?.AbsoluteUri) ?? throw new ArgumentException("The server address must be an http or https URL.", nameof(server));
        if (!ApiTokenText.IsToken(token))
        {
            throw new ArgumentException("That isn't a LibreNMS API token.", nameof(token));
        }

        Uri? backupRoot = null;
        if (backup is not null)
        {
            backupRoot = ReadAddress(backup.AbsoluteUri) ?? throw new ArgumentException("The backup address must be an http or https URL.", nameof(backup));
        }

        var certificates = new List<string>();
        foreach (var fingerprint in trustedCertificates ?? [])
        {
            var normalised = NormaliseFingerprint(fingerprint) ?? throw new ArgumentException("A trusted certificate isn't a SHA-256 fingerprint.", nameof(trustedCertificates));
            if (!certificates.Contains(normalised))
            {
                certificates.Add(normalised);
            }
        }

        return new SignInLink(webRoot, token, backupRoot, certificates);
    }

    /// <summary>
    /// The link as text, for a QR code. Fingerprints go without their colons
    /// to keep the code small; <see cref="TryParse(string?, out SignInLink?, out string?)"/> accepts either.
    /// </summary>
    public override string ToString()
    {
        var text = new StringBuilder($"{Scheme}://{Action}?v={Version.ToString(CultureInfo.InvariantCulture)}");
        Append(text, "server", Server.AbsoluteUri);
        Append(text, "token", Token);
        if (Backup is not null)
        {
            Append(text, "backup", Backup.AbsoluteUri);
        }

        foreach (var fingerprint in TrustedCertificates)
        {
            Append(text, "cert", fingerprint.Replace(":", string.Empty, StringComparison.Ordinal));
        }

        return text.ToString();
    }

    public Uri ToUri() => new(ToString());

    /// <summary>"Sign in to nms.example.com?" - the question to ask before using the link.</summary>
    public string ConfirmationTitle => $"Sign in to {Describe(Server)}?";

    /// <summary>What signing in with the link does, for under <see cref="ConfirmationTitle"/>.</summary>
    public string ConfirmationDetail
    {
        get
        {
            var detail = new StringBuilder($"DashyNMS will sign in to {Server.AbsoluteUri}");
            if (Backup is not null)
            {
                detail.Append(CultureInfo.InvariantCulture, $", with {Backup.AbsoluteUri} as the backup address");
            }

            detail.Append(", using the API token in this link");
            if (AllowUntrustedCertificate)
            {
                detail.Append(TrustedCertificates.Count == 1
                    ? ", and trust the server certificate the link names"
                    : ", and trust the server certificates the link names");
            }

            detail.Append(". Only continue if you've just scanned this from DashyNMS on your own computer.");
            return detail.ToString();
        }
    }

    public static bool TryParse(string? text, out SignInLink? link) => TryParse(text, out link, out _);

    /// <summary>
    /// Reads a link, turning down anything that isn't exactly a version 1
    /// sign-in link with a usable server address and token. <paramref name="error"/>
    /// says why, in words for the user.
    /// </summary>
    public static bool TryParse(string? text, out SignInLink? link, out string? error)
    {
        link = null;
        error = Read(text?.Trim(), out var server, out var token, out var backup, out var certificates);
        if (error is not null)
        {
            return false;
        }

        link = new SignInLink(server!, token!, backup, certificates!);
        return true;
    }

    /// <summary>Null when the link reads; otherwise why not. The out values only mean anything when it reads.</summary>
    private static string? Read(string? text, out Uri? server, out string? token, out Uri? backup, out List<string>? certificates)
    {
        server = null;
        token = null;
        backup = null;
        certificates = null;
        const string NotALink = "That isn't a DashyNMS sign-in link.";

        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return NotALink;
        }

        var prefix = $"{Scheme}://{Action}";
        if (!text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return NotALink;
        }

        var rest = text[prefix.Length..];
        if (rest.StartsWith('/'))
        {
            rest = rest[1..];
        }

        if (!rest.StartsWith('?'))
        {
            return NotALink;
        }

        var hash = rest.IndexOf('#', StringComparison.Ordinal);
        var query = hash >= 0 ? rest[1..hash] : rest[1..];

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var fingerprints = new List<string>();
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                return NotALink;
            }

            string name, value;
            try
            {
                name = Uri.UnescapeDataString(pair[..equals]);
                value = Uri.UnescapeDataString(pair[(equals + 1)..]);
            }
            catch (UriFormatException)
            {
                return NotALink;
            }

            if (name == "cert")
            {
                fingerprints.Add(value);
            }
            else if (name is "v" or "server" or "token" or "backup")
            {
                // Two of the same could be read either way; a genuine link never has them.
                if (!values.TryAdd(name, value))
                {
                    return NotALink;
                }
            }
        }

        if (!values.TryGetValue("v", out var version))
        {
            return NotALink;
        }

        if (version != Version.ToString(CultureInfo.InvariantCulture))
        {
            return "This sign-in link is from a newer DashyNMS. Update DashyNMS and scan it again.";
        }

        server = ReadAddress(values.GetValueOrDefault("server"));
        if (server is null)
        {
            return "The sign-in link doesn't have a usable server address.";
        }

        var foundToken = values.GetValueOrDefault("token");
        if (!ApiTokenText.IsToken(foundToken))
        {
            return "The sign-in link doesn't have a usable API token.";
        }

        if (values.TryGetValue("backup", out var backupText))
        {
            backup = ReadAddress(backupText);
            if (backup is null)
            {
                return "The sign-in link's backup address isn't usable.";
            }
        }

        certificates = [];
        foreach (var fingerprint in fingerprints)
        {
            var normalised = NormaliseFingerprint(fingerprint);
            if (normalised is null)
            {
                return "The sign-in link's certificate fingerprint isn't usable.";
            }

            if (!certificates.Contains(normalised))
            {
                certificates.Add(normalised);
            }
        }

        token = foundToken;
        return null;
    }

    /// <summary>
    /// A complete http or https address, as <see cref="LibreNmsConnection.TryParseWebRoot"/>
    /// tidies it. Unlike typing an address, a link must say its scheme, and
    /// mustn't carry a user name or password.
    /// </summary>
    private static Uri? ReadAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)
            || !Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)
            || parsed.UserInfo.Length > 0
            || string.IsNullOrEmpty(parsed.Host))
        {
            return null;
        }

        return LibreNmsConnection.TryParseWebRoot(parsed.AbsoluteUri, out var webRoot, out _) ? webRoot : null;
    }

    /// <summary>"AB:CD:..." from a SHA-256 fingerprint written with or without separators; null if it isn't one.</summary>
    private static string? NormaliseFingerprint(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var hex = new StringBuilder(64);
        foreach (var c in text)
        {
            if (char.IsAsciiHexDigit(c))
            {
                hex.Append(char.ToUpperInvariant(c));
            }
            else if (c is not (':' or ' ' or '-'))
            {
                return null;
            }
        }

        if (hex.Length != 64)
        {
            return null;
        }

        return string.Join(':', Enumerable.Range(0, 32).Select(i => hex.ToString(i * 2, 2)));
    }

    private static string Describe(Uri address)
    {
        var path = address.AbsolutePath.TrimEnd('/');
        return address.IsDefaultPort ? address.Host + path : $"{address.Host}:{address.Port.ToString(CultureInfo.InvariantCulture)}{path}";
    }

    private static void Append(StringBuilder text, string name, string value)
        => text.Append('&').Append(name).Append('=').Append(Uri.EscapeDataString(value));
}
