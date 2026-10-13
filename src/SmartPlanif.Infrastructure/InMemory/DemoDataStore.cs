using SmartPlanif.Application.Abstractions;
using SmartPlanif.Domain.Allocation;
using SmartPlanif.Domain.Common;
using SmartPlanif.Domain.Entities;

namespace SmartPlanif.Infrastructure.InMemory;

/// <summary>
/// Source de données de démonstration (aucune base requise) : génère de façon déterministe les voyages de n'importe quelle date.
/// Trois types de ligne (urbain, interurbain entre gouvernorats, banlieue) et trois catégories de bus.
/// Les lignes interurbaines n'ont que peu de bus confort : la politique de catégorie et l'ordre d'allocation comptent.
/// </summary>
public sealed class DemoDataStore : ITripRepository, IResourceRepository, IAssignmentRepository, IReferenceDataRepository, IDatabaseDiagnostics
{
    private const long Urban = 1, InterCity = 2, Suburban = 3;
    private const long Standard = 1, Comfort = 2, Articulated = 3;

    /// <summary>Nombre d'agents de la démonstration (tous polyvalents).</summary>
    private const int AgentCount = 28;

    private static readonly (long Id, string Label,string LabelArabic)[] LineTypes =
    {
        (Urban, "URBAIN","'حضري'"), (InterCity, "INTERURBAIN (entre gouvernorats)","جهوي"), (Suburban, "BANLIEUE","الأحواز"),
    };

    private static readonly (long Id, string Label,string LabelArabic)[] Categories =
    {
        (Standard, "BUS STANDARD","حافلة نقل حضري عادية"), (Comfort, "BUS CONFORT","حافلة نقل بين المدن مكيفة"), (Articulated, "BUS ARTICULÉ","حافلة نقل حضري مزدوجة"),
    };

    private static readonly (long LineId, string Label, long TypeId, int DurationMinutes, double Km, int TripCount, int SpacingMinutes)[] Lines =
    {
        (10, "Ligne 10 Centre-Gare", Urban, 75, 22, 5, 170),
        (11, "Ligne 11 Cité Nord", Urban, 60, 18, 5, 170),
        (12, "Ligne 12 Hôpital", Urban, 80, 25, 5, 170),
        (17, "Ligne 17 Université", Urban, 70, 20, 5, 170),
        (13, "Ligne 13 Kairouan-Tunis", InterCity, 210, 160, 4, 240),
        (14, "Ligne 14 Kairouan-Sousse", InterCity, 150, 75, 4, 240),
        (15, "Ligne 15 Banlieue Est", Suburban, 100, 40, 4, 200),
        (16, "Ligne 16 Banlieue Ouest", Suburban, 110, 45, 4, 200),
    };

    /// <summary>Plan PRÉVU enregistré (équivalent des colonnes BUS_PR, CHAUFF_PR, REC_PR de trips), par identifiant de voyage.</summary>
    private readonly Dictionary<long, (long Bus, long Driver, long Receiver)> _planned = new();

    /// <summary>Ressources RÉELLES (équivalent des colonnes *_RE) : voyages déjà effectués.</summary>
    private readonly Dictionary<long, (long Bus, long Driver, long Receiver)> _realized = new();

    /// <summary>Simule un voyage déjà effectué avec ses ressources réelles (pour les tests et la démonstration).</summary>
    public void MarkRealized(long tripId, long busId, long driverId, long receiverId) =>
        _realized[tripId] = (busId, driverId, receiverId);

    /// <summary>Politique par défaut de la démo : règles de compatibilité type de ligne -> catégorie de bus.</summary>
    public static AllocationPolicy CreateDemoPolicy() => new()
    {
        CategoryRules =
        {
            new LineTypeCategoryRule { LineTypeId = Urban, AllowedCategoryIds = { Standard, Comfort, Articulated }, PreferredCategoryIds = { Standard } },
            new LineTypeCategoryRule { LineTypeId = InterCity, AllowedCategoryIds = { Comfort }, PreferredCategoryIds = { Comfort } },
            new LineTypeCategoryRule { LineTypeId = Suburban, AllowedCategoryIds = { Standard, Comfort }, PreferredCategoryIds = { Standard } },
        },
    };

    // ---------------------------------------------------------------- voyages
    /// <inheritdoc />
    public Task<IReadOnlyList<Trip>> GetTripsOfDayAsync(DateOnly date, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<Trip>>(GenerateTrips(date));

    /// <inheritdoc />
    public Task<IReadOnlyList<AssignedTrip>> GetAssignedAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new List<AssignedTrip>();
        for (var day = from; day <= to; day = day.AddDays(1))
            foreach (var trip in GenerateTrips(day))
            {
                if (_realized.TryGetValue(trip.Id, out var real))
                    result.Add(new AssignedTrip(trip, real.Bus, real.Driver, real.Receiver, IsRealized: true));
                else if (_planned.TryGetValue(trip.Id, out var plan))
                    result.Add(new AssignedTrip(trip, plan.Bus, plan.Driver, plan.Receiver, IsRealized: false));
            }
        return Task.FromResult<IReadOnlyList<AssignedTrip>>(result);
    }

