using System.Text.Json;
using DesktopNMS.Core.Configuration;
using DesktopNMS.Core.Intelligence;
using Xunit;

namespace DesktopNMS.Core.Tests;

/// <summary>Provider choices (#97) load from older files and copy without sharing state.</summary>
public sealed class IntelligenceProviderSettingsTests
{
    [Fact]
    public void Settings_saved_before_providers_start_on_ChatGPT()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""{"IntelligenceModel":"gpt-example"}""")!;

        Assert.Equal(AppSettings.IntelligenceProviderChatGpt, settings.IntelligenceProvider);
        Assert.Equal("gpt-example", settings.IntelligenceModel);
        Assert.Empty(settings.IntelligenceProviders);
    }

    [Fact]
    public void Clone_copies_each_provider_rather_than_sharing_it()
    {
        var settings = new AppSettings { IntelligenceProvider = AppSettings.IntelligenceProviderCodex };
        settings.IntelligenceProviders[AppSettings.IntelligenceProviderCodex] = new() { ExecutablePath = "codex", Model = "model-a", Effort = "high" };

        var copy = settings.Clone();
        copy.IntelligenceProviders[AppSettings.IntelligenceProviderCodex].Model = "model-b";

        Assert.Equal(AppSettings.IntelligenceProviderCodex, copy.IntelligenceProvider);
        Assert.Equal("model-a", settings.IntelligenceProviders[AppSettings.IntelligenceProviderCodex].Model);
        Assert.Equal("codex", copy.IntelligenceProviders[AppSettings.IntelligenceProviderCodex].ExecutablePath);
        Assert.Equal("high", copy.IntelligenceProviders[AppSettings.IntelligenceProviderCodex].Effort);
    }

    [Fact]
    public void A_chat_saved_before_providers_has_no_provider_or_session()
    {
        var chat = JsonSerializer.Deserialize<SavedIntelligenceChat>("""{"Title":"Older chat","Model":"gpt-example"}""")!;

        Assert.Null(chat.Provider);
        Assert.Null(chat.ProviderSession);
        Assert.Null(chat.Effort);
    }

    [Fact]
    public void A_chat_keeps_its_provider_and_session_through_a_save()
    {
        var chat = new SavedIntelligenceChat { Provider = AppSettings.IntelligenceProviderClaudeCode, ProviderSession = "session-1", Effort = "medium" };

        var copy = JsonSerializer.Deserialize<SavedIntelligenceChat>(JsonSerializer.SerializeToUtf8Bytes(chat))!;

        Assert.Equal(AppSettings.IntelligenceProviderClaudeCode, copy.Provider);
        Assert.Equal("session-1", copy.ProviderSession);
        Assert.Equal("medium", copy.Effort);
    }

    [Fact]
    public void A_provider_is_undecided_and_shows_every_model_until_one_is_hidden()
    {
        var chosen = JsonSerializer.Deserialize<IntelligenceProviderSettings>("""{"Model":"model-a"}""")!;

        Assert.Null(chosen.Added);
        Assert.Empty(chosen.HiddenModels);
        Assert.Null(chosen.Connection);
    }

    [Fact]
    public void Clone_copies_hidden_models_and_the_connection()
    {
        var chosen = new IntelligenceProviderSettings { Added = false, Connection = AppSettings.IntelligenceConnectionCodex };
        chosen.HiddenModels.Add("model-b");

        var copy = chosen.Clone();
        copy.HiddenModels.Add("model-c");

        Assert.False(copy.Added);
        Assert.Equal(AppSettings.IntelligenceConnectionCodex, copy.Connection);
        Assert.Equal(["model-b"], chosen.HiddenModels);
    }
}
