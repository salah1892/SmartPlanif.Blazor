using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using SmartPlanif.Application.Abstractions;
using SmartPlanif.Domain.Alns;
using SmartPlanif.Domain.Allocation;
using SmartPlanif.Domain.Entities;
using SmartPlanif.Domain.Scheduling;

namespace SmartPlanif.Application.UseCases;

/// <summary>Stratégie d'allocation choisie par l'utilisateur.</summary>
public enum AllocationMethod
{
    /// <summary>Glouton chronologique : heuristique simple, sert de référence de comparaison.</summary>
    GreedyChronological,

    /// <summary>Construction par phases (type de ligne, du plus tendu au moins tendu), voyages les plus contraints d'abord.</summary>
    PhasedConstruction,

    /// <summary>Construction par phases puis amélioration par métaheuristique ALNS + recuit simulé.</summary>
    PhasedConstructionWithAlns,
}

/// <summary>Demande d'allocation d'une journée.</summary>
/// <param name="Date">Jour de service à planifier.</param>
/// <param name="User">Utilisateur à l'origine de la demande (traçabilité).</param>
/// <param name="Method">Stratégie.</param>
/// <param name="Save">Quand écrire le résultat dans trips (jamais, journée complète seulement, ou même partiel).</param>
/// <param name="ExistingPlan">Que faire des voyages déjà planifiés : les conserver ou replanifier toute la journée.</param>
/// <param name="PolicyOverride">Politique à utiliser à la place de la politique enregistrée (essais sans sauvegarder).</param>
public sealed record AllocateDayCommand(
    DateOnly Date, string User, AllocationMethod Method,
    SaveMode Save = SaveMode.None,
    ExistingPlanMode ExistingPlan = ExistingPlanMode.FillMissingOnly,
    AllocationPolicy? PolicyOverride = null);

/// <summary>Synthèse de la métaheuristique.</summary>
public sealed record AlnsSummary(
    int Iterations, TimeSpan Elapsed, double InitialObjective, double FinalObjective,
    IReadOnlyList<ConvergencePoint> Convergence, IReadOnlyList<OperatorStatistic> Operators);

/// <summary>Rapport complet d'une allocation.</summary>
public sealed record AllocationReport(
    DateOnly Date, AllocationMethod Method,
    int TotalTrips, int AssignedCount, int UnassignedCount, TimeSpan Elapsed,
    double ConstructionObjective, double FinalObjective,
    IReadOnlyList<string> HardConstraintCodes, IReadOnlyList<string> SoftConstraintCodes,
    IReadOnlyList<TripAssignment> Assignments, IReadOnlyList<UnassignedTrip> Failures,
    IReadOnlyList<ConstructionPhase> Phases, IReadOnlyList<FeasibilityLine> Feasibility,
    IReadOnlyList<PlanViolation> Violations, AlnsSummary? Alns, int LockedTrips, DayPlanSaveResult? Save);

