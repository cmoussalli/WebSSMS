using WebSSMS.Components;
using WebSSMS.Endpoints;
using WebSSMS.Models;
using WebSSMS.Services;

namespace WebSSMS
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.
            builder.Services.AddRazorComponents()
                .AddInteractiveServerComponents();

            builder.Services.Configure<BackupStorageOptions>(
                builder.Configuration.GetSection(BackupStorageOptions.SectionName));

            // Register WebSSMS services (scoped = per-circuit/per-tab)
            builder.Services.AddScoped<ConnectionManager>();
            builder.Services.AddScoped<QueryExecutionService>();
            builder.Services.AddScoped<SchemaDiscoveryService>();
            builder.Services.AddScoped<ScriptGeneratorService>();
            builder.Services.AddScoped<BackupRestoreService>();
            builder.Services.AddScoped<BackupFileService>();
            builder.Services.AddScoped<DatabaseAdminService>();
            builder.Services.AddScoped<SecurityService>();
            builder.Services.AddScoped<MonitoringService>();
            builder.Services.AddScoped<AgentJobService>();
            builder.Services.AddScoped<ImportExportService>();
            builder.Services.AddScoped<MaintenanceService>();
            builder.Services.AddScoped<IntelliSenseService>();
            builder.Services.AddSingleton<TemplateService>();

            // Singleton: a transfer ticket is minted inside a Blazor circuit but
            // redeemed by a plain HTTP request in a different DI scope.
            builder.Services.AddSingleton<BackupTransferTicketStore>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStatusCodePagesWithReExecute("/not-found");
            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapBackupTransferEndpoints();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
