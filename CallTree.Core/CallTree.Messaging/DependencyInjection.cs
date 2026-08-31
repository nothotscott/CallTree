using CallTree.Messaging.Configuration;
using CallTree.Messaging.Telnyx;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CallTree.Messaging;

public static class DependencyInjection
{
    public static IServiceCollection AddMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<MessagingOptions>(configuration.GetSection(MessagingOptions.SectionName));

        // Singleton: it holds only the options monitor and does no per-request work.
        services.AddSingleton<TelnyxSignatureVerifier>();

        // A typed client so the handler pipeline and its socket pooling are the host's business, not
        // ours. Neither the credential nor the timeout is configured here - both are read per request,
        // because the settings UI can change either while the process is running.
        services.AddHttpClient<TelnyxClient>();

        // Scoped: it drives MessageLifecycleService, which owns a scoped DbContext. Every message write
        // arrives on a provider webhook, so there is always a request scope to live in - no scope
        // factory, unlike the telephony side.
        services.AddScoped<SmsRelayService>();

        return services;
    }
}
