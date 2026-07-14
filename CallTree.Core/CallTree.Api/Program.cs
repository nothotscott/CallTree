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

            builder.Services.AddControllers();
            builder.Services.AddOpenApi();
            builder.Services.AddHealthChecks();

            builder.Services.AddInfrastructure(builder.Configuration);
            builder.Services.AddTelephony(builder.Configuration);

            var app = builder.Build();

            EnsureDatabase(app);

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.MapOpenApi();

                app.MapScalarApiReference();

                app.MapGet("/", () => Results.LocalRedirect("/scalar", true)).ExcludeFromDescription();
            }

            app.UseHttpsRedirection();

            app.UseAuthorization();

            app.MapControllers();
            app.MapHealthChecks("/health");

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
