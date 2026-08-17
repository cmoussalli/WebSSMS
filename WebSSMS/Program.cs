using WebSSMS.Components;
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

            // Register WebSSMS services (scoped = per-circuit/per-tab)
            builder.Services.AddScoped<ConnectionManager>();
            builder.Services.AddScoped<QueryExecutionService>();
            builder.Services.AddScoped<SchemaDiscoveryService>();
            builder.Services.AddScoped<ScriptGeneratorService>();
            builder.Services.AddScoped<BackupRestoreService>();
            builder.Services.AddScoped<SecurityService>();
            builder.Services.AddScoped<MonitoringService>();
            builder.Services.AddScoped<AgentJobService>();
            builder.Services.AddScoped<ImportExportService>();
            builder.Services.AddScoped<MaintenanceService>();
            builder.Services.AddScoped<IntelliSenseService>();
            builder.Services.AddSingleton<TemplateService>();

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (!app.Environment.IsDevelopment())
            {
                app.UseExceptionHandler("/Error");
            }

            app.UseStatusCodePagesWithReExecute("/not-found");
            app.UseAntiforgery();

            app.MapStaticAssets();
            app.MapRazorComponents<App>()
                .AddInteractiveServerRenderMode();

            app.Run();
        }
    }
}
