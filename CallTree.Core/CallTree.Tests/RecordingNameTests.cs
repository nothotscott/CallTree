using CallTree.Domain.Calls;
using CallTree.Domain.ValueObjects;
using Xunit;

namespace CallTree.Tests;

public class RecordingNameTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 14, 31, 5, TimeSpan.Zero);

    [Fact]
    public void Outbound_names_no_caller_because_the_caller_is_always_my_own_cell()
    {
        var name = RecordingName.Default(
            CallSource.Outbound, PhoneNumber.Parse("+13055559999"), "+13055559999", T0);

        Assert.Equal("Outbound call, Aug 22 2026 14:31", name);
    }

    [Fact]
    public void Inbound_names_a_nanp_caller_in_grouped_form()
    {
        var name = RecordingName.Default(
            CallSource.Inbound, PhoneNumber.Parse("+13055551234"), "3055551234", T0);

        Assert.Equal("Inbound call from (305) 555-1234, Aug 22 2026 14:31", name);
    }

    [Fact]
    public void A_non_nanp_number_keeps_its_e164_form()
    {
        var name = RecordingName.Default(
            CallSource.Inbound, PhoneNumber.Parse("+441632960123"), "+441632960123", T0);

        Assert.Equal("Inbound call from +441632960123, Aug 22 2026 14:31", name);
    }

    [Fact]
    public void An_unparsed_caller_id_is_used_verbatim()
    {
        var name = RecordingName.Default(CallSource.Inbound, null, " sip:probe@1.2.3.4 ", T0);

        Assert.Equal("Inbound call from sip:probe@1.2.3.4, Aug 22 2026 14:31", name);
    }

    [Fact]
    public void A_caller_that_identified_itself_with_nothing_is_left_out()
    {
        var name = RecordingName.Default(CallSource.Inbound, null, "   ", T0);

        Assert.Equal("Inbound call, Aug 22 2026 14:31", name);
    }

    [Fact]
    public void A_junk_caller_id_is_truncated_rather_than_swamping_the_name()
    {
        // Caller IDs are stored up to 256 characters and scanners fill them with anything.
        var junk = new string('x', 300);

        var name = RecordingName.Default(CallSource.Inbound, null, junk, T0);

        Assert.Equal($"Inbound call from {new string('x', RecordingName.MaxCallerLength)}, Aug 22 2026 14:31", name);
        Assert.True(name.Length <= RecordingName.MaxLength);
    }

    [Fact]
    public void The_date_is_utc_regardless_of_the_offset_it_arrives_with()
    {
        // Same instant as T0, stated in a different offset. The name is stored, so it must not depend
        // on whichever offset the caller happened to hold.
        var elsewhere = new DateTimeOffset(2026, 8, 22, 10, 31, 5, TimeSpan.FromHours(-4));

        Assert.Equal(RecordingName.Default(CallSource.Outbound, null, "", T0),
            RecordingName.Default(CallSource.Outbound, null, "", elsewhere));
    }

    [Fact]
    public void A_new_recording_is_named_from_the_call_that_owns_it()
    {
        var call = Call.Start(
            CallSource.Inbound,
            SourceClassification.Default,
            PhoneNumber.Parse("+13055551234"),
            "3055551234",
            "sip-call-id",
            T0);
        call.Answer(T0, requireScreening: false);

        var recording = call.StartRecording("2026-08/rec.wav", ChannelLayout.Mono, T0);

        Assert.Equal("Inbound call from (305) 555-1234, Aug 22 2026 14:31", recording.Name);
    }
}

public class RecordingRenameTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 22, 14, 31, 5, TimeSpan.Zero);

    private static Recording NewRecording()
    {
        var call = Call.Start(
            CallSource.Inbound, SourceClassification.Default, null, "3055551234", "sip-call-id", T0);
        call.Answer(T0, requireScreening: false);
        return call.StartRecording("2026-08/rec.wav", ChannelLayout.Mono, T0);
    }

    [Fact]
    public void Rename_trims_the_name()
    {
        var recording = NewRecording();

        recording.Rename("  Landlord re: lease renewal  ");

        Assert.Equal("Landlord re: lease renewal", recording.Name);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Rename_rejects_a_blank_name(string blank)
    {
        var recording = NewRecording();
        var before = recording.Name;

        Assert.Throws<ArgumentException>(() => recording.Rename(blank));
        Assert.Equal(before, recording.Name);
    }

    [Fact]
    public void Rename_rejects_a_name_past_the_column_length()
    {
        var recording = NewRecording();

        Assert.Throws<ArgumentException>(() => recording.Rename(new string('x', RecordingName.MaxLength + 1)));
    }

    [Fact]
    public void Rename_accepts_a_name_exactly_at_the_column_length()
    {
        var recording = NewRecording();
        var exact = new string('x', RecordingName.MaxLength);

        recording.Rename(exact);

        Assert.Equal(exact, recording.Name);
    }
}
