using System.Data.Common;
using Dapper;
using SmartPlanif.Application.Abstractions;

namespace SmartPlanif.Infrastructure.MySql;

/// <summary>
/// Écriture et lecture du plan PRÉVU directement dans la table trips (colonnes BUS_PR, CHAUFF_PR, REC_PR).
/// Les colonnes réelles (*_RE) ne sont JAMAIS modifiées, et un voyage effectué (trois ressources réelles renseignées) n'est jamais touché.
/// </summary>
public sealed class MySqlAssignmentRepository(IDbConnectionFactory db, Microsoft.Extensions.Options.IOptions<TripSourceOptions> options) : IAssignmentRepository
{
    private sealed class SavedRow
    {
        public long TripId { get; set; }
        public string LineLabel { get; set; } = "";
        public DateTime Departure { get; set; }
        public DateTime? Arrival { get; set; }
        public long? BusId { get; set; }
        public string? BusPlate { get; set; }
        public long? DriverId { get; set; }
        public string? DriverName { get; set; }
        public long? ReceiverId { get; set; }
        public string? ReceiverName { get; set; }
        public string? RealBusPlate { get; set; }
        public string? RealDriverName { get; set; }
        public string? RealReceiverName { get; set; }
        public long HasReal { get; set; }
    }

    /// <inheritdoc />
    public async Task<DayPlanSaveResult> SaveDayPlanAsync(DayPlanToSave plan, string user, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            var rows = plan.Assignments
                .Select(a => new { TripId = a.Trip.Id, BusId = a.Bus.Id, DriverId = a.Driver.Id, ReceiverId = a.Receiver.Id })
                .ToList();

            // UPDATE par voyage, dans une transaction. MySqlConnector renvoie par défaut le nombre de lignes TROUVÉES
            // (et non « modifiées ») : réécrire les mêmes valeurs compte donc bien pour 1.
            var updated = await connection.ExecuteAsync(new CommandDefinition(
                $"UPDATE trips t SET t.BUS_PR = @BusId, t.CHAUFF_PR = @DriverId, t.REC_PR = @ReceiverId WHERE t.ID = @TripId AND {TripSql.NotRealized}",
                rows, transaction, cancellationToken: ct));

            if (updated != rows.Count)
            {
                await transaction.RollbackAsync(ct);
                return new DayPlanSaveResult(false,
                    $"Écriture annulée : {rows.Count} voyage(s) à écrire mais {updated} mis à jour (voyage supprimé ou déjà effectué ?). Rien n'a été modifié.", 0, 0);
            }

            var cleared = 0;
            if (plan.ClearUnassigned && plan.UnassignedTripIds.Count > 0)
                cleared = await connection.ExecuteAsync(new CommandDefinition(
                    $"UPDATE trips t SET t.BUS_PR = NULL, t.CHAUFF_PR = NULL, t.REC_PR = NULL WHERE t.ID IN @Ids AND {TripSql.NotRealized}",
                    new { Ids = plan.UnassignedTripIds }, transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);
            return new DayPlanSaveResult(true,
                $"Plan enregistré dans trips : {rows.Count} voyage(s) affecté(s), {cleared} plan(s) précédent(s) vidé(s).", rows.Count, cleared);
        }
        catch (DbException ex)
        {
            // Par exemple une clé étrangère (agent ou bus inexistant) : la transaction est annulée, rien n'est écrit.
            await transaction.RollbackAsync(ct);
            return new DayPlanSaveResult(false, "Écriture annulée par la base : " + ex.Message, 0, 0);
        }
    }

    /// <inheritdoc />
    public async Task<int> ClearDayPlanAsync(DateOnly date, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            $"UPDATE trips t SET t.BUS_PR = NULL, t.CHAUFF_PR = NULL, t.REC_PR = NULL WHERE {TripSql.DayFilter} AND {TripSql.NotRealized} " +
            "AND (COALESCE(t.BUS_PR, 0) > 0 OR COALESCE(t.CHAUFF_PR, 0) > 0 OR COALESCE(t.REC_PR, 0) > 0)",
            TripSql.Day(TripDayMapper.Map(date, options.Value.TemplateYear)), cancellationToken: ct));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SavedTripAssignment>> GetSavedPlanAsync(DateOnly date, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);
        var rows = await connection.QueryAsync<SavedRow>(new CommandDefinition($"""
            SELECT t.ID AS TripId, COALESCE(l.DENOMLI, CONCAT('Ligne ', t.DENUMLI)) AS LineLabel,
                   t.TIME_DEPART AS Departure, {TripSql.ArrivalSubquery} AS Arrival,
                   NULLIF(t.BUS_PR, 0) AS BusId, v.DEMATRI AS BusPlate,
                   NULLIF(t.CHAUFF_PR, 0) AS DriverId, a.DENAGEN AS DriverName,
                   NULLIF(t.REC_PR, 0) AS ReceiverId, r.DENAGEN AS ReceiverName,
                   vr.DEMATRI AS RealBusPlate, ar.DENAGEN AS RealDriverName, rr.DENAGEN AS RealReceiverName,
                   CASE WHEN COALESCE(t.BUS_RE, 0) > 0 OR COALESCE(t.CHAUFF_RE, 0) > 0 OR COALESCE(t.REC_RE, 0) > 0 THEN 1 ELSE 0 END AS HasReal
            FROM trips t
            LEFT JOIN drligne l ON l.DENUMLI = t.DENUMLI
            LEFT JOIN drvehic v ON v.DECODVH = t.BUS_PR
            LEFT JOIN dragent a ON a.DECAGEN = t.CHAUFF_PR
            LEFT JOIN dragent r ON r.DECAGEN = t.REC_PR
            LEFT JOIN drvehic vr ON vr.DECODVH = t.BUS_RE
            LEFT JOIN dragent ar ON ar.DECAGEN = t.CHAUFF_RE
            LEFT JOIN dragent rr ON rr.DECAGEN = t.REC_RE
            WHERE {TripSql.DayFilter} AND t.TIME_DEPART IS NOT NULL
            ORDER BY t.TIME_DEPART, t.ID
            """, TripSql.Day(TripDayMapper.Map(date, options.Value.TemplateYear)), cancellationToken: ct));

        // Affichage : horaires ramenés sur le jour demandé (même règle que pour la planification).
        return rows.Select(r =>
        {
            var times = TripTimeNormalizer.Normalize(date.ToDateTime(TimeOnly.MinValue), r.Departure, r.Arrival);
            return new SavedTripAssignment(
                r.TripId, r.LineLabel, times?.Departure ?? r.Departure, times?.Arrival, r.BusId, r.BusPlate, r.DriverId, r.DriverName,
                r.ReceiverId, r.ReceiverName, r.RealBusPlate, r.RealDriverName, r.RealReceiverName, r.HasReal == 1);
        }).ToList();
    }
}