    private static List<Trip> GenerateTrips(DateOnly date)
    {
        var trips = new List<Trip>();
        var midnight = date.ToDateTime(TimeOnly.MinValue);
        var sequence = 0;

        for (var lineIndex = 0; lineIndex < Lines.Length; lineIndex++)
        {
            var line = Lines[lineIndex];
            var jitter = new Random(date.DayNumber * 31 + (int)line.LineId);   // même date => mêmes voyages
            var centerId = line.LineId % 2 == 0 ? 1L : 2L;

            for (var k = 0; k < line.TripCount; k++)
            {
                var departure = midnight.AddMinutes(330 + lineIndex * 13 + k * line.SpacingMinutes + jitter.Next(0, 40));
                trips.Add(new Trip(
                    Id: date.DayNumber * 1000L + (++sequence),
                    LineId: line.LineId, LineLabel: line.Label, LineTypeId: line.TypeId,
                    Departure: departure, Arrival: departure.AddMinutes(line.DurationMinutes), DistanceKm: line.Km,
                    CenterId: centerId, DelegationId: centerId,
                    OriginStationId: line.LineId * 10 + k % 2, DestinationStationId: line.LineId * 10 + (k + 1) % 2,
                    ExpectedPassengers: null));
            }
        }
        return trips;
    }

    // ---------------------------------------------------------------- ressources
    /// <inheritdoc />
    public Task<ResourcePool> GetPoolAsync(DateOnly from, DateOnly to, CancellationToken ct) =>
        Task.FromResult(BuildPool(from));

    private static ResourcePool BuildPool(DateOnly from)
    {
        var buses = new List<Bus>();
        for (var i = 1; i <= 12; i++)
        {
            var category = i <= 6 ? Standard : i <= 9 ? Comfort : Standard;
            if (i is 10 or 11) category = Articulated;
            var center = 1 + i % 2;
            buses.Add(new Bus(i, $"{100 + i} TU {4500 + i}", Active: i != 12, Seats: 40, Standing: 40,
                category, center, center, new DateTime(2014 + i % 9, 1, 1)));
        }

        // Comme dans votre base : tous les agents peuvent être chauffeur OU receveur (DECQUAL = genre, sans rapport avec le poste).
        var agents = new List<Agent>();
        for (var k = 0; k < AgentCount; k++)
        {
            var center = 1 + k % 2;
            agents.Add(new Agent(101 + k, $"Agent {101 + k}", AgentRole.Both, (DayOfWeek)(k % 7), center, center));
        }

        // Indisponibilités du jour central de la fenêtre [J-1, J+1] : maintenance du bus 2, congé du chauffeur 105.
        var day = from.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var unavailabilities = new List<Unavailability>
        {
            new(ResourceKind.Bus, 2, new TimeInterval(day.AddHours(12), day.AddHours(18)), "Maintenance"),
            new(ResourceKind.Agent, 105, new TimeInterval(day, day.AddDays(1)), "Congé"),
        };
        return new ResourcePool(buses, agents, unavailabilities);
    }

    // ---------------------------------------------------------------- plan prévu (équivalent de trips.*_PR)
    /// <inheritdoc />
    public Task<DayPlanSaveResult> SaveDayPlanAsync(DayPlanToSave plan, string user, CancellationToken ct)
    {
        // Même garde-fou que la base : un voyage effectué n'est jamais modifié ; en cas d'écart, rien n'est écrit.
        if (plan.Assignments.Any(a => _realized.ContainsKey(a.Trip.Id)))
            return Task.FromResult(new DayPlanSaveResult(false, "Écriture annulée : un voyage déjà effectué ne peut pas être modifié.", 0, 0));

        foreach (var a in plan.Assignments) _planned[a.Trip.Id] = (a.Bus.Id, a.Driver.Id, a.Receiver.Id);

        var cleared = 0;
        if (plan.ClearUnassigned)
            foreach (var id in plan.UnassignedTripIds)
                if (!_realized.ContainsKey(id) && _planned.Remove(id)) cleared++;

        return Task.FromResult(new DayPlanSaveResult(true,
            $"Plan enregistré : {plan.Assignments.Count} voyage(s) affecté(s), {cleared} plan(s) précédent(s) vidé(s).", plan.Assignments.Count, cleared));
    }

