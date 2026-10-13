using SmartPlanif.Application.UseCases;
using SmartPlanif.Domain.Entities;
using SmartPlanif.Infrastructure.InMemory;
using SmartPlanif.Infrastructure.MySql;
using SmartPlanif.Infrastructure.Settings;
using Xunit;

namespace SmartPlanif.Domain.Tests;

/// <summary>
/// Cycle de vie du plan prévu (colonnes BUS_PR / CHAUFF_PR / REC_PR de trips) : écriture, remplacement, journée incomplète,
/// voyages déjà effectués, effacement. Le DemoDataStore se comporte comme la table trips (mêmes garde-fous).
/// </summary>
public class DayPlanWorkflowTests
{
    private static readonly DateOnly Day = new(2026, 10, 8);

    /// <summary>Calendrier de test : « aujourd'hui » = 1er janvier 2026, donc le 8 octobre 2026 est une journée à venir.</summary>
    private static PlanningCalendar CalendarBefore() => new(TimeProvider.System, new DateOnly(2026, 1, 1));

    private static (AllocateDayHandler Handler, DemoDataStore Store, DatabaseDiagnosticsService Diagnostics) Create(PlanningCalendar? calendar = null)
    {
        var store = new DemoDataStore();
        var path = Path.Combine(Path.GetTempPath(), $"smartplanif-{Guid.NewGuid():N}.json");
        var settings = new JsonAllocationSettingsStore(path, DemoDataStore.CreateDemoPolicy);
        return (new AllocateDayHandler(store, store, store, store, settings, calendar ?? CalendarBefore()), store,
                new DatabaseDiagnosticsService(store, store, store));
    }

    private static Task<AllocationReport> Run(
        AllocateDayHandler handler, AllocationMethod method, SaveMode save, ExistingPlanMode existing) =>
        handler.HandleAsync(new AllocateDayCommand(Day, "test", method, save, existing), null, CancellationToken.None);

    private static async Task<int> PlannedCount(DemoDataStore store) =>
        (await store.GetSavedPlanAsync(Day, CancellationToken.None)).Count(r => r.BusId is not null);

    [Fact]
    public async Task Complete_day_is_written_then_protected_against_worse_plans()
    {
        var (handler, store, _) = Create();

        // 1. Journée complète (ALNS) : écrite dans les colonnes prévues.
        var first = await Run(handler, AllocationMethod.PhasedConstructionWithAlns, SaveMode.CompleteDayOnly, ExistingPlanMode.FillMissingOnly);
        Assert.Equal(0, first.UnassignedCount);
        Assert.True(first.Save is { Success: true });
        Assert.Equal(first.TotalTrips, await PlannedCount(store));

        // 2. Mode « compléter » : tous les voyages ont déjà un plan, il n'y a rien à faire ni à écrire.
        var again = await Run(handler, AllocationMethod.GreedyChronological, SaveMode.CompleteDayOnly, ExistingPlanMode.FillMissingOnly);
        Assert.Equal(0, again.TotalTrips);
        Assert.True(again.Save is { Success: false });

        // 3. Mode « remplacer » avec un plan INCOMPLET (glouton : 34/36) en « journée complète seulement » :
        //    refusé, et le plan existant est conservé intact.
        var worse = await Run(handler, AllocationMethod.GreedyChronological, SaveMode.CompleteDayOnly, ExistingPlanMode.ReplaceDay);
        Assert.Equal(first.TotalTrips, worse.TotalTrips);
        Assert.True(worse.UnassignedCount > 0);
        Assert.True(worse.Save is { Success: false });
        Assert.Equal(first.TotalTrips, await PlannedCount(store));
    }

    [Fact]
    public async Task Incomplete_day_is_not_written_in_complete_mode()
    {
        var (handler, store, _) = Create();
        var report = await Run(handler, AllocationMethod.GreedyChronological, SaveMode.CompleteDayOnly, ExistingPlanMode.FillMissingOnly);

        Assert.True(report.UnassignedCount > 0);
        Assert.True(report.Save is { Success: false });
        Assert.True(report.Save!.Message.Contains("incomplète"));
        Assert.Equal(0, await PlannedCount(store));
    }

