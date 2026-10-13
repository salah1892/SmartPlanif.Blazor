using Dapper;
using Microsoft.Extensions.Options;
using SmartPlanif.Application.Abstractions;

namespace SmartPlanif.Infrastructure.MySql;

/// <summary>Relevés de diagnostic en LECTURE SEULE dans la base : aucune écriture, aucun verrou long.</summary>
public sealed class MySqlDiagnostics(IDbConnectionFactory db, IOptions<TripSourceOptions> options) : IDatabaseDiagnostics
{
    private sealed class LabelCountRow { public string Label { get; set; } = ""; public long Count { get; set; } }
    private sealed class LabelRow { public string Label { get; set; } = ""; }
    private sealed class TotalRow { public long Total { get; set; } }
    private sealed class RangeRow { public DateTime? FirstDay { get; set; } public DateTime? LastDay { get; set; } }
    private sealed class BusRow { public long Total { get; set; } public long Flagged { get; set; } }

    private sealed class DayRow
    {
        public long Total { get; set; } public long ActiveLine { get; set; } public long NoStopTimes { get; set; }
        public long NoStopTimesNoItinerary { get; set; } public long Planned { get; set; } public long Realized { get; set; }
    }

    private sealed class ColumnsRow
    {
        public long BusPrNonNull { get; set; } public long BusPrPos { get; set; }
        public long DriverPrNonNull { get; set; } public long DriverPrPos { get; set; }
        public long ReceiverPrNonNull { get; set; } public long ReceiverPrPos { get; set; }
        public long BusReNonNull { get; set; } public long BusRePos { get; set; }
        public long DriverReNonNull { get; set; } public long DriverRePos { get; set; }
        public long ReceiverReNonNull { get; set; } public long ReceiverRePos { get; set; }
    }

    private sealed class LineRow { public long LineId { get; set; } public string Label { get; set; } = ""; public long Trips { get; set; } }

    private sealed class StopRow
    {
        public long LineId { get; set; } public long Sequence { get; set; }
        public long? Minutes { get; set; } public long? Km { get; set; }
    }

