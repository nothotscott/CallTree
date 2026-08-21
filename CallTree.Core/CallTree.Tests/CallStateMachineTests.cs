using CallTree.Domain.Calls;
using CallTree.Domain.ValueObjects;
using Xunit;

namespace CallTree.Tests;

public class CallStateMachineTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 14, 12, 0, 0, TimeSpan.Zero);
    private static readonly PhoneNumber Caller = PhoneNumber.Parse("+13055551234");
    private static readonly PhoneNumber MyCell = PhoneNumber.Parse("+13055559999");

    private static Call StartInbound() =>
        Call.Start(CallSource.Inbound, SourceClassification.Default, Caller, "+13055551234", "sip-call-id-1", T0);

    private static Call StartOutboundSource() =>
        Call.Start(CallSource.Outbound, SourceClassification.CallerIdMatch, MyCell, "+13055559999", "sip-call-id-2", T0);

    [Fact]
    public void Inbound_happy_path_walks_full_state_machine()
    {
        var call = StartInbound();
        Assert.Equal(CallStatus.Ringing, call.Status);

        call.Answer(T0.AddSeconds(2), requireScreening: true);
        Assert.Equal(CallStatus.Screening, call.Status);

        call.BeginDialing(MyCell, "sip-call-id-out", T0.AddSeconds(10));
        Assert.Equal(CallStatus.Dialing, call.Status);
        Assert.NotNull(call.OutboundLeg);

        call.Bridge(T0.AddSeconds(15));
        Assert.Equal(CallStatus.InProgress, call.Status);
        Assert.Equal(T0.AddSeconds(15), call.OutboundLeg!.AnsweredAt);

        call.StartRecording("2026/07/rec.wav", ChannelLayout.StereoPerLeg, T0.AddSeconds(15));
        Assert.NotNull(call.Recording);

        call.Complete(T0.AddMinutes(5));
        Assert.Equal(CallStatus.Completed, call.Status);
        Assert.True(call.IsTerminal);
        Assert.All(call.Legs, leg => Assert.NotNull(leg.EndedAt));
    }

    [Fact]
    public void CompleteScreening_ends_a_passed_call_without_bridging()
    {
        var call = StartInbound();
        call.Answer(T0.AddSeconds(2), requireScreening: true);

        call.CompleteScreening(T0.AddSeconds(9), "screening passed (pressed 1)");

        Assert.Equal(CallStatus.Completed, call.Status);
        Assert.Equal("screening passed (pressed 1)", call.TerminationReason);
        Assert.True(call.IsTerminal);
        Assert.All(call.Legs, leg => Assert.NotNull(leg.EndedAt));
    }

    [Fact]
    public void CompleteScreening_is_only_legal_while_screening()
    {
        var call = StartOutboundSource();
        call.Answer(T0.AddSeconds(1), requireScreening: false);

        Assert.Throws<InvalidOperationException>(() => call.CompleteScreening(T0.AddSeconds(5), "nope"));
    }

    [Fact]
    public void Outbound_source_answer_goes_straight_to_in_progress()
    {
        var call = StartOutboundSource();

        call.Answer(T0.AddSeconds(1), requireScreening: false);

        Assert.Equal(CallStatus.InProgress, call.Status);
    }

    [Fact]
    public void Outbound_source_with_a_pin_is_screened_before_it_proceeds()
    {
        var call = StartOutboundSource();

        call.Answer(T0.AddSeconds(1), requireScreening: true);
        Assert.Equal(CallStatus.Screening, call.Status);

        call.PassScreening(T0.AddSeconds(6));

        Assert.Equal(CallStatus.InProgress, call.Status);
        Assert.Equal(T0.AddSeconds(1), call.AnsweredAt);
    }

    [Fact]
    public void A_failed_pin_lands_in_screened_out_rather_than_completed()
    {
        // The point of gating the Outbound path through Screening: a spoofed caller ID that fails the
        // PIN has to be distinguishable in the call log from a call that simply finished.
        var call = StartOutboundSource();
        call.Answer(T0.AddSeconds(1), requireScreening: true);

        call.ScreenOut(T0.AddSeconds(14), "wrong PIN");

        Assert.Equal(CallStatus.ScreenedOut, call.Status);
    }

    [Fact]
    public void PassScreening_is_only_legal_while_screening()
    {
        var call = StartOutboundSource();
        call.Answer(T0.AddSeconds(1), requireScreening: false);

        Assert.Throws<InvalidOperationException>(() => call.PassScreening(T0.AddSeconds(5)));
    }

    [Fact]
    public void ScreenOut_only_allowed_while_screening()
    {
        var call = StartInbound();
        call.Answer(T0.AddSeconds(1), requireScreening: true);

        call.ScreenOut(T0.AddSeconds(30), "no digit pressed");

        Assert.Equal(CallStatus.ScreenedOut, call.Status);
    }

    [Fact]
    public void MarkMissed_records_unanswered_outbound_leg()
    {
        var call = StartInbound();
        call.Answer(T0.AddSeconds(1), requireScreening: true);
        call.BeginDialing(MyCell, "sip-call-id-out", T0.AddSeconds(5));

        call.MarkMissed(T0.AddSeconds(35), "cell did not answer");

        Assert.Equal(CallStatus.Missed, call.Status);
        Assert.Null(call.OutboundLeg!.AnsweredAt);
    }

    [Fact]
    public void Invalid_transitions_throw()
    {
        var call = StartInbound();

        Assert.Throws<InvalidOperationException>(() => call.Bridge(T0));
        Assert.Throws<InvalidOperationException>(() => call.Complete(T0));
        Assert.Throws<InvalidOperationException>(() => call.BeginDialing(MyCell, "x", T0));
        Assert.Throws<InvalidOperationException>(() => call.StartRecording("x.wav", ChannelLayout.Mono, T0));

        call.Answer(T0, requireScreening: true);
        Assert.Throws<InvalidOperationException>(() => call.Answer(T0, requireScreening: true));
    }

    [Fact]
    public void Fail_works_from_any_live_state_but_not_after_termination()
    {
        var call = StartInbound();
        call.Fail(T0.AddSeconds(1), "SIP error");

        Assert.Equal(CallStatus.Failed, call.Status);
        Assert.Throws<InvalidOperationException>(() => call.Fail(T0.AddSeconds(2), "again"));
    }

    [Fact]
    public void Recording_finalization_is_one_shot()
    {
        var call = StartOutboundSource();
        call.Answer(T0, requireScreening: false);
        var recording = call.StartRecording("rec.wav", ChannelLayout.Mono, T0);

        recording.MarkFinalized(120.5, 1_928_000, T0.AddMinutes(2));

        Assert.NotNull(recording.FinalizedAt);
        Assert.Throws<InvalidOperationException>(() => recording.MarkFinalized(1, 1, T0.AddMinutes(3)));
    }

    [Fact]
    public void FinalizeRecording_tolerates_being_called_twice_or_with_no_recording()
    {
        // Both the hangup path and the error path reach it, and losing the call's terminal state over a
        // duplicate bookkeeping call would be the worse failure.
        var call = StartOutboundSource();
        call.Answer(T0, requireScreening: false);

        call.FinalizeRecording(1, 1, T0.AddSeconds(30));
        Assert.Null(call.Recording);

        call.StartRecording("rec.wav", ChannelLayout.Mono, T0);
        call.FinalizeRecording(30, 480_000, T0.AddSeconds(30));
        call.FinalizeRecording(99, 99, T0.AddSeconds(31));

        Assert.Equal(30, call.Recording!.DurationSeconds);
        Assert.Equal(T0.AddSeconds(30), call.Recording.FinalizedAt);
    }

    [Fact]
    public void Transitions_raise_domain_events()
    {
        var call = StartInbound();
        call.Answer(T0, requireScreening: true);
        call.BeginDialing(MyCell, "x", T0);
        call.Bridge(T0);
        call.Complete(T0);

        Assert.Collection(call.DomainEvents,
            e => Assert.IsType<CallStarted>(e),
            e => Assert.IsType<CallAnswered>(e),
            e => Assert.IsType<CallBridged>(e),
            e => Assert.IsType<CallEnded>(e));
    }
}
