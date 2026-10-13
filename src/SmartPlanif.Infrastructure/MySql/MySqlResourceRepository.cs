using Dapper;
using Microsoft.Extensions.Options;
using SmartPlanif.Application.Abstractions;
using SmartPlanif.Domain.Common;
using SmartPlanif.Domain.Entities;

namespace SmartPlanif.Infrastructure.MySql;

/// <summary>
/// Rôle des agents. Dans votre base, dragent.DECQUAL est le GENRE (« H = homme ») : il ne distingue pas chauffeurs et receveurs.
/// Tous les agents de dragent peuvent donc être chauffeur ou receveur (<see cref="DefaultRole"/> = Both).
/// <see cref="Map"/> reste disponible pour réserver un jour des valeurs de DECQUAL à un poste (ou exclure un agent avec « Other »).
/// </summary>
public sealed class QualificationOptions
{
    /// <summary>Rôle des agents dont la valeur de DECQUAL n'est pas dans <see cref="Map"/>. Par défaut : polyvalent.</summary>
    public AgentRole DefaultRole { get; set; } = AgentRole.Both;

    /// <summary>Exceptions éventuelles : valeur de DECQUAL -> rôle. Vide par défaut.</summary>
    public Dictionary<string, AgentRole> Map { get; set; } = new();
}

/// <summary>Comment déterminer si un bus est actif.</summary>
public enum BusActiveMode
{
    /// <summary>Si au moins un bus a DEACTIF = 1, on utilise ce champ ; sinon (champ non renseigné) tous les bus sont actifs.</summary>
    Auto,

    /// <summary>Strict : actif seulement si drvehic.DEACTIF = 1.</summary>
    FlagIsOne,

    /// <summary>Tous les bus sont actifs, quel que soit DEACTIF.</summary>
    AllActive,
}

/// <summary>Réglages de lecture des ressources (section « ResourceSource » de appsettings.json).</summary>
public sealed class ResourceSourceOptions
{
    /// <summary>Règle d'activité des bus. Auto par défaut : dans votre base DEACTIF vaut 0 pour tous les bus.</summary>
    public BusActiveMode BusActiveMode { get; set; } = BusActiveMode.Auto;
}

