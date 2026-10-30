using System.Net;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Graylog;
using DesktopNMS.Core.Logs;
using DesktopNMS.Core.Models;
using DesktopNMS.Core.Services;
using DesktopNMS.Core.Spotlights;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>A dashboard card's Spotlight (desktop#552), from saving it to what the card fetches.</summary>
public sealed class SpotlightTests : IDisposable
{
    private static readonly Device[] Devices =
    {
        new() { DeviceId = 1, Hostname = "core-01.example.net", Ip = "192.0.2.1", LocationId = 10 },
        new() { DeviceId = 2, Hostname = "core-02.example.net", Ip = "192.0.2.2", LocationId = 10 },
        new() { DeviceId = 3, Hostname = "edge-01.example.net", Ip = "198.51.100.1", SysName = "edge-01", LocationId = 20 },
        new() { DeviceId = 4, Hostname = "edge-02.example.net", Ip = "198.51.100.2" },
    };

    private static readonly DeviceGroup[] Groups = { new() { Id = 5, Name = "Core routers" } };

    private static readonly Location[] Locations = { new() { Id = 10, Name = "London" }, new() { Id = 20, Name = "Leeds" } };

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dashynms-tests-" + Guid.NewGuid().ToString("N"));

    public SpotlightTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void No_Spotlight_is_the_whole_network()
    {
        var scope = SpotlightResolver.Resolve(new List<SpotlightTarget>(), Devices, null, new Dictionary<int, IReadOnlyList<int>>(), null);

        Assert.True(scope.IsWholeNetwork);
        Assert.True(scope.Includes(99));
        Assert.Equal("Whole network", scope.Summary);
    }

    [Fact]
    public void Devices_groups_and_locations_become_device_ids()
    {
        var targets = new List<SpotlightTarget>
        {
            SpotlightTarget.Device(4, "edge-02"),
            SpotlightTarget.Group(5, "Old name"),
            SpotlightTarget.Location(20, "Leeds"),
        };
        var members = new Dictionary<int, IReadOnlyList<int>> { [5] = new[] { 1, 2 } };

        var scope = SpotlightResolver.Resolve(targets, Devices, Groups, members, Locations);

        Assert.False(scope.IsWholeNetwork);
        Assert.Equal(new[] { 1, 2, 3, 4 }, scope.DeviceIds.OrderBy(id => id));
        Assert.False(scope.HasMissing);
        Assert.Equal("edge-02, Core routers, Leeds", scope.Summary);
    }

    [Fact]
    public void Removed_entries_stay_and_are_called_unknown()
    {
        var targets = new List<SpotlightTarget>
        {
            SpotlightTarget.Device(77, "old-switch"),
            SpotlightTarget.Group(6, "Retired"),
            SpotlightTarget.Location(30, "Closed site"),
        };

        var scope = SpotlightResolver.Resolve(targets, Devices, Groups, new Dictionary<int, IReadOnlyList<int>>(), Locations);

        Assert.False(scope.IsWholeNetwork);
        Assert.True(scope.IsEmpty);
        Assert.Equal(3, scope.Missing.Count);
        Assert.False(scope.Includes(1));
        Assert.Equal("Unknown device (old-switch), Unknown group (Retired), Unknown location (Closed site)", scope.Summary);
    }

    [Fact]
    public void A_device_is_trusted_before_the_device_list_loads()
    {
        var scope = SpotlightResolver.Resolve(new List<SpotlightTarget> { SpotlightTarget.Device(77, "x") }, Array.Empty<Device>(), null, new Dictionary<int, IReadOnlyList<int>>(), null);

        Assert.Equal(new[] { 77 }, scope.DeviceIds);
        Assert.False(scope.HasMissing);
    }

    [Fact]
    public void Unknown_kinds_are_left_out_rather_than_guessed()
    {
        var targets = new List<SpotlightTarget> { new() { KindName = "tenant", Id = 3, Name = "Newer app" } };

        Assert.True(SpotlightResolver.Resolve(targets, Devices, null, new Dictionary<int, IReadOnlyList<int>>(), null).IsWholeNetwork);
    }

