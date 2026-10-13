namespace SmartPlanif.Infrastructure.MySql;

/// <summary>Réglages de lecture des voyages (section « TripSource » de appsettings.json).</summary>
public sealed class TripSourceOptions
{
    /// <summary>
    /// Valeurs de trips.ETAT à exclure de la planification. VIDE par défaut : aucune valeur n'est exclue, car leur signification
    /// n'est pas confirmée (le schéma indique seulement « 3 = terminé, 5 = autre »). Un voyage « terminé » dans le passé
    /// peut très bien être à planifier si la table est remplie pour toute l'année.
    /// </summary>
    public List<long> ExcludedStates { get; set; } = new();

    /// <summary>N'accepter que les voyages dont la ligne est active (drligne.DEACTIF = 1).</summary>
    public bool RequireActiveLine { get; set; } = true;

    /// <summary>
    /// Lecture de dritin.DEDURTR quand stop_times est vide (voyages futurs) : l'heure d'arrivée est alors
    /// départ + durée de l'itinéraire de la ligne. Auto par défaut ; fixez MaxCumulative ou SumOfLegs une fois le sens confirmé.
    /// </summary>
    public ItineraryDurationMode ItineraryDuration { get; set; } = ItineraryDurationMode.Auto;

    /// <summary>
    /// Année « modèle » de la table trips (ex. 2025). Si renseignée, le jour demandé (ex. 15/10/2026) est lu dans les lignes du même
    /// jour et mois de l'année modèle (15/10/2025) : la table sert de calendrier annuel réutilisé chaque année. Les horaires sont
    /// ramenés sur le jour demandé et les colonnes *_PR / *_RE de ces lignes sont celles qui sont lues et écrites.
    /// Laisser vide (null) si la table contient des lignes pour chaque année.
    /// </summary>
    public int? TemplateYear { get; set; }
}

/// <summary>Correspondance entre le jour demandé et le jour lu dans la table trips.</summary>
public static class TripDayMapper
{
    /// <summary>Jour à lire : le même jour/mois dans l'année modèle (le 29 février devient le 28 si l'année modèle n'est pas bissextile).</summary>
    public static DateOnly Map(DateOnly requested, int? templateYear) =>
        templateYear is { } year
            ? new DateOnly(year, requested.Month, Math.Min(requested.Day, DateTime.DaysInMonth(year, requested.Month)))
            : requested;

    /// <summary>Phrase de diagnostic : « 15/10/2026 -> lignes de trips du 15/10/2025 (année modèle 2025) ».</summary>
    public static string Describe(DateOnly requested, int? templateYear) =>
        templateYear is null
            ? $"{requested:dd/MM/yyyy} -> lignes de trips dont DEDATED = {requested:dd/MM/yyyy} (jour exact)"
            : $"{requested:dd/MM/yyyy} -> lignes de trips du {Map(requested, templateYear):dd/MM/yyyy} (année modèle {templateYear}, horaires ramenés sur le jour demandé)";
}

/// <summary>Morceaux de SQL partagés par les dépôts MySQL (une seule définition du « jour de service »).</summary>
internal static class TripSql
{
    /// <summary>
    /// Jour de service = DEDATED ; à défaut la date de TIME_DEPART. Paramètres : @From (inclus) et @ToExclusive (exclu).
    /// La forme « bornes » permet d'utiliser un index sur DEDATED.
    /// </summary>
    public const string DayFilter =
        "((t.DEDATED >= @From AND t.DEDATED < @ToExclusive) OR (t.DEDATED IS NULL AND t.TIME_DEPART >= @From AND t.TIME_DEPART < @ToExclusive))";

    /// <summary>
    /// Une ressource est « renseignée » si son identifiant est strictement positif : NULL et 0 (valeur par défaut fréquente
    /// des bases héritées) signifient tous deux « aucune ressource ».
    /// </summary>
    public static string Has(string column) => $"COALESCE({column}, 0) > 0";

    /// <summary>Un voyage dont les trois ressources RÉELLES sont renseignées est « effectué » : on n'y touche jamais.</summary>
    public const string NotRealized = "NOT (COALESCE(t.BUS_RE, 0) > 0 AND COALESCE(t.CHAUFF_RE, 0) > 0 AND COALESCE(t.REC_RE, 0) > 0)";

    /// <summary>Heure d'arrivée = arrivée au dernier arrêt (trips n'a pas d'heure d'arrivée).</summary>
    public const string ArrivalSubquery = "(SELECT MAX(s.ARRIVAL_TIME) FROM stop_times s WHERE s.TRIP_ID = t.ID)";

    /// <summary>Paramètres SQL d'UN jour de la table (le jour déjà converti par <see cref="TripDayMapper"/>) : [jour, lendemain[.</summary>
    public static object Day(DateOnly tableDay) => new
    {
        From = tableDay.ToDateTime(TimeOnly.MinValue),
        ToExclusive = tableDay.AddDays(1).ToDateTime(TimeOnly.MinValue),
    };
}