/// <summary>Lecture des bus (drvehic), des agents (dragent) et, si la table existe, des indisponibilités.</summary>
public sealed class MySqlResourceRepository(
    IDbConnectionFactory db, IOptions<QualificationOptions> qualifications, IOptions<ResourceSourceOptions> resourceOptions) : IResourceRepository
{
    private sealed class BusRow
    {
        public long Id { get; set; } public string? Plate { get; set; } public long Active { get; set; }
        public long Seats { get; set; } public long Standing { get; set; } public long? CategoryId { get; set; }
        public long? CenterId { get; set; } public long? DelegationId { get; set; } public DateTime? InServiceSince { get; set; }
    }

    private sealed class AgentRow
    {
        public long Id { get; set; } public string? Name { get; set; } public string? Qual { get; set; }
        public long? RestDay { get; set; } public long? CenterId { get; set; } public long? DelegationId { get; set; }
    }

    private sealed class UnavailabilityRow
    {
        public string Kind { get; set; } = ""; public long ResourceId { get; set; }
        public DateTime StartAt { get; set; } public DateTime EndAt { get; set; } public string? Reason { get; set; }
    }

    private sealed class NameRow
    {
        public string? Name { get; set; }
    }

    /// <inheritdoc />
    public async Task<ResourcePool> GetPoolAsync(DateOnly from, DateOnly to, CancellationToken ct)
    {
        await using var connection = await db.OpenAsync(ct);

        // drvehic.DECCENT / DECDELEG sont des DOUBLE alors que dragent/drligne utilisent des BIGINT : on convertit.
        var busRows = (await connection.QueryAsync<BusRow>(new CommandDefinition("""
            SELECT DECODVH AS Id, DEMATRI AS Plate, COALESCE(DEACTIF, 0) AS Active,
                   COALESCE(DENBRPA, 0) AS Seats, COALESCE(DENBRPD, 0) AS Standing, DECATVH AS CategoryId,
                   CAST(DECCENT AS SIGNED) AS CenterId, CAST(DECDELEG AS SIGNED) AS DelegationId, DEDATEC AS InServiceSince
            FROM drvehic
            """, cancellationToken: ct))).ToList();

        // Si DEACTIF n'est renseigné pour aucun bus (tous à 0), le champ n'est pas fiable : en mode Auto tous les bus sont actifs.
        var anyFlagged = busRows.Any(r => r.Active == 1);
        bool IsActive(BusRow r) => IsBusActive(resourceOptions.Value.BusActiveMode, anyFlagged, r.Active);

        var buses = busRows
            .Select(r => new Bus(r.Id, r.Plate ?? $"#{r.Id}", IsActive(r), (int)r.Seats, (int)r.Standing,
                                 r.CategoryId, r.CenterId, r.DelegationId, r.InServiceSince))
            .ToList();

        var map = qualifications.Value.Map;
        var defaultRole = qualifications.Value.DefaultRole;
        var agents = (await connection.QueryAsync<AgentRow>(new CommandDefinition("""
            SELECT DECAGEN AS Id, DENAGEN AS Name, DECQUAL AS Qual, DECJOUR AS RestDay, DECCENT AS CenterId, DECDELEG AS DelegationId
            FROM dragent
            """, cancellationToken: ct)))
            .Select(r => new Agent(r.Id, r.Name ?? $"#{r.Id}",
                                   r.Qual is not null && map.TryGetValue(r.Qual, out var role) ? role : defaultRole,
                                   ToDayOfWeek(r.RestDay), r.CenterId, r.DelegationId))
            .ToList();

        var unavailabilities = await ReadUnavailabilitiesAsync(connection, from, to, ct);
        return new ResourcePool(buses, agents, unavailabilities);
    }

    /// <summary>
    /// La table resource_unavailability est OPTIONNELLE : absente du schéma d'origine, elle sert aux congés et à la maintenance.
    /// Sans elle on ne lit rien et on continue (aucune erreur) ; la page « Diagnostic » signale son absence.
    /// </summary>
    private static async Task<List<Unavailability>> ReadUnavailabilitiesAsync(
        System.Data.Common.DbConnection connection, DateOnly from, DateOnly to, CancellationToken ct)
    {
        var exists = (await connection.QueryAsync<NameRow>(new CommandDefinition(
            "SELECT table_name AS Name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name = 'resource_unavailability'",
            cancellationToken: ct))).Any();
        if (!exists) return new List<Unavailability>();

        var rows = await connection.QueryAsync<UnavailabilityRow>(new CommandDefinition("""
            SELECT KIND AS Kind, RESOURCE_ID AS ResourceId, START_AT AS StartAt, END_AT AS EndAt, REASON AS Reason
            FROM resource_unavailability
            WHERE END_AT > @From AND START_AT < @To
            """, new { From = from.ToDateTime(TimeOnly.MinValue), To = to.AddDays(1).ToDateTime(TimeOnly.MinValue) },
            cancellationToken: ct));

        return rows.Select(r => new Unavailability(r.Kind == "BUS" ? ResourceKind.Bus : ResourceKind.Agent,
                                                   r.ResourceId, new TimeInterval(r.StartAt, r.EndAt), r.Reason ?? "")).ToList();
    }

    /// <summary>Règle d'activité d'un bus selon le mode et selon que DEACTIF est renseigné (= 1) pour au moins un bus.</summary>
    public static bool IsBusActive(BusActiveMode mode, bool anyBusFlaggedActive, long flag) => mode switch
    {
        BusActiveMode.FlagIsOne => flag == 1,
        BusActiveMode.AllActive => true,
        _ => !anyBusFlaggedActive || flag == 1,
    };

    /// <summary>DECJOUR : 1=lundi..7=dimanche. DayOfWeek .NET : Sunday=0, Monday=1..Saturday=6 -> modulo 7.</summary>
    public static DayOfWeek? ToDayOfWeek(long? decjour) =>
        decjour is >= 1 and <= 7 ? (DayOfWeek)(decjour.Value % 7) : null;
}
