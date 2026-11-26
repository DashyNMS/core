using System.Text.RegularExpressions;
using DesktopNMS.Core.Updates;

namespace DesktopNMS.Core.Help;

/// <summary>
/// Which release a guide describes (#96). An app shows the guide for the
/// version that's running, not whatever is newest, so steps and names match
/// what's on screen.
/// </summary>
public sealed partial record HelpRelease
{
    private HelpRelease(string version, string reference)
    {
        Version = version;
        Reference = reference;
    }

    /// <summary>The version to show people, such as "2.0.0-nightly.4".</summary>
    public string Version { get; }

    /// <summary>The Git tag or commit the guide was taken from, used in file links and refreshes.</summary>
    public string Reference { get; }

    /// <summary>
    /// The release for an app's own version: its tag is the version with a "v".
    /// Build metadata after "+" is dropped. Null when it isn't a version, such
    /// as a local build that reports none.
    /// </summary>
    public static HelpRelease? ForVersion(string? version)
    {
        var text = version?.Trim().TrimStart('v', 'V');
        if (text?.IndexOf('+') is int plus and >= 0)
        {
            text = text[..plus];
        }

        return text is { Length: > 0 } && char.IsAsciiDigit(text[0]) && ReleaseVersion.IsValid(text) && SafeReference().IsMatch(text)
            ? new HelpRelease(text, "v" + text)
            : null;
    }

    /// <summary>A guide copied from a particular commit or tag, such as another product's that a host bundles.</summary>
    /// <exception cref="ArgumentException">The reference isn't a plain tag name or commit.</exception>
    public static HelpRelease ForReference(string version, string reference)
        => SafeReference().IsMatch(reference)
            ? new HelpRelease(version, reference)
            : throw new ArgumentException("A help release needs a plain tag name or commit.", nameof(reference));

    // It becomes part of an address and of a folder's name.
    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,99}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeReference();
}
