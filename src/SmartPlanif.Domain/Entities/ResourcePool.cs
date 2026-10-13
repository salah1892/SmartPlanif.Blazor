namespace SmartPlanif.Domain.Entities;

public sealed class ResourcePool
{
    private readonly ILookup<(ResourceKind, long), Unavailability> _unavailabilities;

    public ResourcePool(IReadOnlyList<Bus> buses, IReadOnlyList<Agent> agents, IEnumerable<Unavailability> unavailabilities)
    {
        Buses = buses;
        Agents = agents;
        _unavailabilities = unavailabilities.ToLookup(u => (u.Kind, u.ResourceId));
    }

    public IReadOnlyList<Bus> Buses { get; }
    public IReadOnlyList<Agent> Agents { get; }

    /// <summary>
    /// Vrai si tous les agents planifiables peuvent faire les deux postes : un seul classement sert alors pour le chauffeur
    /// et le receveur (calcul divisé par deux), à condition de ne jamais choisir la même personne deux fois.
    /// </summary>
    public bool AllAgentsInterchangeable => Agents.Where(a => a.Role.IsPlannable()).All(a => a.Role == AgentRole.Both);

    public IReadOnlyList<Unavailability> UnavailabilityFor(ResourceKind kind, long id) => _unavailabilities[(kind, id)].ToList();
}
