using SmartPlanif.Domain.Entities;

namespace SmartPlanif.Application.UseCases;

/// <summary>Que faire des voyages de la journée qui ont déjà un plan prévu (BUS_PR, CHAUFF_PR, REC_PR) ?</summary>
public enum ExistingPlanMode
{
    /// <summary>Ne planifier que les voyages sans plan complet ; les voyages déjà planifiés sont conservés et bloquent leurs ressources.</summary>
    FillMissingOnly,

    /// <summary>Replanifier TOUTE la journée (le plan existant sera remplacé à l'enregistrement). Les voyages déjà effectués restent intacts.</summary>
    ReplaceDay,
}

/// <summary>Quand écrire le résultat dans la table trips ?</summary>
public enum SaveMode
{
    /// <summary>Simulation : rien n'est écrit.</summary>
    None,

    /// <summary>N'écrire que si TOUS les voyages de la journée ont été affectés (la journée « réussit »).</summary>
    CompleteDayOnly,

    /// <summary>Écrire aussi un plan incomplet (les voyages non affectés restent sans ressource).</summary>
    AllowPartial,
}

/// <summary>Voyages à planifier et voyages verrouillés pour une exécution.</summary>
public sealed record DayPlanningInputs(IReadOnlyList<Trip> ToAllocate, IReadOnlyList<AssignedTrip> Locked)
{
    /// <summary>
    /// Détermine ce qu'il faut planifier selon le mode :
    /// <list type="bullet">
    /// <item>FillMissingOnly : les voyages du jour sans plan complet sont à planifier ; tout ce qui est déjà affecté est verrouillé.</item>
    /// <item>ReplaceDay : tous les voyages du jour sont à planifier, SAUF ceux déjà effectués (ressources réelles) ; les voyages des
    /// jours voisins restent verrouillés.</item>
    /// </list>
    /// </summary>
    public static DayPlanningInputs Resolve(
        IReadOnlyList<Trip> dayTrips, IReadOnlyList<AssignedTrip> assignedInWindow, ExistingPlanMode mode)
    {
        if (mode == ExistingPlanMode.ReplaceDay)
        {
            var dayIds = dayTrips.Select(t => t.Id).ToHashSet();
            var realizedDayIds = assignedInWindow.Where(a => a.IsRealized && dayIds.Contains(a.Trip.Id))
                .Select(a => a.Trip.Id).ToHashSet();

            var locked = assignedInWindow.Where(a => !dayIds.Contains(a.Trip.Id) || a.IsRealized).ToList();
            var toAllocate = dayTrips.Where(t => !realizedDayIds.Contains(t.Id)).ToList();
            return new DayPlanningInputs(toAllocate, locked);
        }

        var assignedIds = assignedInWindow.Select(a => a.Trip.Id).ToHashSet();
        return new DayPlanningInputs(dayTrips.Where(t => !assignedIds.Contains(t.Id)).ToList(), assignedInWindow);
    }
}