    [Fact]
    public async Task Group_members_and_lists_are_cached_until_they_go_stale()
    {
        var time = new ManualTime();
        var groups = new FakeGroups();
        var locations = new FakeLocations();
        var resolver = new SpotlightResolver(groups, locations, time);
        var targets = new List<SpotlightTarget> { SpotlightTarget.Group(5, "Core routers"), SpotlightTarget.Location(10, "London") };

        await resolver.ResolveAsync(targets, Devices);
        await resolver.ResolveAsync(targets, Devices);
        Assert.Equal((1, 1, 1), (groups.ListCalls, groups.MemberCalls, locations.ListCalls));

        time.Now += SpotlightResolver.CacheLifetime;
        await resolver.ResolveAsync(targets, Devices);
        Assert.Equal((2, 2, 2), (groups.ListCalls, groups.MemberCalls, locations.ListCalls));

        resolver.Invalidate();
        await resolver.ResolveAsync(targets, Devices);
        Assert.Equal((3, 3, 3), (groups.ListCalls, groups.MemberCalls, locations.ListCalls));
    }

    [Fact]
    public async Task Only_what_a_Spotlight_needs_is_asked_for()
    {
        var groups = new FakeGroups();
        var locations = new FakeLocations();
        var resolver = new SpotlightResolver(groups, locations);

        var scope = await resolver.ResolveAsync(new List<SpotlightTarget> { SpotlightTarget.Device(1, "core-01") }, Devices);

        Assert.Equal(new[] { 1 }, scope.DeviceIds);
        Assert.Equal((0, 0, 0), (groups.ListCalls, groups.MemberCalls, locations.ListCalls));
    }

    [Fact]
    public async Task A_group_this_account_cannot_read_is_missing()
    {
        var groups = new FakeGroups { Refuse = true };
        var scope = await new SpotlightResolver(groups, new FakeLocations()).ResolveAsync(new List<SpotlightTarget> { SpotlightTarget.Group(5, "Core routers") }, Devices);

        Assert.True(scope.IsEmpty);
        Assert.Single(scope.Missing);
    }

    [Fact]
    public async Task Event_log_whole_network_reads_the_fleet_wide_log()
    {
        var logs = new FakeLogs();
        await EventLogFeed.FetchAsync(logs, 10, null, null, SpotlightScope.WholeNetwork);

        Assert.Equal(new[] { ((int?)null, 10) }, logs.Calls);
    }

    [Fact]
    public async Task Event_log_for_a_few_devices_reads_each_ones_log_and_merges_newest_first()
    {
        var logs = new FakeLogs();
        var scope = Scope(1, 3);

        var entries = await EventLogFeed.FetchAsync(logs, 10, "system", null, scope);

        Assert.Equal(new[] { ((int?)1, EventLogFeed.FilteredFetchLimit), ((int?)3, EventLogFeed.FilteredFetchLimit) }, logs.Calls.OrderBy(c => c.DeviceId));
        Assert.Equal(new[] { 301, 101 }, entries.Select(e => e.Id));
    }

    [Fact]
    public async Task Event_log_for_a_big_Spotlight_filters_the_fleet_wide_log()
    {
        var logs = new FakeLogs();
        var scope = Scope(Enumerable.Range(1, EventLogFeed.MaxPerDeviceFetches + 1).ToArray());

        var entries = await EventLogFeed.FetchAsync(logs, 10, null, null, scope);

        Assert.Equal(new[] { ((int?)null, EventLogFeed.SpotlightFetchLimit) }, logs.Calls);
        Assert.All(entries, e => Assert.True(scope.Includes(e.DeviceId)));
        Assert.DoesNotContain(entries, e => e.DeviceId == 42);
    }