    [Fact]
    public async Task Incomplete_day_is_written_when_partial_saving_is_allowed()
    {
        var (handler, store, _) = Create();
        var report = await Run(handler, AllocationMethod.GreedyChronological, SaveMode.AllowPartial, ExistingPlanMode.FillMissingOnly);

        Assert.True(report.Save is { Success: true });
        Assert.Equal(report.AssignedCount, await PlannedCount(store));
        Assert.True(report.UnassignedCount > 0);
    }

    [Fact]
    public async Task Simulation_never_writes()
    {
        var (handler, store, _) = Create();
        var report = await Run(handler, AllocationMethod.GreedyChronological, SaveMode.None, ExistingPlanMode.FillMissingOnly);

        Assert.True(report.Save is null);
        Assert.Equal(0, await PlannedCount(store));
    }

    [Fact]
    public async Task Replacing_with_a_partial_plan_clears_the_trips_that_lost_their_resources()
    {
        var (handler, store, _) = Create();
        await Run(handler, AllocationMethod.GreedyChronological, SaveMode.AllowPartial, ExistingPlanMode.FillMissingOnly);
        var phased = await Run(handler, AllocationMethod.PhasedConstruction, SaveMode.AllowPartial, ExistingPlanMode.ReplaceDay);

        // Le plan enregistré est EXACTEMENT le nouveau plan : pas de reste de l'ancien plan.
        Assert.Equal(phased.AssignedCount, await PlannedCount(store));
        Assert.True(phased.Save is { Success: true });
    }

    [Fact]
    public async Task Realized_trips_are_locked_and_never_replanned()
    {
        var (handler, store, _) = Create();
        var trips = await store.GetTripsOfDayAsync(Day, CancellationToken.None);
        var realizedTrip = trips.First();
        store.MarkRealized(realizedTrip.Id, busId: 1, driverId: 101, receiverId: 102);

        var report = await Run(handler, AllocationMethod.GreedyChronological, SaveMode.AllowPartial, ExistingPlanMode.ReplaceDay);

        Assert.Equal(trips.Count - 1, report.TotalTrips);                                  // le voyage effectué n'est pas replanifié
        Assert.True(report.LockedTrips >= 1);
        Assert.True(report.Assignments.All(a => a.Trip.Id != realizedTrip.Id));
        // Le bus et les agents RÉELS restent occupés pendant ce voyage : personne d'autre ne les utilise en même temps.
        Assert.True(report.Assignments.All(a =>
            !(a.Bus.Id == 1 && a.Trip.Interval.Overlaps(realizedTrip.Interval))
            && !((a.Driver.Id == 101 || a.Driver.Id == 102 || a.Receiver.Id == 101 || a.Receiver.Id == 102)
                 && a.Trip.Interval.Overlaps(realizedTrip.Interval))));
        // Et le plan prévu du voyage effectué n'a pas été écrit.
        var saved = (await store.GetSavedPlanAsync(Day, CancellationToken.None)).Single(r => r.TripId == realizedTrip.Id);
        Assert.True(saved.BusId is null && saved.HasRealResources);
    }

    [Fact]
    public async Task Clearing_a_day_removes_the_planned_columns()
    {
        var (handler, store, _) = Create();
        var report = await Run(handler, AllocationMethod.GreedyChronological, SaveMode.AllowPartial, ExistingPlanMode.FillMissingOnly);
        Assert.Equal(report.AssignedCount, await PlannedCount(store));

        var cleared = await new SavedPlanService(store, CalendarBefore()).ClearAsync(Day);
        Assert.Equal(report.AssignedCount, cleared);
        Assert.Equal(0, await PlannedCount(store));
    }

