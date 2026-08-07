using CallTree.Telephony.Audio;
using CallTree.Telephony.Configuration;
using CallTree.Telephony.Status;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CallTree.Telephony;

public static class DependencyInjection
{
    public static IServiceCollection AddTelephony(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TrunkOptions>(configuration.GetSection(TrunkOptions.SectionName));
        services.Configure<TelephonyOptions>(configuration.GetSection(TelephonyOptions.SectionName));

        // Registered after the rules built from the Logging section, which is what lets Telephony:TraceSip
        // win over an explicit level for the SIP trace category. Configuration is captured rather than
        // resolved so this does not depend on IConfiguration being in the container.
        services.AddSingleton<IConfigureOptions<LoggerFilterOptions>>(new SipTraceLogLevel(configuration));

        services.AddSingleton<TelephonySettingsWatcher>();
        services.AddSingleton<TelephonyStatus>();
        services.AddSingleton<PromptLibrary>();
        services.AddHostedService<TelephonyBackgroundService>();

        return services;
    }
}
