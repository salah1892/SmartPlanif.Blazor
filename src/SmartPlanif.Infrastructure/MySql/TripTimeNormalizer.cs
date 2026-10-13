namespace SmartPlanif.Infrastructure.MySql;

/// <summary>Horaires d'un voyage, nettoyés et cohérents.</summary>
/// <param name="Departure">Départ sur le jour de service.</param>
/// <param name="Arrival">Arrivée, strictement après le départ.</param>
/// <param name="DatePartRepaired">Vrai si la partie « date » des horaires bruts n'était pas exploitable et a été reconstruite.</param>
public sealed record NormalizedTimes(DateTime Departure, DateTime Arrival, bool DatePartRepaired);

/// <summary>
/// Nettoie les horaires lus en base (fonction pure, testable sans base).
/// Certaines bases héritées stockent l'heure dans un DATETIME dont la partie date est factice (1900-01-01, 1970-01-01...).
/// Si la date du départ est à plus d'un jour du jour de service, on la remplace par le jour de service en conservant
/// l'heure, et on décale l'arrivée de la même durée (la durée du voyage est préservée).
/// </summary>
public static class TripTimeNormalizer
{
    /// <summary>Renvoie null si l'arrivée est absente ou incohérente avec le départ (voyage à écarter, règle V08).</summary>
    public static NormalizedTimes? Normalize(DateTime serviceDay, DateTime departureRaw, DateTime? arrivalRaw)
    {
        if (arrivalRaw is null) return null;

        var departure = departureRaw;
        var arrival = arrivalRaw.Value;
        var repaired = false;

        if (Math.Abs((departure.Date - serviceDay.Date).TotalDays) > 1)
        {
            var shift = serviceDay.Date + departure.TimeOfDay - departure;
            departure += shift;
            arrival += shift;
            repaired = true;
            if (arrival <= departure) arrival = arrival.AddDays(1);   // passage de minuit avec date de base factice
        }
        return arrival > departure ? new NormalizedTimes(departure, arrival, repaired) : null;
    }
}
