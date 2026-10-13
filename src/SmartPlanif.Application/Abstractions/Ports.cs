using SmartPlanif.Domain.Allocation;
using SmartPlanif.Domain.Entities;

namespace SmartPlanif.Application.Abstractions;

/// <summary>Élément de référence (type de ligne, catégorie de bus...) : identifiant + libellé.</summary>
public sealed record ReferenceItem(long Id, string Label,string LabelArabic);

/// <summary>Accès aux voyages (tables trips, stop_times, drligne).</summary>
public interface ITripRepository
{
    /// <summary>
    /// TOUS les voyages valides d'un jour de service (planifiés ou non). Le choix « lesquels faut-il (re)planifier ? »
    /// est fait par <c>DayPlanningInputs</c> selon le mode demandé, pas par le dépôt.
    /// </summary>
    Task<IReadOnlyList<Trip>> GetTripsOfDayAsync(DateOnly date, CancellationToken ct);

    /// <summary>
    /// Voyages dont les trois ressources sont connues sur la fenêtre [from, to] : ressources RÉELLES (*_RE) si elles sont
    /// toutes renseignées (voyage effectué), sinon ressources PRÉVUES (*_PR) si elles sont toutes renseignées.
    /// </summary>
    Task<IReadOnlyList<AssignedTrip>> GetAssignedAsync(DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>Accès aux ressources (drvehic, dragent) et à leurs indisponibilités.</summary>
public interface IResourceRepository
{
    /// <summary>Bus, agents et indisponibilités couvrant la fenêtre [from, to].</summary>
    Task<ResourcePool> GetPoolAsync(DateOnly from, DateOnly to, CancellationToken ct);
}

/// <summary>Plan d'une journée à écrire dans la table trips (colonnes BUS_PR, CHAUFF_PR, REC_PR).</summary>
/// <param name="Date">Jour de service.</param>
/// <param name="Assignments">Affectations à écrire.</param>
/// <param name="UnassignedTripIds">Voyages du jour restés sans ressource.</param>
/// <param name="ClearUnassigned">Vider les colonnes *_PR des voyages non affectés (mode « remplacer la journée »).</param>
public sealed record DayPlanToSave(DateOnly Date, IReadOnlyList<TripAssignment> Assignments, IReadOnlyList<long> UnassignedTripIds, bool ClearUnassigned);

/// <summary>Résultat de l'écriture d'un plan de journée.</summary>
public sealed record DayPlanSaveResult(bool Success, string Message, int TripsAssigned, int TripsCleared);

/// <summary>Une ligne du plan enregistré, avec les libellés pour l'affichage.</summary>
public sealed record SavedTripAssignment(
    long TripId, string LineLabel, DateTime Departure, DateTime? Arrival,
    long? BusId, string? BusPlate, long? DriverId, string? DriverName, long? ReceiverId, string? ReceiverName,
    string? RealBusPlate, string? RealDriverName, string? RealReceiverName, bool HasRealResources);

/// <summary>Lecture et écriture du plan PRÉVU dans la table trips.</summary>
public interface IAssignmentRepository
{
    /// <summary>
    /// Écrit le plan d'une journée dans trips (BUS_PR, CHAUFF_PR, REC_PR) dans UNE transaction : tout ou rien.
    /// Les voyages déjà effectués (ressources réelles toutes renseignées) ne sont jamais modifiés.
    /// </summary>
    Task<DayPlanSaveResult> SaveDayPlanAsync(DayPlanToSave plan, string user, CancellationToken ct);

    /// <summary>Vide le plan PRÉVU d'une journée (hors voyages effectués). Renvoie le nombre de voyages modifiés.</summary>
    Task<int> ClearDayPlanAsync(DateOnly date, CancellationToken ct);

    /// <summary>Plan prévu enregistré pour un jour.</summary>
    Task<IReadOnlyList<SavedTripAssignment>> GetSavedPlanAsync(DateOnly date, CancellationToken ct);
}

/// <summary>Données de référence servant aux écrans de configuration (tables drtyli, drcatve).</summary>
public interface IReferenceDataRepository
{
    /// <summary>Types de ligne (urbain, interurbain...).</summary>
    Task<IReadOnlyList<ReferenceItem>> GetLineTypesAsync(CancellationToken ct);

    /// <summary>Catégories de véhicules (standard, confort...).</summary>
    Task<IReadOnlyList<ReferenceItem>> GetBusCategoriesAsync(CancellationToken ct);
}

/// <summary>Persistance de la politique d'allocation (choix des contraintes, paramètres, ALNS).</summary>
public interface IAllocationSettingsStore
{
    /// <summary>Charge la politique ; renvoie toujours un objet complet et indépendant (copie).</summary>
    Task<AllocationPolicy> LoadAsync(CancellationToken ct);

    /// <summary>Enregistre la politique.</summary>
    Task SaveAsync(AllocationPolicy policy, CancellationToken ct);

    /// <summary>Revient à la politique par défaut.</summary>
    Task ResetAsync(CancellationToken ct);
}

/// <summary>Nombre d'éléments par libellé (répartition d'une colonne).</summary>
public sealed record DiagnosticCount(string Label, long Count);

/// <summary>Statistiques d'une colonne de ressource de trips : lignes non NULL, et lignes avec un identifiant valide (> 0).</summary>
public sealed record ResourceColumnStat(string Column, long NonNull, long Positive)
{
    /// <summary>Lignes non NULL mais à 0 ou négatives : traitées comme « aucune ressource ».</summary>
    public long Zero => NonNull - Positive;
}

/// <summary>Durée déduite de l'itinéraire d'une ligne (table dritin), avec les valeurs lues pour vérification.</summary>
public sealed record LineDurationInfo(
    long LineId, string Label, long TripsOfDay, int Stops, long? MinMinutes, long? MaxMinutes, long? SumMinutes, long? MaxKm,
    double? ResolvedMinutes, string Method, double? SpeedKmh);

/// <summary>Constats bruts relevés directement dans la base pour un jour donné.</summary>
public sealed record RawDiagnostics(
    string DayMapping, long TotalTripsInTable, DateTime? FirstTripDay, DateTime? LastTripDay,
    long TripsOfDay, long TripsOfDayOnActiveLine, long TripsOfDayWithoutStopTimes, long TripsOfDayWithoutStopTimesAndItinerary,
    long TripsOfDayWithPlannedResources, long TripsOfDayWithRealResources,
    long BusesTotal, long BusesFlaggedActive,
    IReadOnlyList<ResourceColumnStat> ResourceColumns, IReadOnlyList<LineDurationInfo> LineDurations,
    IReadOnlyList<DiagnosticCount> TripStates, IReadOnlyList<DiagnosticCount> AgentQualifications,
    IReadOnlyList<DiagnosticCount> AgentRestDays, IReadOnlyList<DiagnosticCount> BusActivity,
    IReadOnlyList<DiagnosticCount> BusCategories, IReadOnlyList<DiagnosticCount> LineTypesOfDay,
    IReadOnlyList<string> SampleTrips, IReadOnlyList<string> MissingOptionalTables);

/// <summary>Relevés de diagnostic de la base (lecture seule).</summary>
public interface IDatabaseDiagnostics
{
    /// <summary>Collecte les constats bruts pour un jour de service.</summary>
    Task<RawDiagnostics> CollectAsync(DateOnly date, CancellationToken ct);
}
