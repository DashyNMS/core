using System.Globalization;
using System.Text.RegularExpressions;

namespace DesktopNMS.Core.Search;

/// <summary>How a MAC address is written for people.</summary>
public enum MacAddressFormat
{
    /// <summary>aa:bb:cc:dd:ee:ff - LibreNMS's own display form.</summary>
    Colon,

    /// <summary>aa-bb-cc-dd-ee-ff - Windows.</summary>
    Hyphen,

    /// <summary>aabb.ccdd.eeff - Cisco.</summary>
    Dotted,

    /// <summary>aabbccddeeff - how LibreNMS stores it.</summary>
    Bare,
}

/// <summary>
/// MAC addresses as people paste them and as LibreNMS stores them (twelve
/// lowercase hex digits). Shared so every app reads and writes them alike.
/// </summary>
public static partial class MacAddresses
{
    /// <summary>
    /// The twelve lowercase hex digits of <paramref name="text"/> when it is a
    /// whole MAC address in a common form - colon or hyphen pairs, Cisco's
    /// dotted quads, bare hex, an optional 0x - otherwise null.
    /// </summary>
    public static string? Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var value = text.Trim();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            value = value[2..];
        }

        if (Separated().IsMatch(value))
        {
            // Unix tools drop leading zeros: 0:19:7c:2:1:68.
            return string.Concat(value.Split(':', '-').Select(p => p.PadLeft(2, '0'))).ToLowerInvariant();
        }

        return Dotted().IsMatch(value) || BareHex().IsMatch(value)
            ? new string(value.Where(Uri.IsHexDigit).ToArray()).ToLowerInvariant()
            : null;
    }

    /// <summary>
    /// <paramref name="mac"/> (any form <see cref="Normalise"/> reads) written in
    /// <paramref name="format"/>, or the text unchanged when it isn't a MAC.
    /// </summary>
    public static string Format(string? mac, MacAddressFormat format)
    {
        if (Normalise(mac) is not { } hex)
        {
            return mac ?? string.Empty;
        }

        return format switch
        {
            MacAddressFormat.Hyphen => Pairs(hex, '-'),
            MacAddressFormat.Dotted => string.Create(CultureInfo.InvariantCulture, $"{hex[..4]}.{hex[4..8]}.{hex[8..]}"),
            MacAddressFormat.Bare => hex,
            _ => Pairs(hex, ':'),
        };
    }

    /// <summary>
    /// Addresses that are never one host's: all zeros, broadcast, and
    /// multicast (the low bit of the first byte). Upstream's search leaves
    /// them out for the same reason.
    /// </summary>
    public static bool IsNotAHost(string hex)
        => hex == "000000000000" || hex == "ffffffffffff"
           || (byte.TryParse(hex.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var first) && (first & 1) == 1);

    private static string Pairs(string hex, char separator)
        => string.Join(separator, Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));

    [GeneratedRegex(@"^[0-9a-fA-F]{1,2}([:-])[0-9a-fA-F]{1,2}(\1[0-9a-fA-F]{1,2}){4}$")]
    private static partial Regex Separated();

    [GeneratedRegex(@"^[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}\.[0-9a-fA-F]{4}$")]
    private static partial Regex Dotted();

    [GeneratedRegex("^[0-9a-fA-F]{12}$")]
    private static partial Regex BareHex();
}
