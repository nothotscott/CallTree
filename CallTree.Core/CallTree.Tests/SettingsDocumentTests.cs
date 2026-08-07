using System.Text.Json.Nodes;
using CallTree.Api.Settings;
using CallTree.Telephony.Configuration;
using Xunit;

namespace CallTree.Tests;

/// <summary>
/// The merge rules for the writable config file. The password cases matter most: getting them wrong
/// blanks a working trunk credential the first time an unrelated setting is saved.
/// </summary>
public class SettingsDocumentTests
{
    private static SettingsUpdate Update(string? password = null, string? authUsername = null) => new()
    {
        Telephony = new TelephonySettings
        {
            MyCellNumber = "+15550001111",
            DidNumber = "+15550002222",
            PublicHost = "example.test",
            SipListenPort = 5060,
            ListenOnTcp = true,
            RtpPortStart = 10000,
            RtpPortEnd = 10100,
            TraceSip = true,
            ScreeningDigit = 1,
            ScreeningTimeoutSeconds = 12,
        },
        Trunk = new TrunkSettings
        {
            Host = "sip.example.test",
            Port = 5060,
            Username = "user",
            AuthUsername = authUsername,
            RegistrationExpirySeconds = 120,
        },
        TrunkPassword = password,
    };

    [Fact]
    public void Writes_both_sections_into_an_empty_document()
    {
        var document = SettingsDocument.Apply([], Update());

        Assert.Equal("+15550002222", (string?)document["Telephony"]!["DidNumber"]);
        Assert.True((bool)document["Telephony"]!["TraceSip"]!);
        Assert.Equal(10100, (int)document["Telephony"]!["RtpPortEnd"]!);
        Assert.Equal("sip.example.test", (string?)document["Trunk"]!["Host"]);
    }

    [Fact]
    public void Leaves_the_password_alone_when_none_is_supplied()
    {
        var document = new JsonObject
        {
            ["Trunk"] = new JsonObject { ["Password"] = "already-set" },
        };

        SettingsDocument.Apply(document, Update(password: null));

        Assert.Equal("already-set", (string?)document["Trunk"]!["Password"]);
    }

    [Fact]
    public void Does_not_introduce_a_password_key_when_none_is_supplied()
    {
        // A key present with an empty value would override a password coming from user secrets or the
        // environment, both of which sit outside this file.
        var document = SettingsDocument.Apply([], Update(password: null));

        Assert.False(document["Trunk"]!.AsObject().ContainsKey("Password"));
    }

    [Fact]
    public void Writes_the_password_when_one_is_supplied()
    {
        var document = SettingsDocument.Apply([], Update(password: "s3cret"));

        Assert.Equal("s3cret", (string?)document["Trunk"]!["Password"]);
    }

    [Fact]
    public void Removes_a_blank_auth_username_rather_than_writing_an_empty_one()
    {
        var document = new JsonObject
        {
            ["Trunk"] = new JsonObject { ["AuthUsername"] = "old" },
        };

        SettingsDocument.Apply(document, Update(authUsername: "   "));

        Assert.False(document["Trunk"]!.AsObject().ContainsKey("AuthUsername"));
    }

    [Fact]
    public void Preserves_content_the_endpoint_does_not_manage()
    {
        var document = new JsonObject
        {
            ["Storage"] = new JsonObject { ["RecordingsRoot"] = "/srv/recordings" },
            ["Telephony"] = new JsonObject { ["PromptsRoot"] = "/opt/prompts" },
        };

        SettingsDocument.Apply(document, Update());

        Assert.Equal("/srv/recordings", (string?)document["Storage"]!["RecordingsRoot"]);
        Assert.Equal("/opt/prompts", (string?)document["Telephony"]!["PromptsRoot"]);
    }

    [Fact]
    public void Round_trips_options_through_the_settings_shape()
    {
        var options = new TelephonyOptions { DidNumber = "+15550002222", ScreeningTimeoutSeconds = 30 };

        var applied = SettingsDocument.Apply(options, SettingsDocument.ToSettings(options));

        Assert.Equal(options, applied);
    }

    [Fact]
    public void Applying_trunk_settings_keeps_the_current_password_when_none_is_given()
    {
        var current = new TrunkOptions { Host = "old.example.test", Password = "already-set" };

        var applied = SettingsDocument.Apply(current, new TrunkSettings { Host = "new.example.test" }, password: null);

        Assert.Equal("new.example.test", applied.Host);
        Assert.Equal("already-set", applied.Password);
    }
}
