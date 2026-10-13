using SmartPlanif.Application.Abstractions;

namespace SmartPlanif.Application.UseCases;

/// <summary>Cas d'usage « consulter ou effacer le plan prévu enregistré dans trips pour une journée ».</summary>
public sealed class SavedPlanService(IAssignmentRepository assignments, PlanningCalendar calendar)
{
    /// <summary>Plan prévu enregistré (voyages de la journée avec bus, chauffeur, receveur prévus).</summary>
    public Task<IReadOnlyList<SavedTripAssignment>> GetAsync(DateOnly date, CancellationToken ct = default) =>
        assignments.GetSavedPlanAsync(date, ct);

    /// <summary>Vide BUS_PR, CHAUFF_PR, REC_PR de la journée (les voyages effectués ne sont pas touchés). Renvoie le nombre de voyages modifiés.</summary>
    /// <exception cref="PastDayException">La journée est passée : son plan n'est plus modifiable.</exception>
    public Task<int> ClearAsync(DateOnly date, CancellationToken ct = default)
    {
        calendar.EnsureEditable(date);
        return assignments.ClearDayPlanAsync(date, ct);
    }
}