    [Fact]
    public async Task Event_log_for_an_empty_Spotlight_asks_for_nothing()
    {
        var logs = new FakeLogs();
        var entries = await EventLogFeed.FetchAsync(logs, 10, null, null, Scope());

        Assert.Empty(entries);
        Assert.Empty(logs.Calls);
    }

    [Fact]
    public void Graylog_whole_network_is_the_search_alone()
    {
        Assert.Equal("*", GraylogQuery.ForSpotlight(" ", null, SpotlightScope.WholeNetwork, Devices).Query);
        Assert.Equal("level:<=3", GraylogQuery.ForSpotlight("level:<=3", null, SpotlightScope.WholeNetwork, Devices).Query);
    }

    [Fact]
    public void Graylog_Spotlight_matches_its_devices_addresses_with_the_search()
    {
        var query = GraylogQuery.ForSpotlight("level:<=3", "source", Scope(1, 3), Devices);

        Assert.Equal(
            "(level:<=3) AND source: (\"core-01.example.net\" OR \"192.0.2.1\" OR \"edge-01.example.net\" OR \"198.51.100.1\" OR \"edge-01\")",
            query.Query);
        Assert.Equal(0, query.DevicesLeftOut);
    }

    [Fact]
    public void Graylog_Spotlight_with_no_devices_has_nothing_to_search()
    {
        Assert.Null(GraylogQuery.ForSpotlight(null, null, Scope(), Devices).Query);
        Assert.Null(GraylogQuery.ForSpotlight(null, null, Scope(77), Devices).Query);
    }

    [Fact]
    public void Graylog_Spotlight_stops_before_the_query_gets_too_long()
    {
        var many = Enumerable.Range(1, 200)
            .Select(i => new Device { DeviceId = i, Hostname = $"switch-{i:000}.branch.example.net", Ip = $"203.0.113.{i}" })
            .ToList();

        var query = GraylogQuery.ForSpotlight(null, null, Scope(many.Select(d => d.DeviceId).ToArray()), many);

        Assert.True(query.Query!.Length <= GraylogQuery.MaxQueryLength);
        Assert.True(query.DevicesLeftOut > 0);
        Assert.Contains("switch-001.branch.example.net", query.Query);
    }

    [Fact]
    public void A_layout_saved_before_Spotlight_loads_as_the_whole_network()
    {
        File.WriteAllText(SettingsFile, """{ "dashboardWidgets": [ { "id": "a", "widgetType": "EventLog", "logCount": 25 } ] }""");

        var widget = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile).Load().DashboardWidgets.Single();

