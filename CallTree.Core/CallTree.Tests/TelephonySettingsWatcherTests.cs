using CallTree.Telephony;
using CallTree.Telephony.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace CallTree.Tests;

/// <summary>
/// Which settings the running SIP stack can pick up and which need a restart. The distinction is the
/// difference between a settings screen that tells the truth and one that accepts a new SIP port while
/// the old one stays bound.
/// </summary>
public class TelephonySettingsWatcherTests
{
    private static TelephonySettingsWatcher Started(TelephonyOptions telephony, TrunkOptions trunk)
    {
        var watcher = new TelephonySettingsWatcher(new StaticMonitor<TelephonyOptions>(telephony), new StaticMonitor<TrunkOptions>(trunk));
        watcher.CaptureStartupSnapshot(telephony, trunk);
        return watcher;
    }

    [Fact]
    public void Reports_nothing_before_the_stack_has_started()
    {
        var watcher = new TelephonySettingsWatcher(
            new StaticMonitor<TelephonyOptions>(new TelephonyOptions()),
            new StaticMonitor<TrunkOptions>(new TrunkOptions()));

        Assert.False(watcher.HasStarted);
        Assert.Empty(watcher.PendingRestartKeysFor(new TelephonyOptions { SipListenPort = 5080 }, new TrunkOptions()));
    }

    [Fact]
    public void Live_settings_never_need_a_restart()
    {
        var watcher = Started(new TelephonyOptions(), new TrunkOptions());

        // The DID and the mobile are not here any more: they live on LineOptions, which the messaging
        // layer shares, and neither has ever been startup-only.
        var candidate = new TelephonyOptions
        {
            TraceSip = true,
            ScreeningDigit = 2,
            ScreeningTimeoutSeconds = 30,
            DialTimeoutSeconds = 40,
        };

        Assert.Empty(watcher.PendingRestartKeysFor(candidate, new TrunkOptions()));
    }

    [Fact]
    public void Rebinding_a_socket_needs_a_restart()
    {
        var watcher = Started(new TelephonyOptions(), new TrunkOptions());

        var pending = watcher.PendingRestartKeysFor(
            new TelephonyOptions { SipListenPort = 5080, RtpPortEnd = 10200 },
            new TrunkOptions());

        Assert.Equal(["Telephony:RtpPortEnd", "Telephony:SipListenPort"], pending.Order());
    }

    [Fact]
    public void Every_trunk_change_needs_a_restart_including_the_password()
    {
        var watcher = Started(new TelephonyOptions(), new TrunkOptions { Host = "old.example.test", Password = "old" });

        var pending = watcher.PendingRestartKeysFor(
            new TelephonyOptions(),
            new TrunkOptions { Host = "new.example.test", Password = "new" });

        Assert.Contains("Trunk:Host", pending);
        Assert.Contains("Trunk:Password", pending);
    }

    [Fact]
    public void A_null_and_a_blank_auth_username_are_the_same_setting()
    {
        // The config file drops the key rather than writing an empty string, so the bound value comes
        // back as null. That must not read as a change every time the settings are saved.
        var watcher = Started(new TelephonyOptions(), new TrunkOptions { AuthUsername = null });

        Assert.Empty(watcher.PendingRestartKeysFor(new TelephonyOptions(), new TrunkOptions { AuthUsername = "" }));
    }

    private sealed class StaticMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value;

        public T Get(string? name) => value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