/// <summary>
/// Cas d'usage principal : allouer bus, chauffeurs et receveurs aux voyages d'une journée.
/// Charge les données une seule fois, exécute l'algorithme hors du fil d'interface, contrôle le plan obtenu
/// puis, si demandé et si le plan est valide, enregistre les propositions.
/// </summary>
public sealed class AllocateDayHandler(
    ITripRepository trips, IResourceRepository resources, IAssignmentRepository assignments,
    IReferenceDataRepository reference, IAllocationSettingsStore settings, PlanningCalendar calendar)
{
    /// <summary>Exécute l'allocation. L'annulation (<paramref name="ct"/>) arrête proprement la métaheuristique.</summary>
    /// <exception cref="PastDayException">La journée est passée : elle n'est pas planifiable.</exception>
    public async Task<AllocationReport> HandleAsync(AllocateDayCommand command, IProgress<AlnsProgress>? progress, CancellationToken ct)
    {
        calendar.EnsureEditable(command.Date);   // une journée passée n'est jamais planifiée, même en simulation

        var policy = command.PolicyOverride ?? await settings.LoadAsync(ct);

        // Chargement unique de toute la fenêtre J-1..J+1 (repos et voyages après minuit), aucune requête dans la boucle.
        var dayTrips = await trips.GetTripsOfDayAsync(command.Date, ct);
        var assigned = await trips.GetAssignedAsync(command.Date.AddDays(-1), command.Date.AddDays(1), ct);
        var inputs = DayPlanningInputs.Resolve(dayTrips, assigned, command.ExistingPlan);
        var pool = await resources.GetPoolAsync(command.Date.AddDays(-1), command.Date.AddDays(1), ct);
        var labels = (await reference.GetLineTypesAsync(ct)).ToDictionary(x => x.Id, x => x.Label);

        var report = await Task.Run(() => Compute(command, policy, inputs.ToAllocate, inputs.Locked, pool, labels, progress, ct));

        if (command.Save != SaveMode.None)
            report = report with { Save = await SaveAsync(command, report, ct) };
        return report;
    }

    /// <summary>
    /// Écrit le plan dans trips si les garde-fous le permettent. Refus (sans rien écrire) si : le contrôle indépendant a trouvé
    /// des violations, il n'y a rien à enregistrer, ou la journée est incomplète en mode « journée complète seulement ».
    /// Un plan existant n'est donc jamais écrasé par un plan incomplet ou invalide.
    /// </summary>
    private async Task<DayPlanSaveResult> SaveAsync(AllocateDayCommand command, AllocationReport report, CancellationToken ct)
    {
        if (report.Violations.Count > 0)
            return new DayPlanSaveResult(false,
                $"Enregistrement refusé : le contrôle indépendant a détecté {report.Violations.Count} violation(s) de règle dure.", 0, 0);
        if (report.TotalTrips == 0)
            return new DayPlanSaveResult(false,
                "Rien à enregistrer : aucun voyage à planifier (tous ont déjà un plan). Choisissez « Remplacer le plan de la journée » pour replanifier.", 0, 0);
        if (report.Assignments.Count == 0)
            return new DayPlanSaveResult(false, "Rien à enregistrer : aucun voyage n'a pu être affecté.", 0, 0);
        if (command.Save == SaveMode.CompleteDayOnly && report.UnassignedCount > 0)
            return new DayPlanSaveResult(false,
                $"Journée incomplète : {report.UnassignedCount} voyage(s) non affecté(s) sur {report.TotalTrips}. Rien n'a été écrit dans trips (le plan existant est conservé).", 0, 0);

        var plan = new DayPlanToSave(
            command.Date, report.Assignments, report.Failures.Select(f => f.Trip.Id).ToList(),
            ClearUnassigned: command.ExistingPlan == ExistingPlanMode.ReplaceDay);
        return await assignments.SaveDayPlanAsync(plan, command.User, ct);
    }

    /// <summary>Partie CPU de l'allocation (exécutée sur un fil d'arrière-plan).</summary>
    private static AllocationReport Compute(
        AllocateDayCommand command, AllocationPolicy policy,
        IReadOnlyList<Trip> toAllocate, IReadOnlyList<AssignedTrip> locked, ResourcePool pool,
        IReadOnlyDictionary<long, string> lineTypeLabels, IProgress<AlnsProgress>? progress, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();
        var allocator = new TripAllocator(policy);

        // Les voyages déjà affectés occupent leurs ressources avant tout calcul.
        var book = new ScheduleBook();
        foreach (var a in locked) book.Commit(a.Trip, a.BusId, a.DriverId, a.ReceiverId);

        var feasibility = FeasibilityAnalyzer.Analyze(toAllocate, pool, policy, command.Date, lineTypeLabels);

        // Phase 1 : construction.
        IConstructionStrategy strategy = command.Method == AllocationMethod.GreedyChronological
            ? new ChronologicalConstructionStrategy()
            : new PhasedConstructionStrategy();
        var construction = strategy.Build(toAllocate, pool, book, allocator, lineTypeLabels);
        var objective = new ObjectiveFunction(policy, pool);
        var constructionObjective = objective.Evaluate(construction.State);
        var state = construction.State;

        // Phase 2 : amélioration par ALNS (optionnelle).
        AlnsSummary? alnsSummary = null;
        if (command.Method == AllocationMethod.PhasedConstructionWithAlns && toAllocate.Count > 0)
        {
            var result = new AlnsImprover().Improve(state, pool, allocator, progress, ct);
            state = result.Best;
            alnsSummary = new AlnsSummary(result.Iterations, result.Elapsed, result.InitialObjective,
                result.FinalObjective, result.Convergence, result.OperatorStatistics);
        }

        // Phase 3 : dernière chance déterministe pour les voyages restants, puis contrôles.
        PlanCompleter.TryCompleteUnassigned(state, pool, allocator);
        var finalObjective = objective.Evaluate(state);

        var finalAssignments = PlanEvaluator.BuildFinalAssignments(state, pool, allocator);
        var violations = PlanValidator.Validate(state, pool, allocator);

        var failures = state.UnassignedTrips
            .Select(t => allocator.TryAssign(t, pool, state.Book).Failure)
            .OfType<UnassignedTrip>()
            .OrderBy(f => f.Trip.Departure)
            .ToList();

        var hardCodes = allocator.BusRules.Hard.Select(r => r.Code)
            .Concat(allocator.AgentRules.Hard.Select(r => r.Code)).Distinct().OrderBy(c => c).ToList();
        var softCodes = allocator.BusRules.Soft.Select(r => r.Code)
            .Concat(allocator.AgentRules.Soft.Select(r => r.Code)).Distinct().OrderBy(c => c).ToList();

        return new AllocationReport(
            command.Date, command.Method, toAllocate.Count, finalAssignments.Count, failures.Count, stopwatch.Elapsed,
            constructionObjective, finalObjective, hardCodes, softCodes,
            finalAssignments, failures, construction.Phases, feasibility, violations, alnsSummary, locked.Count, null);
    }
}

/// <summary>Enregistrement des services de la couche Application.</summary>
public static class ApplicationServiceRegistration
{
    /// <summary>Ajoute les cas d'usage au conteneur d'injection de dépendances.</summary>
    public static IServiceCollection AddApplication(this IServiceCollection services) =>
        services
            .AddTransient<AllocateDayHandler>()
            .AddTransient<PreviewDayHandler>()
            .AddTransient<AllocationSettingsService>()
            .AddTransient<DatabaseDiagnosticsService>()
            .AddTransient<SavedPlanService>();
}