    /// <inheritdoc />
    public Task<int> ClearDayPlanAsync(DateOnly date, CancellationToken ct)
    {
        var cleared = 0;
        foreach (var trip in GenerateTrips(date))
            if (!_realized.ContainsKey(trip.Id) && _planned.Remove(trip.Id)) cleared++;
        return Task.FromResult(cleared);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<SavedTripAssignment>> GetSavedPlanAsync(DateOnly date, CancellationToken ct)
    {
        var pool = BuildPool(date.AddDays(-1));
        var buses = pool.Buses.ToDictionary(b => b.Id);
        var agents = pool.Agents.ToDictionary(a => a.Id);

        var rows = GenerateTrips(date).Select(trip =>
        {
            _planned.TryGetValue(trip.Id, out var plan);
            var hasPlan = _planned.ContainsKey(trip.Id);
            var hasReal = _realized.TryGetValue(trip.Id, out var real);
            return new SavedTripAssignment(
                trip.Id, trip.LineLabel, trip.Departure, trip.Arrival,
                hasPlan ? plan.Bus : null, hasPlan ? buses[plan.Bus].Plate : null,
                hasPlan ? plan.Driver : null, hasPlan ? agents[plan.Driver].Name : null,
                hasPlan ? plan.Receiver : null, hasPlan ? agents[plan.Receiver].Name : null,
                hasReal ? buses[real.Bus].Plate : null, hasReal ? agents[real.Driver].Name : null, hasReal ? agents[real.Receiver].Name : null,
                hasReal);
        }).ToList();
        return Task.FromResult<IReadOnlyList<SavedTripAssignment>>(rows);
    }

    // ---------------------------------------------------------------- diagnostic de démonstration
    /// <inheritdoc />
    public Task<RawDiagnostics> CollectAsync(DateOnly date, CancellationToken ct)
    {
        var trips = GenerateTrips(date);
        var pool = BuildPool(date.AddDays(-1));
        var ids = trips.Select(t => t.Id).ToHashSet();

        RawDiagnostics raw = new(
            DayMapping: $"{date:dd/MM/yyyy} -> jour exact (données de démonstration générées)",
            TotalTripsInTable: trips.Count, FirstTripDay: date.ToDateTime(TimeOnly.MinValue), LastTripDay: date.ToDateTime(TimeOnly.MinValue),
            TripsOfDay: trips.Count, TripsOfDayOnActiveLine: trips.Count, TripsOfDayWithoutStopTimes: 0, TripsOfDayWithoutStopTimesAndItinerary: 0,
            TripsOfDayWithPlannedResources: _planned.Keys.Count(ids.Contains),
            TripsOfDayWithRealResources: _realized.Keys.Count(ids.Contains),
            BusesTotal: pool.Buses.Count, BusesFlaggedActive: pool.Buses.Count(b => b.Active),
            ResourceColumns: Array.Empty<ResourceColumnStat>(), LineDurations: Array.Empty<LineDurationInfo>(),
            TripStates: new[] { new DiagnosticCount("(démo)", trips.Count) },
            AgentQualifications: new[] { new DiagnosticCount("(démo : tous polyvalents)", pool.Agents.Count) },
            AgentRestDays: Array.Empty<DiagnosticCount>(),
            BusActivity: new[] { new DiagnosticCount("actif", pool.Buses.Count(b => b.Active)), new DiagnosticCount("inactif", pool.Buses.Count(b => !b.Active)) },
            BusCategories: pool.Buses.GroupBy(b => b.CategoryId).Select(g => new DiagnosticCount($"Catégorie {g.Key}", g.Count())).ToList(),
            LineTypesOfDay: trips.GroupBy(t => t.LineTypeId).Select(g => new DiagnosticCount($"Type {g.Key}", g.Count())).ToList(),
            SampleTrips: trips.Take(3).Select(t => $"ID {t.Id} | départ {t.Departure:yyyy-MM-dd HH:mm} | arrivée {t.Arrival:HH:mm} (données générées)").ToList(),
            MissingOptionalTables: Array.Empty<string>());
        return Task.FromResult(raw);
    }

    // ---------------------------------------------------------------- référentiels
    /// <inheritdoc />
    public Task<IReadOnlyList<ReferenceItem>> GetLineTypesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ReferenceItem>>(LineTypes.Select(t => new ReferenceItem(t.Id, t.Label,t.Label)).ToList());

    /// <inheritdoc />
    public Task<IReadOnlyList<ReferenceItem>> GetBusCategoriesAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<ReferenceItem>>(Categories.Select(c => new ReferenceItem(c.Id, c.Label,c.LabelArabic)).ToList());
}
