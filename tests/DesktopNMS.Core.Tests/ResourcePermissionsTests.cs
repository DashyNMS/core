using System.Net;
using System.Text.Json;
using DesktopNMS.Core.Api;
using DesktopNMS.Core.Models;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>LibreNMS 26.10's per-resource permissions (#9).</summary>
public class ResourcePermissionsTests
{
    private static readonly LibreNmsApiException ItemDenied =
        new("denied", HttpStatusCode.Forbidden, "Insufficient permissions to update this alert");

    private static readonly LibreNmsApiException TypeDenied =
        new("denied", HttpStatusCode.Forbidden, "This action is unauthorized.");

    private static readonly LibreNmsApiException Crashed =
        new("failed", HttpStatusCode.InternalServerError, "Server Error");

    [Fact]
    public void One_denied_alert_doesnt_turn_acknowledging_off_for_the_rest()
    {
        var permissions = new ApiPermissions();

        Assert.True(permissions.Learn(HttpMethod.Put, "faults/9001", ItemDenied));

        Assert.False(permissions.IsRefused(ApiPermission.AcknowledgeAlerts));
        Assert.True(permissions.IsAcknowledgeRefused(new AlertKey(AlertSource.Fault, 9001)));
        Assert.True(permissions.IsRefusedFor(HttpMethod.Put, "faults/unmute/9001"));
        Assert.False(permissions.IsAcknowledgeRefused(new AlertKey(AlertSource.Fault, 9002)));
        Assert.False(permissions.IsAcknowledgeRefused(new AlertKey(AlertSource.Alert, 9001)));
    }

    [Fact]
    public void A_refusal_of_the_whole_type_still_applies_everywhere()
    {
        var permissions = new ApiPermissions();

        permissions.Learn(HttpMethod.Put, "faults/9001", TypeDenied);

        Assert.True(permissions.IsRefused(ApiPermission.AcknowledgeAlerts));
        Assert.True(permissions.IsAcknowledgeRefused(new AlertKey(AlertSource.Fault, 9002)));
    }

    [Theory]
    [InlineData("Insufficient permissions to update this alert", true)]
    [InlineData("Insufficient permissions to access this device", true)]
    [InlineData("Insufficient permissions to access the target device", true)]
    [InlineData("This action is unauthorized.", false)]
    [InlineData(null, false)]
    public void Item_refusals_are_told_from_the_routes_own(string? message, bool item)
        => Assert.Equal(item, ApiPermissions.IsItemRefusal(new LibreNmsApiException("x", HttpStatusCode.Forbidden, message)));

    [Fact]
    public void On_26_10_a_500_is_a_failure_not_only_admins_can()
    {
        var permissions = new ApiPermissions();
        Assert.True(permissions.IsCrashedPermissionCheckOnThisServer(HttpMethod.Put, "alerts/12", Crashed));

        permissions.NoteServerVersion(new Version(26, 9, 1));
        Assert.True(permissions.IsCrashedPermissionCheckOnThisServer(HttpMethod.Put, "alerts/12", Crashed));

        permissions.NoteServerVersion(new Version(26, 10, 1));
        Assert.True(permissions.HasPerResourceChecks);
        Assert.False(permissions.IsCrashedPermissionCheckOnThisServer(HttpMethod.Put, "faults/12", Crashed));
    }

    [Fact]
    public void A_new_connection_forgets_the_version_and_item_refusals()
    {
        var permissions = new ApiPermissions();
        permissions.NoteServerVersion(new Version(26, 10, 0));
        permissions.Learn(HttpMethod.Put, "faults/9001", ItemDenied);

        permissions.Forget();

        Assert.Null(permissions.ServerVersion);
        Assert.False(permissions.IsAcknowledgeRefused(new AlertKey(AlertSource.Fault, 9001)));
    }

    [Fact]
    public async Task The_server_version_is_learned_from_system_and_from_faults()
    {
        var fromSystem = new RecordingTransport("""{"status":"ok","system":[{"local_ver":"26.10.1"}]}""");
        await new LibreNmsClientProbe(fromSystem).System.GetAsync();
        Assert.Equal(new Version(26, 10, 1), fromSystem.Permissions.ServerVersion);

        var fromFaults = new RecordingTransport("""{"status":"ok","alerts":[]}""");
        Assert.Equal(AlertSource.Fault, await new AlertsApi(fromFaults).GetSourceAsync());
        Assert.True(fromFaults.Permissions.HasPerResourceChecks);
    }

    [Fact]
    public void A_denied_graph_is_denied_not_broken()
    {
        const string denied = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 300 100"><foreignObject x="0" y="0" width="300" height="100">
            <xhtml:div style="color:rgb(128, 0, 0)">No Authorization</xhtml:div></foreignObject></svg>
            """;
        const string small = """<svg><foreignObject><xhtml:div>No Auth</xhtml:div></foreignObject></svg>""";
        const string noData = """<svg><foreignObject><xhtml:div>No Data</xhtml:div></foreignObject></svg>""";

        Assert.True(GraphImages.IsDenied(denied));
        Assert.True(GraphImages.IsDenied(small));
        Assert.False(GraphImages.IsDenied(noData));
        Assert.False(GraphImages.IsDenied("<svg><text>No Authorization needed</text></svg>"));

        var ex = Assert.Throws<LibreNmsApiException>(() => GraphImages.ThrowIfDenied(denied));
        Assert.True(ex.IsPermissionDenied);
    }

    [Theory]
    [InlineData(12, null, SourceOutcome.Loaded, "12 devices visible to this account")]
    [InlineData(1, null, SourceOutcome.Loaded, "1 device visible to this account")]
    [InlineData(0, null, SourceOutcome.Empty, "No devices visible to this account")]
    [InlineData(null, HttpStatusCode.Forbidden, SourceOutcome.Denied, "This account isn't allowed to see devices")]
    [InlineData(null, HttpStatusCode.InternalServerError, SourceOutcome.Failed, "Devices couldn't be loaded")]
    public void Totals_say_whose_they_are(int? count, HttpStatusCode? status, SourceOutcome outcome, string text)
    {
        var error = status is { } code ? new LibreNmsApiException("x", code) : null;

        var classified = VisibleTotals.Classify(count, error);

        Assert.Equal(outcome, classified);
        Assert.Equal(text, VisibleTotals.Describe(classified, count ?? 0, "devices"));
    }

    /// <summary>Answers every GET with one body, and tracks permissions like the real transport.</summary>
    private sealed class RecordingTransport : ILibreNmsTransport
    {
        private readonly string _body;

        public RecordingTransport(string body) => _body = body;

        public ApiPermissions Permissions { get; } = new();

        ApiPermissions? ILibreNmsTransport.Permissions => Permissions;

        public LibreNmsConnection? Connection { get; } = new(new Uri("https://nms.example.net/"), "token");

        public Task<IReadOnlyList<T>> GetCollectionAsync<T>(string relativeUrl, string collectionProperty, CancellationToken cancellationToken = default)
        {
            using var document = JsonDocument.Parse(_body);
            var items = document.RootElement.TryGetProperty(collectionProperty, out var array)
                ? array.Deserialize<List<T>>(Json.LibreNmsJson.Options)!
                : new List<T>();
            return Task.FromResult<IReadOnlyList<T>>(items);
        }

        public Task<JsonDocument> SendAsync(HttpMethod method, string relativeUrl, object? body = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<string> SendRawAsync(string relativeUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class LibreNmsClientProbe
    {
        public LibreNmsClientProbe(ILibreNmsTransport transport) => System = new SystemApi(transport);

        public ISystemApi System { get; }
    }
}
