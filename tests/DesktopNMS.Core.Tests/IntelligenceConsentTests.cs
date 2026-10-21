using System.Text.Json;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

public sealed class IntelligenceConsentTests
{
    [Fact]
    public void A_notice_accepted_before_web_search_is_asked_again_and_search_waits()
    {
        // Settings saved by an earlier version: consent given, no version.
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"IntelligenceConsentGiven":true,"IntelligenceWebSearchEnabled":true}""")!;

        Assert.False(IntelligenceConsent.IsCurrent(settings));
        Assert.False(IntelligenceConsent.AllowsWebSearch(settings));

        IntelligenceConsent.Accept(settings);

        Assert.True(IntelligenceConsent.IsCurrent(settings));
        Assert.True(IntelligenceConsent.AllowsWebSearch(settings));
        Assert.Equal(IntelligenceConsent.CurrentVersion, settings.Clone().IntelligenceConsentVersion);
    }

    [Fact]
    public void Web_search_turned_off_stays_off_after_consent()
    {
        var settings = new AppSettings { IntelligenceWebSearchEnabled = false };
        IntelligenceConsent.Accept(settings);

        Assert.False(IntelligenceConsent.AllowsWebSearch(settings));
    }
}