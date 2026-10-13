using SmartPlanif.Application.Abstractions;
using SmartPlanif.Domain.Allocation;
using SmartPlanif.Domain.Entities;

namespace SmartPlanif.Application.UseCases;

/// <summary>Nombre de voyages d'un type de ligne.</summary>
public sealed record LineTypeSummary(string Label, int TripCount);

/// <summary>Aperçu d'une journée avant allocation : volumes et diagnostic de faisabilité (phase 0).</summary>
public sealed record DayPreview(
    DateOnly Date, bool IsEditable, int DayTrips, int TripsToAllocate, int LockedTrips, int AlreadyPlannedInDay, int RealizedInDay,
    int ActiveBuses, int Drivers, int Receivers,
    IReadOnlyList<LineTypeSummary> LineTypes, IReadOnlyList<FeasibilityLine> Feasibility);

/// <summary>Cas d'usage « analyser la journée » : ne modifie rien, signale les pénuries de ressources.</summary>
public sealed class PreviewDayHandler(
    ITripRepository trips, IResourceRepository resources, IReferenceDataRepository reference,
    IAllocationSettingsStore settings, PlanningCalendar calendar)
{
    /// <summary>Calcule l'aperçu de la journée avec la politique enregistrée (ou celle fournie).</summary>
    public async Task<DayPreview> HandleAsync(DateOnly date, ExistingPlanMode mode, AllocationPolicy? policyOverride, CancellationToken ct)
    {
        var policy = policyOverride ?? await settings.LoadAsync(ct);
        var dayTrips = await trips.GetTripsOfDayAsync(date, ct);
        var assigned = await trips.GetAssignedAsync(date.AddDays(-1), date.AddDays(1), ct);
        var inputs = DayPlanningInputs.Resolve(dayTrips, assigned, mode);
        var pool = await resources.GetPoolAsync(date.AddDays(-1), date.AddDays(1), ct);
        var labels = (await reference.GetLineTypesAsync(ct)).ToDictionary(x => x.Id, x => x.Label);

        var dayIds = dayTrips.Select(t => t.Id).ToHashSet();
        var assignedOfDay = assigned.Where(a => dayIds.Contains(a.Trip.Id)).ToList();

        var feasibility = FeasibilityAnalyzer.Analyze(inputs.ToAllocate, pool, policy, date, labels);
        var lineTypes = inputs.ToAllocate
            .GroupBy(t => t.LineTypeId)
            .OrderBy(g => g.Key)
            .Select(g => new LineTypeSummary(
                g.Key is { } id ? labels.GetValueOrDefault(id, $"Type {id}") : "Type inconnu", g.Count()))
            .ToList();

        return new DayPreview(
            date, calendar.IsEditable(date), dayTrips.Count, inputs.ToAllocate.Count, inputs.Locked.Count,
            assignedOfDay.Count(a => !a.IsRealized), assignedOfDay.Count(a => a.IsRealized),
            pool.Buses.Count(b => b.Active),
            pool.Agents.Count(a => a.Role.CanDrive()),
            pool.Agents.Count(a => a.Role.CanReceive()),
            lineTypes, feasibility);
    }
}
