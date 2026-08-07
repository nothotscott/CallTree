using System.Text.Json.Serialization;
using CallTree.Api.Settings;
using CallTree.Application;
using CallTree.Infrastructure;
using CallTree.Infrastructure.Configuration;
using CallTree.Infrastructure.Persistence;
using CallTree.Telephony;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Scalar.AspNetCore;

namespace CallTree.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            var configFile = AddWritableConfiguration(builder);
            builder.Services.AddSingleton(configFile);

            builder.Services
                .AddControllers()
                .AddJsonOptions(options =>
                {
                    // Enums go over the wire as names, matching how they are persisted. Numbers would
                    // silently change meaning the moment a member is inserted into the middle of an enum.
                    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                });
            builder.Services.AddOpenApi();
            builder.Services.AddHealthChecks();

            builder.Services.AddApplication();
            builder.Services.AddInfrastructure(builder.Configuration);
            builder.Services.AddTelephony(builder.Configuration);

            var app = builder.Build();

            EnsureDatabase(app);

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();

                app.MapScalarApiReference();

                // Not permanent: a 301 here is cached by the browser indefinitely, and "/" belongs to
                // the SPA outside development - a cached redirect would send you to /scalar forever.
                app.MapGet("/", () => Results.LocalRedirect("/scalar")).ExcludeFromDescription();
            }

            // The built SvelteKit UI is copied into wwwroot by deploy/Dockerfile, so one container
            // serves both halves on one port and the browser sees a single origin - no CORS anywhere.
            // In development wwwroot is empty and the UI runs on Vite's dev server instead, which
            // proxies /api back here; nothing below does any harm in that case.
            app.UseStaticFiles();

            app.UseAuthorization();

            app.MapControllers();
            app.MapHealthChecks("/health");

            // Unknown API paths must 404 rather than fall through to the SPA shell below. Without
            // this a typo'd or not-yet-implemented endpoint answers 200 with an HTML document, so
            // callers see success and then fail confusingly at JSON.parse. Controller routes are more
            // specific than this catch-all, so they still match first.
            app.Map("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound))
                .ExcludeFromDescription();

            // A single-page app owns its own routing, so any other unmatched GET returns the shell
            // rather than a 404 - otherwise deep links like /calls only work when arrived at from
            // inside the app.
            app.MapFallbackToFile("index.html");

            app.Run();
        }

        /// <summary>
        /// Inserts the writable config file the settings UI edits, between the appsettings files and
        /// the environment variables: appsettings (what ships) is overridden by the config file (what
        /// this instance was told), which is overridden by the environment (what this container run
        /// demands). Anything else the host added after the environment - the command line - still wins.
        /// </summary>
        /// <remarks>
        /// Ordering is done by finding the environment-variable source rather than by index, because the
        /// host decides how many sources precede it and that has changed between releases. It has to be
        /// the *unprefixed* one: the host adds ASPNETCORE_ and DOTNET_ sources of the same type before
        /// the appsettings files, and inserting ahead of those puts the config file underneath
        /// appsettings.json instead of above it. That failure is quiet and partial — keys absent from
        /// appsettings appear to save correctly while keys present there are silently ignored.
        ///
        /// The source is added directly rather than through AddJsonFile so it can be positioned; that
        /// means resolving the file provider by hand, since the builder would otherwise resolve the path
        /// against the content root and reload-watch the wrong directory.
        /// </remarks>
        private static RuntimeConfigFile AddWritableConfiguration(WebApplicationBuilder builder)
        {
            var path = RuntimeConfigFile.ResolvePath(
                builder.Configuration[$"{StorageOptions.SectionName}:{nameof(StorageOptions.ConfigFile)}"],
                builder.Environment.ContentRootPath);

            // The file watcher needs the directory to exist now, not once something writes the file.
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var source = new JsonConfigurationSource
            {
                Path = path,
                Optional = true,
                ReloadOnChange = true,
            };
            source.ResolveFileProvider();

            var sources = builder.Configuration.Sources;
            var index = sources.Count;
            for (var i = 0; i < sources.Count; i++)
            {
                if (sources[i] is EnvironmentVariablesConfigurationSource { Prefix: null or "" })
                {
                    index = i;
                    break;
                }
            }

            sources.Insert(index, source);

            return new RuntimeConfigFile(path);
        }

        private static void EnsureDatabase(WebApplication app)
        {
            // SQLite won't create a missing parent directory for the db file.
            var connectionString = app.Configuration.GetConnectionString("CallTree")!;
            var dataSource = new SqliteConnectionStringBuilder(connectionString).DataSource;
            var directory = Path.GetDirectoryName(Path.GetFullPath(dataSource));
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using var scope = app.Services.CreateScope();
            scope.ServiceProvider.GetRequiredService<CallTreeDbContext>().Database.Migrate();
        }
    }
}
