using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;

namespace DesktopNMS.Core.Spotlights;

/// <summary>
/// Works out which devices a dashboard card's Spotlight covers (desktop#552):
/// a device is itself, a group is its members, a location is the devices
/// there. The group and location lists and each group's members are cached
/// for <see cref="CacheLifetime"/>, as a card asks again on every poll.
/// One per server connection; safe to share between cards.
/// </summary>
public sealed class SpotlightResolver
{
    /// <summary>How long group members and the group and location lists are reused before asking LibreNMS again.</summary>
    public static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(5);

    private readonly IDeviceGroupsApi _groups;
    private readonly ILocationsApi _locations;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly Dictionary<int, Cached<IReadOnlyList<int>>> _members = new();
    private Cached<IReadOnlyList<DeviceGroup>>? _groupList;
    private Cached<IReadOnlyList<Location>>? _locationList;

    public SpotlightResolver(IDeviceGroupsApi groups, ILocationsApi locations, TimeProvider? time = null)
    {
        _groups = groups;
        _locations = locations;
        _time = time ?? TimeProvider.System;
    }

    public SpotlightResolver(ILibreNmsClient client, TimeProvider? time = null)
        : this(client.DeviceGroups, client.Locations, time)
    {
    }

    /// <summary>
    /// The devices <paramref name="targets"/> cover, among <paramref name="devices"/>
    /// (the app's current device list). No targets is the whole network.
    /// LibreNMS errors other than a refused group propagate, for the card to show.
    /// </summary>
    public async Task<SpotlightScope> ResolveAsync(IReadOnlyList<SpotlightTarget>? targets, IReadOnlyCollection<Device> devices, CancellationToken cancellationToken = default)
    {
        var known = Known(targets);
        if (known.Count == 0)
        {
            return SpotlightScope.WholeNetwork;
        }

        IReadOnlyList<DeviceGroup>? groups = null;
        var members = new Dictionary<int, IReadOnlyList<int>>();
        if (known.Any(t => t.Kind == SpotlightKind.Group))
        {
            groups = await GetGroupsAsync(cancellationToken).ConfigureAwait(false);
            foreach (var target in known.Where(t => t.Kind == SpotlightKind.Group && groups.Any(g => g.Id == t.Id)))
            {
                if (await GetMembersAsync(target.Id, cancellationToken).ConfigureAwait(false) is { } ids)
                {
                    members[target.Id] = ids;
                }
            }
        }

        IReadOnlyList<Location>? locations = null;
        if (known.Any(t => t.Kind == SpotlightKind.Location))
        {
            locations = await GetLocationsAsync(cancellationToken).ConfigureAwait(false);
        }

        return Resolve(known, devices, groups, members, locations);
    }

    /// <summary>Forgets what's cached - after editing a group, or switching server.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _members.Clear();
            _groupList = null;
            _locationList = null;
        }
    }

    /// <summary>
    /// The working-out itself, with everything already fetched. A device
    /// counts as missing only once <paramref name="devices"/> has loaded and
    /// doesn't have it; a group is missing when it's not in
    /// <paramref name="groups"/> or its members couldn't be read
    /// (absent from <paramref name="groupMembers"/>); a location when it's
    /// not in <paramref name="locations"/>. Groups and locations take their
    /// current names.
    /// </summary>
    public static SpotlightScope Resolve(
        IReadOnlyList<SpotlightTarget>? targets,
        IReadOnlyCollection<Device> devices,
        IReadOnlyList<DeviceGroup>? groups,
        IReadOnlyDictionary<int, IReadOnlyList<int>> groupMembers,
        IReadOnlyList<Location>? locations)
    {
        var known = Known(targets);
        if (known.Count == 0)
        {
            return SpotlightScope.WholeNetwork;
        }

        var ids = new HashSet<int>();
        var resolved = new List<SpotlightTarget>();
        var missing = new List<SpotlightTarget>();

        foreach (var target in known)
        {
            var current = target.Clone();
            switch (target.Kind)
            {
                case SpotlightKind.Device:
                    // Before the device list loads, trust the saved id rather than call it missing.
                    if (devices.Count > 0 && !devices.Any(d => d.DeviceId == target.Id))
                    {
                        missing.Add(current);
                    }
                    else
                    {
                        ids.Add(target.Id);
                    }

                    break;

                case SpotlightKind.Group:
                    if (groups?.FirstOrDefault(g => g.Id == target.Id) is { } group && groupMembers.TryGetValue(target.Id, out var memberIds))
                    {
                        current.Name = string.IsNullOrWhiteSpace(group.Name) ? current.Name : group.Name;
                        ids.UnionWith(memberIds);
                    }
                    else
                    {
                        missing.Add(current);
                    }

                    break;

                case SpotlightKind.Location:
                    if (locations?.FirstOrDefault(l => l.Id == target.Id) is { } location)
                    {
                        current.Name = string.IsNullOrWhiteSpace(location.Name) ? current.Name : location.Name;
                        ids.UnionWith(devices.Where(d => d.LocationId == target.Id).Select(d => d.DeviceId));
                    }
                    else
                    {
                        missing.Add(current);
                    }

                    break;
            }

            resolved.Add(current);
        }

        return SpotlightScope.Of(ids, resolved, missing);
    }

    /// <summary>The entries this version understands, each once - an unknown kind from a newer app is left out rather than guessed at.</summary>
    private static List<SpotlightTarget> Known(IReadOnlyList<SpotlightTarget>? targets)
    {
        var known = new List<SpotlightTarget>();
        foreach (var target in targets ?? Array.Empty<SpotlightTarget>())
        {
            if (target.Kind is not null && target.Id > 0 && !known.Any(k => k.SameAs(target)))
            {
                known.Add(target);
            }
        }

        return known;
    }

    private async Task<IReadOnlyList<DeviceGroup>> GetGroupsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Fresh(_groupList) is { } cached)
            {
                return cached;
            }
        }

        var groups = await _groups.ListAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _groupList = new(groups, _time.GetUtcNow());
        }

        return groups;
    }

    /// <summary>A group's member ids, or null when LibreNMS refuses this account the group.</summary>
    private async Task<IReadOnlyList<int>?> GetMembersAsync(int groupId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_members.TryGetValue(groupId, out var entry) && Fresh(entry) is { } cached)
            {
                return cached;
            }
        }

        IReadOnlyList<int> ids;
        try
        {
            ids = await _groups.GetMemberDeviceIdsAsync(groupId, cancellationToken).ConfigureAwait(false);
        }
        catch (LibreNmsApiException ex) when (ex.IsPermissionDenied)
        {
            return null;
        }

        lock (_gate)
        {
            _members[groupId] = new(ids, _time.GetUtcNow());
        }

        return ids;
    }

    private async Task<IReadOnlyList<Location>> GetLocationsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (Fresh(_locationList) is { } cached)
            {
                return cached;
            }
        }

        var locations = await _locations.ListAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _locationList = new(locations, _time.GetUtcNow());
        }

        return locations;
    }

    private T? Fresh<T>(Cached<T>? cached) where T : class
        => cached is not null && _time.GetUtcNow() - cached.At < CacheLifetime ? cached.Value : null;

    private sealed record Cached<T>(T Value, DateTimeOffset At);
}
