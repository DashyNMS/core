using System.Text.Json;
using DesktopNMS.Core.Alerting;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace DesktopNMS.Core.Tests;

/// <summary>
/// Runs only when <c>DASHYNMS_LIVE_URL</c> and <c>DASHYNMS_LIVE_TOKEN</c> are
/// set - never in CI.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (LiveServer.Url is null || LiveServer.Token is null)
        {
            Skip = "Set DASHYNMS_LIVE_URL and DASHYNMS_LIVE_TOKEN to check a live LibreNMS server (#10).";
        }
    }
}

internal static class LiveServer
{
    public static Uri? Url => Uri.TryCreate(Environment.GetEnvironmentVariable("DASHYNMS_LIVE_URL"), UriKind.Absolute, out var url) ? url : null;

    public static string? Token => Environment.GetEnvironmentVariable("DASHYNMS_LIVE_TOKEN") is { Length: > 0 } token ? token : null;

    /// <summary>"admin", "read-only", "restricted" - only labels the output.</summary>
    public static string Account => Environment.GetEnvironmentVariable("DASHYNMS_LIVE_ACCOUNT") ?? "unlabelled";

    public static LibreNmsTransport Connect()
    {
        var transport = new LibreNmsTransport(NullLogger<LibreNmsTransport>.Instance);
        transport.Configure(new LibreNmsConnection(
            Url!,
            Token!,
            allowUntrustedCertificate: Environment.GetEnvironmentVariable("DASHYNMS_LIVE_INSECURE") == "1"));
        return transport;
    }
}

/// <summary>
/// The #10 integration run, read-only, against a real server and account:
/// <code>
/// $env:DASHYNMS_LIVE_URL = "https://librenms.example.net/"
/// $env:DASHYNMS_LIVE_TOKEN = "..."
/// $env:DASHYNMS_LIVE_ACCOUNT = "read-only"
/// dotnet test tests/DesktopNMS.Core.Tests -f net9.0 --filter LiveServerChecks --logger "console;verbosity=detailed"
/// </code>
/// Nothing is written to the server. The output is counts and versions only -
/// no device names, addresses or hostnames - so it can be pasted onto the
/// issue. Acknowledging, notes and undo stay a manual check.
/// </summary>
public class LiveServerChecks
{
    private readonly ITestOutputHelper _output;

    public LiveServerChecks(ITestOutputHelper output) => _output = output;

    [LiveFact]
    public async Task Alerts_faults_and_their_details()
    {
        using var transport = LiveServer.Connect();
        var system = await new SystemApi(transport).GetAsync();
        var alerts = new AlertsApi(transport);
        var source = await alerts.GetSourceAsync();
        var open = await alerts.ListAsync(AlertQuery.Open);

        var groups = AlertGrouping.ByDeviceAndRule(open);
        var rules = AlertGrouping.ByRule(groups);
        Report($"LibreNMS {LibreNmsVersion.Parse(system.LocalVersion)?.ToString() ?? "unknown"}, account {LiveServer.Account}, source {source}");
        Report($"Open: {open.Count} rows, {groups.Count} device/rule groups, {rules.Count} rules, largest group {(groups.Count > 0 ? groups.Max(g => g.FaultCount) : 0)} faults");
        Report($"Badge (device/rule groups, unacknowledged): {AlertCounting.CountGroups(groups, AlertSeverity.Ok, includeAcknowledged: false).Total}");

        Assert.All(open, a => Assert.Equal(source, a.Source));
        Assert.Equal(open.Count, groups.Sum(g => g.FaultCount));

        if (source == AlertSource.Fault)
        {
            var keyed = open.Where(f => !string.IsNullOrEmpty(f.EntityKey)).ToList();
            var named = keyed.Count(f => AlertGrouping.EntityName(f) is not null);
            Report($"Faults with an entity: {keyed.Count}, named: {named}; whole-device: {open.Count - keyed.Count}");
            Assert.Equal(keyed.Count, named);

            // Each fault's details are its own: its log entry carries its fault_id.
            var mismatched = 0;
            var checkedFaults = 0;
            foreach (var group in groups.Where(g => !g.IsSingle).Take(5))
            {
                var log = await new LogsApi(transport).ListAlertLogAsync(group.DeviceId, 500);
                foreach (var fault in group.Faults)
                {
                    checkedFaults++;
                    if (AlertFaultDetails.LatestLog(log, fault) is { } entry && entry.FaultId != fault.Id)
                    {
                        mismatched++;
                    }
                }

                var history = AlertHistory.Summarise(log, group.RuleId, group.DeviceId, DateTime.Now, serverTimestampsAreUtc: false, truncated: log.Count >= 500);
                Report($"Group of {group.FaultCount}: history '{history.Describe(DateTime.Now)}' over {AlertHistory.Occurrences(log, group.RuleId, group.DeviceId, false).Count} occurrences");
            }

            Report($"Fault details checked: {checkedFaults}, from another fault's log: {mismatched}");
            Assert.Equal(0, mismatched);
        }
    }

