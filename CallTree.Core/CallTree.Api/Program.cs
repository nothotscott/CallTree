using System.Text.Json.Serialization;
using CallTree.Application;
using CallTree.Infrastructure;
using CallTree.Infrastructure.Persistence;
using CallTree.Telephony;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

namespace CallTree.Api
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

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
