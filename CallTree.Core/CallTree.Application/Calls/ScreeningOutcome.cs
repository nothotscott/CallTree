namespace CallTree.Application.Calls;

/// <summary>How an inbound caller fared at the IVR spam gate.</summary>
public enum ScreeningOutcome
{
    /// <summary>The caller pressed the expected digit.</summary>
    Passed,

    /// <summary>The caller pressed something else.</summary>
    WrongDigit,

    /// <summary>The caller pressed nothing before the gate timed out.</summary>
    TimedOut,
}