    [LiveFact]
    public async Task Rules_devices_and_groups()
    {
        using var transport = LiveServer.Connect();
        var version = LibreNmsVersion.Parse((await new SystemApi(transport).GetAsync()).LocalVersion);

        var rules = await TryAsync("rules", () => new AlertRulesApi(transport).ListAsync());
        if (rules is not null)
        {
            var flagged = rules.Count(r => AlertRuleCompatibility.FindRemovedColumns(r, version).Count > 0);
            Report($"Rules: {rules.Count}, per-entity on: {rules.Count(r => r.NotifyPerEntity == true)}, inherited limit: {rules.Count(r => r.NotifyPerEntity == true && r.MaxEntities is null)}, using removed columns: {flagged}");
        }

        var devices = await TryAsync("devices", () => new DevicesApi(transport).ListAsync());
        if (devices is { Count: > 0 })
        {
            Report($"Devices: {devices.Count}, sysContact override known: {devices.Count(d => d.IsSysContactOverrideKnown)}");

            // Every field Core reads, against what this server actually sends.
            using var raw = await transport.SendAsync(HttpMethod.Get, "devices?type=device_id&query=" + devices[0].DeviceId);
            var keys = raw.RootElement.GetProperty("devices")[0].EnumerateObject().Select(p => p.Name).ToHashSet();
            var missing = typeof(Device).GetProperties()
                .Select(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false).OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>().FirstOrDefault()?.Name)
                .OfType<string>()
                .Where(name => !keys.Contains(name))
                .ToList();
            Report($"Device fields Core reads that this server leaves out: {(missing.Count == 0 ? "none" : string.Join(", ", missing))}");
        }

        var groupsApi = new DeviceGroupsApi(transport);
        var groups = await TryAsync("device groups", () => groupsApi.ListAsync());
        if (groups is not null)
        {
            var counts = await groupsApi.GetMemberCountsAsync(groups);
            Report($"Device groups: {groups.Count}, empty or not visible: {counts.Count(c => c.Value == 0)}");
        }

        if (devices is { Count: > 0 })
        {
            var graphs = new GraphsApi(transport);
            var outcome = "shown";
            try
            {
                await graphs.GetSvgAsync(devices[0].DeviceId, "device_uptime", GraphTimeRange.LastDay, 300, 100);
            }
            catch (LibreNmsApiException ex)
            {
                outcome = ex.IsPermissionDenied ? "denied" : "failed: " + ex.StatusCode;
            }

            Report($"A device graph: {outcome}");
        }
    }

    private async Task<IReadOnlyList<T>?> TryAsync<T>(string what, Func<Task<IReadOnlyList<T>>> load)
    {
        try
        {
            return await load();
        }
        catch (LibreNmsApiException ex)
        {
            Report($"{char.ToUpperInvariant(what[0]) + what[1..]}: {VisibleTotals.Describe(VisibleTotals.Classify(null, ex), 0, what)}");
            return null;
        }
    }

    private void Report(string line) => _output.WriteLine(line);
}
