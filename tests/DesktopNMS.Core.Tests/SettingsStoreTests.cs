using DesktopNMS.Core.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Always against a temporary file - never the real per-user settings.</summary>
public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "dashynms-tests-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_folder);
    }

    private string SettingsFile => Path.Combine(_folder, "settings.json");

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
    public void Save_writes_the_file_even_with_nothing_listening()
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        store.Current.Theme = AppTheme.Light;

        store.Save();

        Assert.True(File.Exists(SettingsFile));
        var reloaded = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        Assert.Equal(AppTheme.Light, reloaded.Current.Theme);
    }

    [Fact]
    public void Save_raises_Changed_after_writing()
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        var fileExistedWhenRaised = false;
        store.Changed += (_, _) => fileExistedWhenRaised = File.Exists(SettingsFile);

        store.Save();

        Assert.True(fileExistedWhenRaised);
    }

    [Fact]
    public void A_fresh_install_gets_the_shipped_defaults()
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);

        Assert.True(store.Current.JigglePhysicsOnMaps);
        Assert.Equal(AppTheme.System, store.Current.Theme);
        Assert.True(store.Current.ServerTimestampsAreUtc);
        Assert.True(store.Current.ShowRecentlyViewedDevices);
        Assert.Equal(5, store.Current.RecentlyViewedDeviceCount);
        Assert.Equal(DeviceNameStyle.SysName, store.Current.DeviceNameStyle);
        Assert.Equal(StartupTab.Dashboard, store.Current.StartupTab);
        Assert.True(store.Current.DownloadUpdatesAutomatically);
        Assert.False(store.Current.IntelligenceSingleChatMode);
        Assert.Equal(DiagnosticsSharingPreference.Undecided, store.Current.DiagnosticsSharing);
    }

    [Fact]
    public void An_existing_file_keeps_the_values_it_was_saved_with()
    {
        File.WriteAllText(SettingsFile, """
            {
              "jigglePhysicsOnMaps": false,
              "theme": "Dark",
              "serverTimestampsAreUtc": false,
              "recentlyViewedDeviceCount": 10,
              "deviceNameStyle": "Hostname"
            }
            """);

        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);

        Assert.False(store.Current.JigglePhysicsOnMaps);
        Assert.Equal(AppTheme.Dark, store.Current.Theme);
        Assert.False(store.Current.ServerTimestampsAreUtc);
        Assert.Equal(10, store.Current.RecentlyViewedDeviceCount);
        Assert.Equal(DeviceNameStyle.Hostname, store.Current.DeviceNameStyle);
        Assert.True(store.Current.DownloadUpdatesAutomatically);
        Assert.False(store.Current.IntelligenceSingleChatMode);
        Assert.Equal(DiagnosticsSharingPreference.Undecided, store.Current.DiagnosticsSharing);
    }

    [Fact]
    public void A_settings_file_without_a_diagnostics_choice_keeps_the_prompt_pending()
    {
        File.WriteAllText(SettingsFile, "{}");

        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);

        Assert.Equal(DiagnosticsSharingPreference.Undecided, store.Current.DiagnosticsSharing);
    }

    [Theory]
    [InlineData(DiagnosticsSharingPreference.Undecided)]
    [InlineData(DiagnosticsSharingPreference.None)]
    [InlineData(DiagnosticsSharingPreference.ErrorsOnly)]
    [InlineData(DiagnosticsSharingPreference.UsageOnly)]
    [InlineData(DiagnosticsSharingPreference.ErrorsAndUsage)]
    public void Diagnostics_sharing_choice_survives_save_and_reload(DiagnosticsSharingPreference choice)
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        store.Current.DiagnosticsSharing = choice;

        store.Save();

        var reloaded = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        Assert.Equal(choice, reloaded.Current.DiagnosticsSharing);
    }

    [Theory]
    [InlineData(DiagnosticsSharingPreference.Undecided)]
    [InlineData(DiagnosticsSharingPreference.None)]
    [InlineData(DiagnosticsSharingPreference.ErrorsOnly)]
    [InlineData(DiagnosticsSharingPreference.UsageOnly)]
    [InlineData(DiagnosticsSharingPreference.ErrorsAndUsage)]
    public void Editing_a_clone_keeps_diagnostics_sharing_choice_until_changed(DiagnosticsSharingPreference choice)
    {
        var settings = new AppSettings { DiagnosticsSharing = choice };

        var edited = settings.Clone();

        Assert.Equal(choice, edited.DiagnosticsSharing);
        edited.DiagnosticsSharing = choice == DiagnosticsSharingPreference.None
            ? DiagnosticsSharingPreference.ErrorsAndUsage
            : DiagnosticsSharingPreference.None;
        Assert.Equal(choice, settings.DiagnosticsSharing);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Automatic_update_downloads_survive_save_and_reload(bool enabled)
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        store.Current.DownloadUpdatesAutomatically = enabled;

        store.Save();

        var reloaded = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        Assert.Equal(enabled, reloaded.Current.DownloadUpdatesAutomatically);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Editing_a_clone_keeps_automatic_downloads_until_changed(bool enabled)
    {
        var settings = new AppSettings { DownloadUpdatesAutomatically = enabled };

        var edited = settings.Clone();

        Assert.Equal(enabled, edited.DownloadUpdatesAutomatically);
        edited.DownloadUpdatesAutomatically = !enabled;
        Assert.Equal(enabled, settings.DownloadUpdatesAutomatically);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Single_chat_mode_survives_save_and_reload(bool enabled)
    {
        var store = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        store.Current.IntelligenceSingleChatMode = enabled;

        store.Save();

        var reloaded = new SettingsStore(NullLogger<SettingsStore>.Instance, SettingsFile);
        Assert.Equal(enabled, reloaded.Current.IntelligenceSingleChatMode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Editing_a_clone_keeps_single_chat_mode_until_changed(bool enabled)
    {
        var settings = new AppSettings { IntelligenceSingleChatMode = enabled };

        var edited = settings.Clone();

        Assert.Equal(enabled, edited.IntelligenceSingleChatMode);
        edited.IntelligenceSingleChatMode = !enabled;
        Assert.Equal(enabled, settings.IntelligenceSingleChatMode);
    }
}
