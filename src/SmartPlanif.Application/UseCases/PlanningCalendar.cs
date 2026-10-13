using Microsoft.Extensions.DependencyInjection;

namespace SmartPlanif.Application.UseCases;

/// <summary>Levée quand on tente de planifier ou de modifier une journée passée.</summary>
public sealed class PastDayException(DateOnly date, DateOnly today)
    : InvalidOperationException($"Le {date:dd/MM/yyyy} est une journée passée (aujourd'hui : {today:dd/MM/yyyy}) : elle n'est plus modifiable. " +
                                $"La planification se fait à partir du {today:dd/MM/yyyy}.");

/// <summary>
/// Calendrier de planification : une journée passée n'est JAMAIS planifiable ni modifiable (elle reste consultable).
/// Aujourd'hui et les jours futurs sont modifiables. La date du jour vient d'une horloge injectable ; pour tester sur des données
/// anciennes, <c>Planning:Today</c> dans appsettings.json fixe une date de référence.
/// </summary>
public sealed class PlanningCalendar(TimeProvider clock, DateOnly? todayOverride = null)
{
    /// <summary>Date du jour pour la planification (réglage de test, sinon date locale du serveur).</summary>
    public DateOnly Today => todayOverride ?? DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    /// <summary>Vrai si la journée peut être planifiée ou modifiée (aujourd'hui ou plus tard).</summary>
    public bool IsEditable(DateOnly date) => date >= Today;

    /// <summary>Lève <see cref="PastDayException"/> si la journée est passée.</summary>
    public void EnsureEditable(DateOnly date)
    {
        if (!IsEditable(date)) throw new PastDayException(date, Today);
    }
}

/// <summary>Enregistrement du calendrier de planification.</summary>
public static class PlanningCalendarRegistration
{
    /// <summary>Ajoute le calendrier. <paramref name="todayOverride"/> : date de référence fixée (tests sur données anciennes), sinon l'horloge du serveur.</summary>
    public static IServiceCollection AddPlanningCalendar(this IServiceCollection services, DateOnly? todayOverride = null) =>
        services.AddSingleton(new PlanningCalendar(TimeProvider.System, todayOverride));
}
