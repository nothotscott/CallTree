using CallTree.Application.Calls;
using CallTree.Application.Configuration;
using CallTree.Application.Messages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace CallTree.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<CallLifecycleService>();
        services.AddScoped<RecordingService>();

        // Scoped, and reached directly rather than through a command type: every message write arrives
        // on a provider webhook, which already has a scope. See the remarks on the service.
        services.AddScoped<MessageLifecycleService>();

        // Singleton: it holds only the scope factory and opens a scope per command.
        services.AddSingleton<ICallCommands, ScopedCallCommands>();

        // Bound here rather than in either consumer: Infrastructure resolves the database directory from
        // it and Telephony resolves the recordings root, and whichever bound it would be an odd
        // dependency for the other.
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        // Same reasoning, one layer along: the DID and the operator's mobile are read by both the SIP
        // stack and the messaging layer, which are siblings and cannot see each other. Note the section
        // is Telephony - the keys are unchanged, only the type that owns them is shared.
        services.Configure<LineOptions>(configuration.GetSection(LineOptions.SectionName));

        return services;
    }
}
