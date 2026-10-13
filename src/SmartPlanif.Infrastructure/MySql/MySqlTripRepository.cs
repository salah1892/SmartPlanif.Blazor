using Dapper;
using Microsoft.Extensions.Options;
using SmartPlanif.Application.Abstractions;
using SmartPlanif.Domain.Entities;

namespace SmartPlanif.Infrastructure.MySql;

/// <summary>
/// Lecture des voyages dans la table trips (jour de service = DEDATED), avec leurs ressources prévues (*_PR) et réelles (*_RE).
/// Requêtes validées sur une copie du schéma fourni avec des données de test ; à re-valider sur vos données réelles
/// (page « Diagnostic »).
/// </summary>
public sealed class MySqlTripRepository(IDbConnectionFactory db, IOptions<TripSourceOptions> options) : ITripRepository
{
    /// <summary>Ligne brute lue en base (propriétés modifiables, requis par Dapper).</summary>
    private sealed class Row
    {
        public long Id { get; set; }
        public long LineId { get; set; }
        public string LineLabel { get; set; } = "";
        public long? LineTypeId { get; set; }
        public DateTime Departure { get; set; }
        public DateTime? Arrival { get; set; }
        public double DistanceKm { get; set; }
        public long? CenterId { get; set; }
        public long? DelegationId { get; set; }
        public long? OriginStationId { get; set; }
        public long? DestinationStationId { get; set; }
        public long? BusId { get; set; }
        public long? DriverId { get; set; }
        public long? ReceiverId { get; set; }
        public long? RealBusId { get; set; }
        public long? RealDriverId { get; set; }
        public long? RealReceiverId { get; set; }
        public DateTime? ArrivalFromItinerary { get; set; }

        /// <summary>
        /// Convertit en voyage sur le JOUR DEMANDÉ (<paramref name="requestedDay"/>) ; null si les horaires sont inexploitables
        /// (le voyage est alors écarté). En mode année modèle, les horaires de la table sont ramenés sur le jour demandé.
        /// </summary>
        public Trip? ToTrip(DateOnly requestedDay)
        {
            var times = TripTimeNormalizer.Normalize(requestedDay.ToDateTime(TimeOnly.MinValue), Departure, Arrival ?? ArrivalFromItinerary);
            return times is null
                ? null
                : new Trip(Id, LineId, LineLabel, LineTypeId, times.Departure, times.Arrival, DistanceKm, CenterId, DelegationId,
                           OriginStationId, DestinationStationId, null);
        }
    }

    private string BuildSql(bool withExcludedStates)
    {
        var lineCondition = options.Value.RequireActiveLine ? "AND l.DEACTIF = 1" : "";
        var stateCondition = withExcludedStates ? "AND COALESCE(t.ETAT, -1) NOT IN @ExcludedStates" : "";
        return $"""
            SELECT t.ID AS Id, t.DENUMLI AS LineId,
                   COALESCE(l.DENOMLI, CONCAT('Ligne ', t.DENUMLI)) AS LineLabel,
                   l.DECTYLI AS LineTypeId,
                   t.TIME_DEPART AS Departure,
                   {TripSql.ArrivalSubquery} AS Arrival,
                   COALESCE((SELECT MAX(s.SHAPE_DIST_TRAVELED) FROM stop_times s WHERE s.TRIP_ID = t.ID), l.DENBRKM, 0) AS DistanceKm,
                   l.DECCENT AS CenterId, l.DECDELEG AS DelegationId,
                   (SELECT s.DECSTAT FROM stop_times s WHERE s.TRIP_ID = t.ID ORDER BY s.STOP_SEQUENCE ASC  LIMIT 1) AS OriginStationId,
                   (SELECT s.DECSTAT FROM stop_times s WHERE s.TRIP_ID = t.ID ORDER BY s.STOP_SEQUENCE DESC LIMIT 1) AS DestinationStationId,
                   NULLIF(t.BUS_PR, 0) AS BusId, NULLIF(t.CHAUFF_PR, 0) AS DriverId, NULLIF(t.REC_PR, 0) AS ReceiverId,
                   NULLIF(t.BUS_RE, 0) AS RealBusId, NULLIF(t.CHAUFF_RE, 0) AS RealDriverId, NULLIF(t.REC_RE, 0) AS RealReceiverId
            FROM trips t
            JOIN drligne l ON l.DENUMLI = t.DENUMLI {lineCondition}
            WHERE {TripSql.DayFilter}
              AND t.TIME_DEPART IS NOT NULL
              {stateCondition}
            ORDER BY t.TIME_DEPART, t.ID
            """;
    }

