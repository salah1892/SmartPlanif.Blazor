namespace SmartPlanif.Infrastructure.MySql;

/// <summary>Comment lire dritin.DEDURTR (« durée du trajet en minutes ») pour obtenir la durée d'un voyage.</summary>
public enum ItineraryDurationMode
{
    /// <summary>Détection automatique : valeurs strictement croissantes = cumulées ; sinon durées de tronçons. Départage par la vitesse moyenne.</summary>
    Auto,

    /// <summary>DEDURTR est cumulée depuis le départ : la durée du voyage est la plus grande valeur.</summary>
    MaxCumulative,

    /// <summary>DEDURTR est la durée de chaque tronçon : la durée du voyage est la somme.</summary>
    SumOfLegs,
}

/// <summary>Un arrêt de l'itinéraire d'une ligne (table dritin).</summary>
/// <param name="Sequence">dritin.DENUMLG : ordre de l'arrêt.</param>
/// <param name="Minutes">dritin.DEDURTR : durée en minutes (cumulée ou de tronçon selon le mode).</param>
/// <param name="Km">dritin.DEKMSTA : kilomètres depuis le départ (cumulés).</param>
public sealed record ItineraryStop(long Sequence, long? Minutes, long? Km);

/// <summary>Durée déduite pour une ligne, avec la méthode retenue et la vitesse moyenne implicite (contrôle de vraisemblance).</summary>
public sealed record ItineraryDurationResult(TimeSpan? Duration, string Method, double? SpeedKmh);

/// <summary>
/// Calcule la durée d'un voyage à partir de l'itinéraire de sa ligne quand stop_times est vide (cas des voyages futurs).
/// Fonction pure, testable sans base. La signification exacte de DEDURTR n'étant pas documentée, le mode est configurable
/// et la page « Diagnostic » affiche, par ligne, les valeurs lues et la durée retenue.
/// </summary>
public static class ItineraryDuration
{
    /// <summary>Vitesses commerciales considérées plausibles pour un bus (km/h) : sert à départager cumulé / somme.</summary>
    private const double MinPlausibleSpeed = 12, MaxPlausibleSpeed = 90, TypicalSpeed = 35;

    /// <summary>Résout la durée de la ligne. <c>Duration</c> est null si l'itinéraire est vide ou sans durée.</summary>
    public static ItineraryDurationResult Resolve(IReadOnlyList<ItineraryStop> stops, ItineraryDurationMode mode)
    {
        var minutes = stops.OrderBy(s => s.Sequence).Select(s => s.Minutes).Where(m => m is not null).Select(m => m!.Value).ToList();
        if (minutes.Count == 0) return new ItineraryDurationResult(null, "itinéraire vide", null);

        var maxKm = stops.Max(s => s.Km);
        var cumulative = minutes.Max();
        var sum = minutes.Sum();

        long chosen;
        string method;
        switch (mode)
        {
            case ItineraryDurationMode.MaxCumulative: chosen = cumulative; method = "maximum (cumulé)"; break;
            case ItineraryDurationMode.SumOfLegs: chosen = sum; method = "somme des tronçons"; break;
            default:
                var looksCumulative = IsStrictlyIncreasing(minutes);
                var speedCumulative = Speed(maxKm, cumulative);
                var speedSum = Speed(maxKm, sum);
                var cumulativePlausible = IsPlausible(speedCumulative);
                var sumPlausible = IsPlausible(speedSum);

                if (cumulativePlausible && !sumPlausible) looksCumulative = true;          // la vitesse tranche
                else if (sumPlausible && !cumulativePlausible) looksCumulative = false;
                else if (cumulativePlausible && sumPlausible && looksCumulative)
                    looksCumulative = Math.Abs((speedCumulative ?? 0) - TypicalSpeed) <= Math.Abs((speedSum ?? 0) - TypicalSpeed);

                chosen = looksCumulative ? cumulative : sum;
                method = looksCumulative ? "auto : cumulé (maximum)" : "auto : somme des tronçons";
                break;
        }

        if (chosen <= 0) return new ItineraryDurationResult(null, method + " (durée nulle)", null);
        return new ItineraryDurationResult(TimeSpan.FromMinutes(chosen), method, Speed(maxKm, chosen));
    }

    /// <summary>Valeurs strictement croissantes (en ignorant les zéros de tête) : signature d'une durée cumulée.</summary>
    private static bool IsStrictlyIncreasing(IReadOnlyList<long> values)
    {
        var nonLeadingZero = values.SkipWhile(v => v == 0).ToList();
        if (nonLeadingZero.Count < 2) return nonLeadingZero.Count == 1 && values.Count > 1;
        for (var i = 1; i < nonLeadingZero.Count; i++)
            if (nonLeadingZero[i] <= nonLeadingZero[i - 1]) return false;
        return true;
    }

    private static double? Speed(long? km, long minutes) => km is > 0 && minutes > 0 ? km.Value / (minutes / 60.0) : null;

    private static bool IsPlausible(double? speed) => speed is >= MinPlausibleSpeed and <= MaxPlausibleSpeed;
}
