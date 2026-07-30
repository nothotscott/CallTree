using CallTree.Telephony.Audio;
using CallTree.Telephony.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CallTree.Telephony;

public static class DependencyInjection
{
    public static IServiceCollection AddTelephony(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TrunkOptions>(configuration.GetSection(TrunkOptions.SectionName));
        services.Configure<TelephonyOptions>(configuration.GetSection(TelephonyOptions.SectionName));
        services.AddSingleton<PromptLibrary>();
        services.AddHostedService<TelephonyBackgroundService>();

        return services;
    }
}
