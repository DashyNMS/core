using System.Text.Json;
using DesktopNMS.Core.Json;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Tests;

/// <summary>Reads the sanitised LibreNMS responses in Fixtures/LibreNMS (#10).</summary>
internal static class LibreNmsFixtures
{
    public static async Task<IReadOnlyList<T>> Read<T>(string file, string property)
    {
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LibreNMS", file)));
        return document.RootElement.GetProperty(property).Deserialize<List<T>>(LibreNmsJson.Options)!;
    }

    /// <summary>A file of faults, marked as faults as the API would.</summary>
    public static async Task<IReadOnlyList<Alert>> Faults(string file)
    {
        var faults = await Read<Alert>(file, "alerts");
        foreach (var fault in faults)
        {
            fault.Source = AlertSource.Fault;
        }

        return faults;
    }
}