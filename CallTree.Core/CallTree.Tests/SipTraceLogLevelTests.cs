using CallTree.Telephony;
using CallTree.Telephony.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CallTree.Tests;

/// <summary>
/// Telephony:TraceSip is the only switch for SIP wire tracing. It used to need a matching
/// Logging:LogLevel entry, and setting one without the other produced no output at all — which looks
/// exactly like a packet that never arrived, the very thing tracing is turned on to rule out.
/// </summary>
public class SipTraceLogLevelTests
{
    private static (ILogger Logger, IConfigurationRoot Configuration, ServiceProvider Provider) Build(bool traceSip)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information",
                [$"{TelephonyOptions.SectionName}:{nameof(TelephonyOptions.TraceSip)}"] = traceSip ? "true" : "false",
            })
            .Build();

        var services = new ServiceCollection();

        // Logging configuration first, telephony second - the same order the host uses, and the reason
        // the TraceSip rule wins over an explicit level for the same category.
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddProvider(new PassThroughProvider());
        });
        services.AddTelephony(configuration);

        var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(SipTrace.CategoryName);
        return (logger, configuration, provider);
    }

    [Fact]
    public void Trace_sip_alone_enables_the_trace_category()
    {
        var (logger, _, provider) = Build(traceSip: true);
        using (provider)
        {
            Assert.True(logger.IsEnabled(LogLevel.Trace));
        }
    }

    [Fact]
    public void The_category_stays_at_the_default_level_when_trace_sip_is_off()
    {
        var (logger, _, provider) = Build(traceSip: false);
        using (provider)
        {
            Assert.False(logger.IsEnabled(LogLevel.Trace));
            Assert.True(logger.IsEnabled(LogLevel.Information));
        }
    }

    [Fact]
    public void Other_categories_are_unaffected()
    {
        var (_, _, provider) = Build(traceSip: true);
        using (provider)
        {
            var other = provider.GetRequiredService<ILoggerFactory>().CreateLogger("CallTree.Telephony");
            Assert.False(other.IsEnabled(LogLevel.Trace));
        }
    }

    [Fact]
    public void Follows_a_configuration_reload_without_a_restart()
    {
        // This is what lets tracing be turned on from the settings UI during a misbehaving call,
        // instead of a restart that drops the registration and the call being investigated.
        var (logger, configuration, provider) = Build(traceSip: false);
        using (provider)
        {
            Assert.False(logger.IsEnabled(LogLevel.Trace));

            configuration[$"{TelephonyOptions.SectionName}:{nameof(TelephonyOptions.TraceSip)}"] = "true";
            configuration.Reload();

            Assert.True(logger.IsEnabled(LogLevel.Trace));

            configuration[$"{TelephonyOptions.SectionName}:{nameof(TelephonyOptions.TraceSip)}"] = "false";
            configuration.Reload();

            Assert.False(logger.IsEnabled(LogLevel.Trace));
        }
    }

    [Fact]
    public void Wins_over_an_explicit_level_for_the_same_category()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Logging:LogLevel:Default"] = "Information",
                [$"Logging:LogLevel:{SipTrace.CategoryName}"] = "Warning",
                [$"{TelephonyOptions.SectionName}:{nameof(TelephonyOptions.TraceSip)}"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(logging =>
        {
            logging.AddConfiguration(configuration.GetSection("Logging"));
            logging.AddProvider(new PassThroughProvider());
        });
        services.AddTelephony(configuration);

        using var provider = services.BuildServiceProvider();
        var logger = provider.GetRequiredService<ILoggerFactory>().CreateLogger(SipTrace.CategoryName);

        Assert.True(logger.IsEnabled(LogLevel.Trace));
    }

    /// <summary>A provider that enables everything, so what is asserted is the filter and nothing else.</summary>
    private sealed class PassThroughProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new AlwaysEnabled();

        public void Dispose()
        {
        }

        private sealed class AlwaysEnabled : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
            }
        }
    }
}
