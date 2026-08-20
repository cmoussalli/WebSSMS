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

            builder.Services.Configure<AiSettings>(
                builder.Configuration.GetSection(AiSettings.SectionName));

            // The AI assistant reaches an external LLM endpoint over HTTP.
            builder.Services.AddHttpClient();

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

            // AI assistant: the saved settings, the LLM transport, the read-only
            // lookups it is allowed, and the agent that drives them.
            //
            // Singleton: the settings are saved to a file on the server, so they
            // survive a restart and are not one browser's private business.
            builder.Services.AddSingleton<AiSettingsStore>();
            builder.Services.AddScoped<AiSettingsProvider>();
            builder.Services.AddScoped<LlmClient>();
            builder.Services.AddScoped<SqlAiToolbox>();
            builder.Services.AddScoped<SqlAiAgent>();

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