    [Fact]
    public void Planning_inputs_depend_on_the_existing_plan_mode()
    {
        var day = new DateTime(2026, 10, 8);
        Trip T(long id, double from) => new(id, 10, "L", 1, day.AddHours(from), day.AddHours(from + 1), 10, 1, 1, null, null, null);
        var trips = new[] { T(1, 6), T(2, 8), T(3, 10) };
        var assigned = new[]
        {
            new AssignedTrip(trips[0], 1, 101, 201, IsRealized: false),   // plan prévu
            new AssignedTrip(trips[1], 1, 101, 201, IsRealized: true),    // effectué
            new AssignedTrip(T(99, 30), 2, 102, 202, IsRealized: false),  // jour voisin
        };

        var fill = DayPlanningInputs.Resolve(trips, assigned, ExistingPlanMode.FillMissingOnly);
        Assert.Equal(1, fill.ToAllocate.Count);                  // seul le voyage 3 est à planifier
        Assert.Equal(3, fill.Locked.Count);

        var replace = DayPlanningInputs.Resolve(trips, assigned, ExistingPlanMode.ReplaceDay);
        Assert.Equal(2, replace.ToAllocate.Count);               // voyages 1 et 3 (le 2 est effectué)
        Assert.Equal(2, replace.Locked.Count);                   // l'effectué + le voisin
    }

    [Fact]
    public async Task Diagnostics_on_demo_data_reports_a_healthy_day()
    {
        var (_, _, diagnostics) = Create();
        var report = await diagnostics.RunAsync(Day, CancellationToken.None);

        Assert.Equal(36, report.TripsLoaded);
        Assert.True(report.DriversMapped > 0 && report.ReceiversMapped > 0);
        Assert.False(report.Findings.Any(f => f.Level == "error"));
    }

    [Fact]
    public void Trip_times_with_a_valid_date_are_kept()
    {
        var day = new DateTime(2026, 10, 8);
        var times = TripTimeNormalizer.Normalize(day, day.AddHours(6), day.AddHours(7.25));
        Assert.True(times is { DatePartRepaired: false });
        Assert.Equal(day.AddHours(6), times!.Departure);
    }

    [Fact]
    public void Trip_times_with_a_fake_base_date_are_rebuilt_on_the_service_day()
    {
        var day = new DateTime(2026, 10, 8);
        var times = TripTimeNormalizer.Normalize(day, new DateTime(1900, 1, 1, 6, 0, 0), new DateTime(1900, 1, 1, 7, 15, 0));
        Assert.True(times is { DatePartRepaired: true });
        Assert.Equal(day.AddHours(6), times!.Departure);
        Assert.Equal(TimeSpan.FromMinutes(75), times.Arrival - times.Departure);
    }

    [Fact]
    public void Trip_times_crossing_midnight_with_a_fake_base_date_keep_their_duration()
    {
        var day = new DateTime(2026, 10, 8);
        var times = TripTimeNormalizer.Normalize(day, new DateTime(1900, 1, 1, 23, 30, 0), new DateTime(1900, 1, 1, 0, 20, 0));
        Assert.Equal(TimeSpan.FromMinutes(50), times!.Arrival - times.Departure);
    }

    [Fact]
    public void Trip_times_without_a_usable_arrival_are_rejected()
    {
        var day = new DateTime(2026, 10, 8);
        Assert.True(TripTimeNormalizer.Normalize(day, day.AddHours(6), null) is null);
        Assert.True(TripTimeNormalizer.Normalize(day, day.AddHours(6), day.AddHours(5)) is null);   // arrivée avant le départ
    }

    [Fact]
    public void Rest_day_numbers_are_converted_from_the_database_convention()
    {
        Assert.Equal(DayOfWeek.Monday, MySqlResourceRepository.ToDayOfWeek(1));
        Assert.Equal(DayOfWeek.Sunday, MySqlResourceRepository.ToDayOfWeek(7));   // 7 % 7 = 0 = dimanche en .NET
        Assert.Equal(DayOfWeek.Thursday, MySqlResourceRepository.ToDayOfWeek(4));
        Assert.True(MySqlResourceRepository.ToDayOfWeek(0) is null);
        Assert.True(MySqlResourceRepository.ToDayOfWeek(8) is null);
        Assert.True(MySqlResourceRepository.ToDayOfWeek(null) is null);
    }