    /// <inheritdoc />
    public async Task<RawDiagnostics> CollectAsync(DateOnly date, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var tableDay = TripDayMapper.Map(date, options.Value.TemplateYear);
        var day = TripSql.Day(tableDay);

        async Task<IReadOnlyList<DiagnosticCount>> Counts(string sql, object? parameters = null) =>
            (await connection.QueryAsync<LabelCountRow>(new CommandDefinition(sql, parameters, cancellationToken: ct)))
                .Select(r => new DiagnosticCount(r.Label, r.Count)).ToList();

        var total = (await connection.QueryAsync<TotalRow>(new CommandDefinition(
            "SELECT COUNT(*) AS Total FROM trips", cancellationToken: ct))).First().Total;

        var range = (await connection.QueryAsync<RangeRow>(new CommandDefinition(
            "SELECT MIN(DATE(COALESCE(DEDATED, TIME_DEPART))) AS FirstDay, MAX(DATE(COALESCE(DEDATED, TIME_DEPART))) AS LastDay FROM trips",
            cancellationToken: ct))).First();

        var dayRow = (await connection.QueryAsync<DayRow>(new CommandDefinition($"""
            SELECT CAST(COUNT(*) AS SIGNED) AS Total,
                   CAST(COALESCE(SUM(CASE WHEN l.DENUMLI IS NOT NULL AND l.DEACTIF = 1 THEN 1 ELSE 0 END), 0) AS SIGNED) AS ActiveLine,
                   CAST(COALESCE(SUM(CASE WHEN NOT EXISTS (SELECT 1 FROM stop_times s WHERE s.TRIP_ID = t.ID) THEN 1 ELSE 0 END), 0) AS SIGNED) AS NoStopTimes,
                   CAST(COALESCE(SUM(CASE WHEN NOT EXISTS (SELECT 1 FROM stop_times s WHERE s.TRIP_ID = t.ID)
                                           AND NOT EXISTS (SELECT 1 FROM dritin i WHERE i.DENUMLI = t.DENUMLI) THEN 1 ELSE 0 END), 0) AS SIGNED) AS NoStopTimesNoItinerary,
                   CAST(COALESCE(SUM(CASE WHEN COALESCE(t.BUS_PR, 0) > 0 OR COALESCE(t.CHAUFF_PR, 0) > 0 OR COALESCE(t.REC_PR, 0) > 0 THEN 1 ELSE 0 END), 0) AS SIGNED) AS Planned,
                   CAST(COALESCE(SUM(CASE WHEN COALESCE(t.BUS_RE, 0) > 0 OR COALESCE(t.CHAUFF_RE, 0) > 0 OR COALESCE(t.REC_RE, 0) > 0 THEN 1 ELSE 0 END), 0) AS SIGNED) AS Realized
            FROM trips t LEFT JOIN drligne l ON l.DENUMLI = t.DENUMLI
            WHERE {TripSql.DayFilter}
            """, day, cancellationToken: ct))).First();

        // Colonnes de ressources : « non NULL » et « valides (> 0) » séparément, pour repérer les valeurs 0.
        var c = (await connection.QueryAsync<ColumnsRow>(new CommandDefinition($"""
            SELECT CAST(COALESCE(SUM(t.BUS_PR IS NOT NULL), 0) AS SIGNED) AS BusPrNonNull, CAST(COALESCE(SUM(COALESCE(t.BUS_PR, 0) > 0), 0) AS SIGNED) AS BusPrPos,
                   CAST(COALESCE(SUM(t.CHAUFF_PR IS NOT NULL), 0) AS SIGNED) AS DriverPrNonNull, CAST(COALESCE(SUM(COALESCE(t.CHAUFF_PR, 0) > 0), 0) AS SIGNED) AS DriverPrPos,
                   CAST(COALESCE(SUM(t.REC_PR IS NOT NULL), 0) AS SIGNED) AS ReceiverPrNonNull, CAST(COALESCE(SUM(COALESCE(t.REC_PR, 0) > 0), 0) AS SIGNED) AS ReceiverPrPos,
                   CAST(COALESCE(SUM(t.BUS_RE IS NOT NULL), 0) AS SIGNED) AS BusReNonNull, CAST(COALESCE(SUM(COALESCE(t.BUS_RE, 0) > 0), 0) AS SIGNED) AS BusRePos,
                   CAST(COALESCE(SUM(t.CHAUFF_RE IS NOT NULL), 0) AS SIGNED) AS DriverReNonNull, CAST(COALESCE(SUM(COALESCE(t.CHAUFF_RE, 0) > 0), 0) AS SIGNED) AS DriverRePos,
                   CAST(COALESCE(SUM(t.REC_RE IS NOT NULL), 0) AS SIGNED) AS ReceiverReNonNull, CAST(COALESCE(SUM(COALESCE(t.REC_RE, 0) > 0), 0) AS SIGNED) AS ReceiverRePos
            FROM trips t WHERE {TripSql.DayFilter}
            """, day, cancellationToken: ct))).First();
        var columns = new List<ResourceColumnStat>
        {
            new("BUS_PR", c.BusPrNonNull, c.BusPrPos), new("CHAUFF_PR", c.DriverPrNonNull, c.DriverPrPos), new("REC_PR", c.ReceiverPrNonNull, c.ReceiverPrPos),
            new("BUS_RE", c.BusReNonNull, c.BusRePos), new("CHAUFF_RE", c.DriverReNonNull, c.DriverRePos), new("REC_RE", c.ReceiverReNonNull, c.ReceiverRePos),
        };

        // Durées par ligne : valeurs lues dans dritin et durée retenue (pour contrôler le sens de DEDURTR).
        var lines = (await connection.QueryAsync<LineRow>(new CommandDefinition($"""
            SELECT t.DENUMLI AS LineId, COALESCE(MAX(l.DENOMLI), CONCAT('Ligne ', t.DENUMLI)) AS Label, COUNT(*) AS Trips
            FROM trips t LEFT JOIN drligne l ON l.DENUMLI = t.DENUMLI
            WHERE {TripSql.DayFilter} GROUP BY t.DENUMLI ORDER BY COUNT(*) DESC, t.DENUMLI LIMIT 60
            """, day, cancellationToken: ct))).ToList();
        var lineIds = lines.Select(l => l.LineId).ToList();
        var stops = lineIds.Count == 0
            ? new List<StopRow>()
            : (await connection.QueryAsync<StopRow>(new CommandDefinition(
                "SELECT i.DENUMLI AS LineId, i.DENUMLG AS Sequence, i.DEDURTR AS Minutes, i.DEKMSTA AS Km FROM dritin i WHERE i.DENUMLI IN @LineIds ORDER BY i.DENUMLI, i.DENUMLG",
                new { LineIds = lineIds }, cancellationToken: ct))).ToList();
        var stopsByLine = stops.GroupBy(s => s.LineId).ToDictionary(g => g.Key, g => g.ToList());

        var lineDurations = lines.Select(line =>
        {
            if (!stopsByLine.TryGetValue(line.LineId, out var lineStops))
                return new LineDurationInfo(line.LineId, line.Label, line.Trips, 0, null, null, null, null, null, "AUCUN itinéraire dans dritin", null);

            var minutes = lineStops.Where(s => s.Minutes is not null).Select(s => s.Minutes!.Value).ToList();
            var resolved = ItineraryDuration.Resolve(
                lineStops.Select(s => new ItineraryStop(s.Sequence, s.Minutes, s.Km)).ToList(), options.Value.ItineraryDuration);
            return new LineDurationInfo(
                line.LineId, line.Label, line.Trips, lineStops.Count,
                minutes.Count > 0 ? minutes.Min() : null, minutes.Count > 0 ? minutes.Max() : null, minutes.Count > 0 ? minutes.Sum() : null,
                lineStops.Max(s => s.Km), resolved.Duration?.TotalMinutes, resolved.Method, resolved.SpeedKmh);
        }).ToList();

        var buses = (await connection.QueryAsync<BusRow>(new CommandDefinition(
            "SELECT CAST(COUNT(*) AS SIGNED) AS Total, CAST(COALESCE(SUM(DEACTIF = 1), 0) AS SIGNED) AS Flagged FROM drvehic",
            cancellationToken: ct))).First();

        var states = await Counts($"""
            SELECT COALESCE(CAST(t.ETAT AS CHAR), '(vide)') AS Label, COUNT(*) AS Count
            FROM trips t WHERE {TripSql.DayFilter} GROUP BY COALESCE(CAST(t.ETAT AS CHAR), '(vide)') ORDER BY 2 DESC
            """, day);

        var qualifications = await Counts(
            "SELECT COALESCE(DECQUAL, '(vide)') AS Label, COUNT(*) AS Count FROM dragent GROUP BY COALESCE(DECQUAL, '(vide)') ORDER BY 2 DESC");
        var restDays = await Counts(
            "SELECT COALESCE(CAST(DECJOUR AS CHAR), '(vide)') AS Label, COUNT(*) AS Count FROM dragent GROUP BY COALESCE(CAST(DECJOUR AS CHAR), '(vide)') ORDER BY 1");
        var busActivity = await Counts("""
            SELECT CASE WHEN DEACTIF = 1 THEN 'actif (DEACTIF = 1)' WHEN DEACTIF = 0 THEN 'DEACTIF = 0' ELSE '(vide)' END AS Label, COUNT(*) AS Count
            FROM drvehic GROUP BY 1 ORDER BY 2 DESC
            """);
        var busCategories = await Counts("""
            SELECT COALESCE(c.DECATEG, CONCAT('Catégorie ', v.DECATVH), '(vide)') AS Label, COUNT(*) AS Count
            FROM drvehic v LEFT JOIN drcatve c ON c.DECATVH = v.DECATVH GROUP BY 1 ORDER BY 2 DESC
            """);
        var lineTypes = await Counts($"""
            SELECT COALESCE(ty.DELTYLI, CONCAT('Type ', l.DECTYLI), '(sans type)') AS Label, COUNT(*) AS Count
            FROM trips t JOIN drligne l ON l.DENUMLI = t.DENUMLI LEFT JOIN drtyli ty ON ty.DECTYLI = l.DECTYLI
            WHERE {TripSql.DayFilter} GROUP BY 1 ORDER BY 2 DESC
            """, day);

        var samples = (await connection.QueryAsync<LabelRow>(new CommandDefinition($"""
            SELECT CONCAT('ID ', t.ID, ' | DEDATED=', COALESCE(CAST(t.DEDATED AS CHAR), 'NULL'),
                          ' | TIME_DEPART=', COALESCE(CAST(t.TIME_DEPART AS CHAR), 'NULL'),
                          ' | arrivée stop_times=', COALESCE(CAST({TripSql.ArrivalSubquery} AS CHAR), 'NULL'),
                          ' | ETAT=', COALESCE(CAST(t.ETAT AS CHAR), 'NULL'),
                          ' | PR=', COALESCE(CAST(t.BUS_PR AS CHAR), 'NULL'), '/', COALESCE(CAST(t.CHAUFF_PR AS CHAR), 'NULL'), '/', COALESCE(CAST(t.REC_PR AS CHAR), 'NULL'),
                          ' | RE=', COALESCE(CAST(t.BUS_RE AS CHAR), 'NULL'), '/', COALESCE(CAST(t.CHAUFF_RE AS CHAR), 'NULL'), '/', COALESCE(CAST(t.REC_RE AS CHAR), 'NULL')) AS Label
            FROM trips t WHERE {TripSql.DayFilter} ORDER BY t.TIME_DEPART LIMIT 5
            """, day, cancellationToken: ct))).Select(r => r.Label).ToList();

        var present = (await connection.QueryAsync<LabelRow>(new CommandDefinition(
            "SELECT table_name AS Label FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'resource_unavailability'",
            cancellationToken: ct))).Select(r => r.Label).ToList();
        var missing = new List<string>();
        if (present.Count == 0) missing.Add("resource_unavailability");

        return new RawDiagnostics(
            TripDayMapper.Describe(date, options.Value.TemplateYear), total, range.FirstDay, range.LastDay,
            dayRow.Total, dayRow.ActiveLine, dayRow.NoStopTimes, dayRow.NoStopTimesNoItinerary, dayRow.Planned, dayRow.Realized,
            buses.Total, buses.Flagged, columns, lineDurations,
            states, qualifications, restDays, busActivity, busCategories, lineTypes, samples, missing);
    }
}