    private sealed class ItineraryRow
    {
        public long LineId { get; set; }
        public long Sequence { get; set; }
        public long? Minutes { get; set; }
        public long? Km { get; set; }
    }

    /// <summary>
    /// Voyages sans heure d'arrivée dans stop_times (cas des voyages futurs) : l'arrivée est déduite de la durée de l'itinéraire
    /// de leur ligne (table dritin). Un voyage dont la ligne n'a pas d'itinéraire reste sans arrivée et est écarté.
    /// </summary>
    private async Task ApplyItineraryFallbackAsync(System.Data.Common.DbConnection connection, List<Row> rows, CancellationToken ct)
    {
        var lineIds = rows.Where(r => r.Arrival is null).Select(r => r.LineId).Distinct().ToList();
        if (lineIds.Count == 0) return;

        var stops = await connection.QueryAsync<ItineraryRow>(new CommandDefinition(
            "SELECT i.DENUMLI AS LineId, i.DENUMLG AS Sequence, i.DEDURTR AS Minutes, i.DEKMSTA AS Km FROM dritin i WHERE i.DENUMLI IN @LineIds ORDER BY i.DENUMLI, i.DENUMLG",
            new { LineIds = lineIds }, cancellationToken: ct));

        var durations = stops.GroupBy(x => x.LineId).ToDictionary(
            g => g.Key,
            g => ItineraryDuration.Resolve(g.Select(x => new ItineraryStop(x.Sequence, x.Minutes, x.Km)).ToList(), options.Value.ItineraryDuration).Duration);

        foreach (var row in rows.Where(r => r.Arrival is null))
            if (durations.TryGetValue(row.LineId, out var duration) && duration is { } d)
                row.ArrivalFromItinerary = row.Departure + d;
    }

    /// <summary>Lit les lignes d'UN jour (converti en jour de la table si une année modèle est configurée).</summary>
    private async Task<List<Row>> QueryDayAsync(System.Data.Common.DbConnection connection, DateOnly requestedDay, CancellationToken ct)
    {
        var excluded = options.Value.ExcludedStates;
        var sql = BuildSql(excluded.Count > 0);
        var tableDay = TripDayMapper.Map(requestedDay, options.Value.TemplateYear);
        var from = tableDay.ToDateTime(TimeOnly.MinValue);

        object parameters = excluded.Count > 0
            ? new { From = from, ToExclusive = from.AddDays(1), ExcludedStates = excluded }
            : TripSql.Day(tableDay);

        var rows = (await connection.QueryAsync<Row>(new CommandDefinition(sql, parameters, cancellationToken: ct))).ToList();
        await ApplyItineraryFallbackAsync(connection, rows, ct);
        return rows;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<Trip>> GetTripsOfDayAsync(DateOnly date, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        return (await QueryDayAsync(connection, date, ct)).Select(r => r.ToTrip(date)).OfType<Trip>().ToList();
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AssignedTrip>> GetAssignedAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        var result = new List<AssignedTrip>();
        await using var connection = await db.OpenAsync(ct);
        for (var day = from; day <= to; day = day.AddDays(1))
            foreach (var row in await QueryDayAsync(connection, day, ct))
            {
                var trip = row.ToTrip(day);
                if (trip is null) continue;

                // Ressources réelles toutes connues : le voyage a été effectué (fait, pas plan).
                if (row.RealBusId is { } rb && row.RealDriverId is { } rd && row.RealReceiverId is { } rr)
                    result.Add(new AssignedTrip(trip, rb, rd, rr, IsRealized: true));
                // Sinon, plan prévu complet.
                else if (row.BusId is { } pb && row.DriverId is { } pd && row.ReceiverId is { } pr)
                    result.Add(new AssignedTrip(trip, pb, pd, pr, IsRealized: false));
            }
        return result;
    }
}