        Assert.Empty(widget.Spotlight);
    }

    [Fact]
    public void Spotlight_saves_and_reloads_and_keeps_kinds_it_does_not_know()
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        store.Load();
        store.Current.DashboardWidgets.Add(new DashboardWidget { Id = "a", WidgetType = DashboardWidgetTypes.Graylog });
        var layout = new DashboardLayoutService(store);

        layout.SetSpotlight("a", new[] { SpotlightTarget.Group(5, "Core routers"), SpotlightTarget.Group(5, "Core routers"), SpotlightTarget.Device(1, "core-01") });

        var text = File.ReadAllText(SettingsFile).Replace("\"kind\": \"device\"", "\"kind\": \"tenant\"", StringComparison.Ordinal);
        File.WriteAllText(SettingsFile, text);
        var reloaded = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile).Load().DashboardWidgets.Single();

        Assert.Equal(2, reloaded.Spotlight.Count);
        Assert.Equal(SpotlightKind.Group, reloaded.Spotlight[0].Kind);
        Assert.Equal("Core routers", reloaded.Spotlight[0].Name);
        Assert.Null(reloaded.Spotlight[1].Kind);
        Assert.Equal("tenant", reloaded.Spotlight[1].KindName);
    }

    [Fact]
    public void SetSpotlight_saves_only_a_change()
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        store.Load();
        store.Current.DashboardWidgets.Add(new DashboardWidget { Id = "a", WidgetType = DashboardWidgetTypes.EventLog });
        var layout = new DashboardLayoutService(store);
        var changes = 0;
        layout.Changed += (_, _) => changes++;

        layout.SetSpotlight("a", new[] { SpotlightTarget.Location(10, "London") });
        layout.SetSpotlight("a", new[] { SpotlightTarget.Location(10, "London") });
        layout.SetSpotlight("a", Array.Empty<SpotlightTarget>());

        Assert.Equal(2, changes);
        Assert.Empty(store.Current.DashboardWidgets.Single().Spotlight);
    }

    [Fact]
    public void Duplicating_a_card_copies_its_Spotlight()
    {
        var original = new DashboardWidget { Spotlight = { SpotlightTarget.Group(5, "Core routers") } };

        var copy = original.Clone();
        copy.Spotlight[0].Name = "Changed";

        Assert.Equal("Core routers", original.Spotlight[0].Name);
        Assert.True(copy.Spotlight[0].SameAs(original.Spotlight[0]));
    }

    private string SettingsFile => Path.Combine(_folder, "settings.json");

    private static SpotlightScope Scope(params int[] ids)
        => SpotlightScope.Of(ids.ToHashSet(), ids.Select(id => SpotlightTarget.Device(id, "device " + id)).ToList(), Array.Empty<SpotlightTarget>());

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FakeLogs : ILogsApi
    {
        public List<(int? DeviceId, int Limit)> Calls { get; } = new();

        public Task<IReadOnlyList<EventLogEntry>> ListEventLogAsync(int? deviceId, int limit = 50, CancellationToken cancellationToken = default)
        {
            lock (Calls)
            {
                Calls.Add((deviceId, limit));
            }

            IReadOnlyList<EventLogEntry> entries = deviceId is { } id
                ? new[] { new EventLogEntry { Id = id * 100 + 1, DeviceId = id, Timestamp = new DateTime(2026, 10, 30, 9, id, 0) } }
                : Enumerable.Range(1, 12).Select(d => new EventLogEntry { Id = d, DeviceId = d == 12 ? 42 : d }).ToArray();
            return Task.FromResult(entries);
        }

        public Task<IReadOnlyList<AlertLogEntry>> ListAlertLogAsync(int? deviceId, int limit = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<AlertLogEntry?> GetLatestForRuleAsync(int deviceId, int ruleId, int searchDepth = 50, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeGroups : IDeviceGroupsApi
    {
        public int ListCalls { get; private set; }

        public int MemberCalls { get; private set; }

        public bool Refuse { get; init; }

        public Task<IReadOnlyList<DeviceGroup>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<DeviceGroup>>(Groups);
        }

        public Task<IReadOnlyList<int>> GetMemberDeviceIdsAsync(int groupId, CancellationToken cancellationToken = default)
        {
            MemberCalls++;
            if (Refuse)
            {
                throw new LibreNmsApiException("Forbidden", HttpStatusCode.Forbidden);
            }

            return Task.FromResult<IReadOnlyList<int>>(new[] { 1, 2 });
        }

        public Task<IReadOnlyList<DeviceGroup>> ListForDeviceAsync(int deviceId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, IReadOnlyList<string>>> GetMembershipByDeviceAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyDictionary<int, int>> GetMemberCountsAsync(IReadOnlyList<DeviceGroup> groups, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task CreateAsync(string name, string? description, IReadOnlyList<int> deviceIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateAsync(string currentName, string newName, string? description, IReadOnlyList<int> deviceIds, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(string name, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeLocations : ILocationsApi
    {
        public int ListCalls { get; private set; }

        public Task<IReadOnlyList<Location>> ListAsync(CancellationToken cancellationToken = default)
        {
            ListCalls++;
            return Task.FromResult<IReadOnlyList<Location>>(Locations);
        }

        public Task CreateAsync(string name, double lat, double lng, bool fixedCoordinates, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task UpdateAsync(int id, string name, double lat, double lng, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(int id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
