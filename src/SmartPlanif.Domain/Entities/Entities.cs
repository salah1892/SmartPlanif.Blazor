using SmartPlanif.Domain.Common;

namespace SmartPlanif.Domain.Entities;

/// <summary>
/// Aptitude d'un agent. <see cref="Both"/> : l'agent peut être chauffeur OU receveur (cas de votre base : dragent.DECQUAL est le
/// genre, donc tous les agents sont interchangeables). <see cref="Other"/> : agent non planifiable (ni chauffeur ni receveur).
/// </summary>
public enum AgentRole { Driver, Receiver, Both, Other }

/// <summary>Aptitudes déduites du rôle.</summary>
public static class AgentRoleExtensions
{
    /// <summary>Vrai si l'agent peut tenir le poste de chauffeur.</summary>
    public static bool CanDrive(this AgentRole role) => role is AgentRole.Driver or AgentRole.Both;

    /// <summary>Vrai si l'agent peut tenir le poste de receveur.</summary>
    public static bool CanReceive(this AgentRole role) => role is AgentRole.Receiver or AgentRole.Both;

    /// <summary>Vrai si l'agent peut être affecté à au moins un poste.</summary>
    public static bool IsPlannable(this AgentRole role) => role != AgentRole.Other;
}

/// <summary>Famille de ressource, utilisée pour retrouver les indisponibilités.</summary>
public enum ResourceKind { Bus, Agent }

/// <summary>Agent (chauffeur ou receveur). RestDay est exprimé en DayOfWeek .NET (Sunday = 0).</summary>
public sealed record Agent(
    long Id, string Name, AgentRole Role,
    DayOfWeek? RestDay, long? CenterId, long? DelegationId);

/// <summary>Autobus du parc (table drvehic).</summary>
public sealed record Bus(
    long Id, string Plate, bool Active, int Seats, int Standing,
    long? CategoryId, long? CenterId, long? DelegationId, DateTime? InServiceSince)
{
    /// <summary>Capacité totale = places assises + places debout.</summary>
    public int Capacity => Seats + Standing;
}

/// <summary>
/// Voyage à planifier. L'heure d'arrivée n'existe pas dans la table trips : elle est dérivée de stop_times.
/// LineTypeId provient de drligne.DECTYLI (urbain, interurbain...).
/// </summary>
public sealed record Trip(
    long Id, long LineId, string LineLabel, long? LineTypeId,
    DateTime Departure, DateTime Arrival, double DistanceKm,
    long? CenterId, long? DelegationId,
    long? OriginStationId, long? DestinationStationId,
    int? ExpectedPassengers)
{
    /// <summary>Intervalle d'occupation des ressources.</summary>
    public TimeInterval Interval => new(Departure, Arrival);

    /// <summary>Jour de rattachement (jour du départ) pour les cumuls journaliers.</summary>
    public DateOnly WorkDate => DateOnly.FromDateTime(Departure);
}

/// <summary>Période d'indisponibilité d'une ressource : congé, absence, maintenance...</summary>
public sealed record Unavailability(ResourceKind Kind, long ResourceId, TimeInterval Period, string Reason);

/// <summary>
/// Voyage déjà affecté en dehors de cette exécution : il bloque ses ressources mais n'est pas replanifié.
/// <paramref name="IsRealized"/> est vrai quand les ressources sont les ressources RÉELLES (colonnes *_RE de trips) :
/// le voyage a été effectué, c'est un fait qu'on ne replanifie jamais.
/// </summary>
public sealed record AssignedTrip(Trip Trip, long BusId, long DriverId, long ReceiverId, bool IsRealized = false);
