using System.Text.Json.Nodes;
using CallTree.Api.Settings;
using CallTree.Application.Configuration;
using CallTree.Messaging.Configuration;
using CallTree.Telephony.Configuration;
using Xunit;

namespace CallTree.Tests;

/// <summary>
/// The merge rules for the writable config file. The password cases matter most: getting them wrong
/// blanks a working trunk credential the first time an unrelated setting is saved.
/// </summary>
public class SettingsDocumentTests
{
    private static SettingsUpdate Update(
        string? password = null,
        string? authUsername = null,
        string? apiKey = null) => new()
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
        Messaging = new MessagingSettings
        {
            Enabled = true,
            PublicKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            MessagingProfileId = "profile-1",
            RequireSignature = true,
            SignatureToleranceSeconds = 300,
            NotifyOnFailure = true,
            ApiTimeoutSeconds = 10,
        },
        TrunkPassword = password,
        MessagingApiKey = apiKey,
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
        var options = new TelephonyOptions { PublicHost = "example.test", ScreeningTimeoutSeconds = 30 };
        var line = new LineOptions { DidNumber = "+15550002222", MyCellNumber = "+15550001111" };
        var settings = SettingsDocument.ToSettings(options, line);

        Assert.Equal(options, SettingsDocument.Apply(options, settings, outboundPin: null));

        // The DTO is one form covering two options types, so the other half has to survive the trip too
        // - the two numbers are on LineOptions because the messaging layer needs them as well.
        Assert.Equal(line, SettingsDocument.Apply(line, settings));
    }

    [Fact]
    public void Applying_telephony_settings_keeps_the_current_pin_when_none_is_given()
    {
        // Same trap as the trunk password: a save of any unrelated field must not turn off the gate on
        // the path that answers automatically and records.
        var current = new TelephonyOptions { OutboundPin = "4821" };
        var line = new LineOptions { DidNumber = "+15550002222" };

        var applied = SettingsDocument.Apply(
            current,
            SettingsDocument.ToSettings(current, line) with { ScreeningTimeoutSeconds = 20 },
            outboundPin: null);

        Assert.Equal("4821", applied.OutboundPin);
        Assert.Equal(20, applied.ScreeningTimeoutSeconds);
    }

    [Fact]
    public void An_empty_pin_is_an_explicit_instruction_to_turn_the_gate_off()
    {
        var current = new TelephonyOptions { OutboundPin = "4821" };

        var applied = SettingsDocument.Apply(
            current, SettingsDocument.ToSettings(current, new LineOptions()), outboundPin: "");

        Assert.Equal("", applied.OutboundPin);
    }

    [Fact]
    public void The_pin_is_only_written_to_the_document_when_one_is_supplied()
    {
        var document = new JsonObject
        {
            ["Telephony"] = new JsonObject { ["OutboundPin"] = "4821" },
        };

        SettingsDocument.Apply(document, Update());

        Assert.Equal("4821", (string?)document["Telephony"]!["OutboundPin"]);
    }

    [Fact]
    public void Applying_trunk_settings_keeps_the_current_password_when_none_is_given()
    {
        var current = new TrunkOptions { Host = "old.example.test", Password = "already-set" };

        var applied = SettingsDocument.Apply(current, new TrunkSettings { Host = "new.example.test" }, password: null);

        Assert.Equal("new.example.test", applied.Host);
        Assert.Equal("already-set", applied.Password);
    }

    [Fact]
    public void Does_not_introduce_an_api_key_when_none_is_supplied()
    {
        // Third instance of the same rule, and the same failure if it is broken: an empty value here
        // would override a key coming from user secrets or the environment, and the next inbound text
        // would be recorded and never forwarded.
        var document = SettingsDocument.Apply([], Update(apiKey: null));

        Assert.False(document["Messaging"]!.AsObject().ContainsKey("ApiKey"));
        Assert.True((bool)document["Messaging"]!["Enabled"]!);
        Assert.Equal("profile-1", (string?)document["Messaging"]!["MessagingProfileId"]);
    }

    [Fact]
    public void Writes_the_api_key_when_one_is_supplied()
    {
        var document = SettingsDocument.Apply([], Update(apiKey: "KEY123"));

        Assert.Equal("KEY123", (string?)document["Messaging"]!["ApiKey"]);
    }

    [Fact]
    public void Applying_messaging_settings_keeps_the_current_api_key_when_none_is_given()
    {
        var current = new MessagingOptions { ApiKey = "already-set", Enabled = false };

        var applied = SettingsDocument.Apply(
            current,
            SettingsDocument.ToSettings(current) with { Enabled = true },
            apiKey: null);

        Assert.True(applied.Enabled);
        Assert.Equal("already-set", applied.ApiKey);
    }

    [Fact]
    public void Round_trips_messaging_options_through_the_settings_shape()
    {
        var options = new MessagingOptions
        {
            Enabled = true,
            PublicKey = "MDEyMzQ1Njc4OWFiY2RlZjAxMjM0NTY3ODlhYmNkZWY=",
            MessagingProfileId = "profile-1",
            RequireSignature = false,
            SignatureToleranceSeconds = 120,
            NotifyOnFailure = false,
            ApiTimeoutSeconds = 20,
        };

        var applied = SettingsDocument.Apply(options, SettingsDocument.ToSettings(options), apiKey: null);

        Assert.Equal(options, applied);
    }
}