    // ------------------------------------------------------------------ journées passées

    [Fact]
    public async Task A_past_day_cannot_be_planned_even_as_a_simulation()
    {
        var today = new PlanningCalendar(TimeProvider.System, new DateOnly(2026, 10, 11));   // le 8 octobre est passé
        var (handler, store, _) = Create(today);

        await Assert.ThrowsAsync<PastDayException>(() =>
            Run(handler, AllocationMethod.GreedyChronological, SaveMode.None, ExistingPlanMode.FillMissingOnly));
        Assert.Equal(0, await PlannedCount(store));
    }

    [Fact]
    public async Task Today_and_future_days_are_editable_but_yesterday_is_not()
    {
        var calendar = new PlanningCalendar(TimeProvider.System, new DateOnly(2026, 10, 8));
        Assert.True(calendar.IsEditable(new DateOnly(2026, 10, 8)));    // aujourd'hui : modifiable
        Assert.True(calendar.IsEditable(new DateOnly(2027, 1, 1)));     // futur : modifiable
        Assert.False(calendar.IsEditable(new DateOnly(2026, 10, 7)));   // hier : grisé
        await Task.CompletedTask;
    }

    [Fact]
    public async Task A_past_day_plan_cannot_be_cleared_but_can_still_be_read()
    {
        var today = new PlanningCalendar(TimeProvider.System, new DateOnly(2026, 10, 11));
        var store = new DemoDataStore();
        var service = new SavedPlanService(store, today);

        await Assert.ThrowsAsync<PastDayException>(() => service.ClearAsync(Day));
        var rows = await service.GetAsync(Day);                          // consultation toujours permise
        Assert.Equal(36, rows.Count);
    }

    [Fact]
    public async Task Preview_tells_whether_the_day_is_editable()
    {
        var store = new DemoDataStore();
        var path = Path.Combine(Path.GetTempPath(), $"smartplanif-{Guid.NewGuid():N}.json");
        var settings = new JsonAllocationSettingsStore(path, DemoDataStore.CreateDemoPolicy);
        var past = new PreviewDayHandler(store, store, store, settings, new PlanningCalendar(TimeProvider.System, new DateOnly(2026, 10, 11)));
        var future = new PreviewDayHandler(store, store, store, settings, CalendarBefore());

        Assert.False((await past.HandleAsync(Day, ExistingPlanMode.FillMissingOnly, null, CancellationToken.None)).IsEditable);
        Assert.True((await future.HandleAsync(Day, ExistingPlanMode.FillMissingOnly, null, CancellationToken.None)).IsEditable);
    }

    // ------------------------------------------------------------------ année modèle

    [Fact]
    public void Template_year_maps_a_requested_day_to_the_same_day_of_the_template_year()
    {
        Assert.Equal(new DateOnly(2025, 10, 15), TripDayMapper.Map(new DateOnly(2026, 10, 15), 2025));
        Assert.Equal(new DateOnly(2025, 1, 1), TripDayMapper.Map(new DateOnly(2031, 1, 1), 2025));
        Assert.Equal(new DateOnly(2025, 2, 28), TripDayMapper.Map(new DateOnly(2028, 2, 29), 2025));   // 29 février -> 28 février
        Assert.Equal(new DateOnly(2026, 10, 15), TripDayMapper.Map(new DateOnly(2026, 10, 15), null)); // sans année modèle : jour exact
    }

    [Fact]
    public void Template_year_rows_are_rebuilt_on_the_requested_day()
    {
        // Ligne du 15/10/2025 réutilisée pour le 15/10/2026 : l'heure est conservée, la date devient celle du jour demandé.
        var requested = new DateTime(2026, 10, 15);
        var times = TripTimeNormalizer.Normalize(requested, new DateTime(2025, 10, 15, 6, 30, 0), new DateTime(2025, 10, 15, 8, 0, 0));
        Assert.Equal(new DateTime(2026, 10, 15, 6, 30, 0), times!.Departure);
        Assert.Equal(TimeSpan.FromMinutes(90), times.Arrival - times.Departure);
    }
}
