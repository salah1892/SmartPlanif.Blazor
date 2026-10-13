using SmartPlanif.Application.UseCases;
using SmartPlanif.Infrastructure;
using SmartPlanif.Web.Components;
using SmartPlanif.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddApplication();

// Calendrier : les journées passées ne sont pas planifiables. Planning:Today (facultatif, AAAA-MM-JJ) fixe une date de référence,
// pratique pour tester sur des données anciennes.
DateOnly? todayOverride = DateOnly.TryParse(builder.Configuration["Planning:Today"], out var parsedToday) ? parsedToday : null;
builder.Services.AddPlanningCalendar(todayOverride);

// Les réglages (contraintes, paramètres, ALNS) sont conservés dans un fichier JSON modifiable depuis l'interface.
var settingsPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "allocation-settings.json");

// Chaîne de connexion vide = mode démonstration ; renseignée = base MySQL Transport_Ai.
var connectionString = builder.Configuration.GetConnectionString("TransportAi");
if (string.IsNullOrWhiteSpace(connectionString))
{
    builder.Services.AddInMemoryInfrastructure(settingsPath);
    builder.Services.AddSingleton(new DataSourceInfo("Démonstration : données générées en mémoire (aucune base requise)", true));
}
else
{
    builder.Services.AddMySqlInfrastructure(connectionString, settingsPath,
        options => builder.Configuration.GetSection("Qualifications").Bind(options),
        options => builder.Configuration.GetSection("TripSource").Bind(options),
        options => builder.Configuration.GetSection("ResourceSource").Bind(options));
    builder.Services.AddSingleton(new DataSourceInfo("Base MySQL Transport_Ai", false));
}

var app = builder.Build();

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
