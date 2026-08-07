using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallTree.Telephony.Configuration;

/// <summary>The log category whole SIP messages are written to.</summary>
public static class SipTrace
{
    public const string CategoryName = "CallTree.Telephony.SipTrace";
}

/// <summary>
/// Raises <see cref="SipTrace.CategoryName"/> to <see cref="LogLevel.Trace"/> whenever
/// <c>Telephony:TraceSip</c> is on, so the switch lives in exactly one place.
/// </summary>
/// <remarks>
/// SIP messages are logged at Trace because that is what they are — the whole wire, unfiltered. That
/// used to mean the feature was bound to two settings that had to agree: turning on <c>TraceSip</c>
/// without also lowering the category's level produced no output at all, which reads exactly like a
/// packet that never arrived. This rule is appended after the ones built from the <c>Logging</c>
/// section, and rule selection prefers the last match of equal specificity, so it wins over an explicit
/// entry for the same category. Setting the category's level directly still works when
/// <c>TraceSip</c> is off — this only ever adds a rule.
///
/// It is registered as an <see cref="IConfigureOptions{TOptions}"/> rather than a one-off
/// <c>AddFilter</c> so that it is re-evaluated whenever configuration reloads: filter options are
/// recomputed on the <c>Logging</c> section's change token, and the logger factory refreshes its
/// filters in response. That is what lets the settings UI turn tracing on mid-call without a restart.
/// </remarks>
internal sealed class SipTraceLogLevel(IConfiguration configuration) : IConfigureOptions<LoggerFilterOptions>
{
    public void Configure(LoggerFilterOptions options)
    {
        var traceSip = configuration
            .GetSection(TelephonyOptions.SectionName)
            .GetValue<bool>(nameof(TelephonyOptions.TraceSip));

        if (!traceSip)
        {
            return;
        }

        options.Rules.Add(new LoggerFilterRule(
            providerName: null,
            categoryName: SipTrace.CategoryName,
            logLevel: LogLevel.Trace,
            filter: null));
    }
}
