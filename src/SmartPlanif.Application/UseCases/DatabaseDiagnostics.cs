using SmartPlanif.Application.Abstractions;
using SmartPlanif.Domain.Entities;

namespace SmartPlanif.Application.UseCases;

/// <summary>Constat du diagnostic : niveau (« ok », « info », « warn », « error ») et message.</summary>
public sealed record DiagnosticFinding(string Level, string Message);

/// <summary>Rapport de diagnostic de la base pour un jour de service.</summary>
public sealed record DatabaseDiagnosticReport(
    DateOnly Date, RawDiagnostics Raw, int TripsLoaded,
    int DriversMapped, int ReceiversMapped, int OtherAgents, int ActiveBuses,
    IReadOnlyList<DiagnosticFinding> Findings);

/// <summary>
/// Cas d'usage « diagnostiquer mes données » : lecture seule. Compare ce que contient la base à ce que le moteur
/// parvient à charger, et explique chaque écart (voyages écartés, rôles non reconnus, tables absentes...).
/// À lancer EN PREMIER sur vos données réelles, avant toute allocation.
/// </summary>
public sealed class DatabaseDiagnosticsService(
    IDatabaseDiagnostics diagnostics, ITripRepository trips, IResourceRepository resources)
{
    /// <summary>Produit le rapport pour un jour de service.</summary>
    public async Task<DatabaseDiagnosticReport> RunAsync(DateOnly date, CancellationToken ct)
    {
        var raw = await diagnostics.CollectAsync(date, ct);
        var loadedTrips = await trips.GetTripsOfDayAsync(date, ct);
        var pool = await resources.GetPoolAsync(date.AddDays(-1), date.AddDays(1), ct);

        var drivers = pool.Agents.Count(a => a.Role.CanDrive());
        var receivers = pool.Agents.Count(a => a.Role.CanReceive());
        var others = pool.Agents.Count(a => a.Role == AgentRole.Other);
        var activeBuses = pool.Buses.Count(b => b.Active);

        var findings = BuildFindings(date, raw, loadedTrips.Count, drivers, receivers, others, activeBuses);
        return new DatabaseDiagnosticReport(date, raw, loadedTrips.Count, drivers, receivers, others, activeBuses, findings);
    }

    private static List<DiagnosticFinding> BuildFindings(
        DateOnly date, RawDiagnostics raw, int loaded, int drivers, int receivers, int others, int activeBuses)
    {
        var findings = new List<DiagnosticFinding>();
        findings.Add(new("info", "Jour demandé -> lignes lues dans trips : " + raw.DayMapping));

        if (raw.TripsOfDay == 0)
            findings.Add(new("error",
                $"Aucun voyage dans la table trips pour le {date:dd/MM/yyyy}. Dates présentes : du {raw.FirstTripDay:dd/MM/yyyy} au {raw.LastTripDay:dd/MM/yyyy}."));
        else
            findings.Add(new("ok", $"{raw.TripsOfDay} voyage(s) trouvé(s) dans trips pour ce jour."));

        if (raw.TripsOfDay > 0 && raw.TripsOfDayOnActiveLine < raw.TripsOfDay)
            findings.Add(new("warn",
                $"{raw.TripsOfDay - raw.TripsOfDayOnActiveLine} voyage(s) sont sur une ligne inactive (drligne.DEACTIF) ou inexistante : ils sont ignorés."));

        if (raw.TripsOfDayWithoutStopTimes > 0)
        {
            var viaItinerary = raw.TripsOfDayWithoutStopTimes - raw.TripsOfDayWithoutStopTimesAndItinerary;
            if (viaItinerary > 0)
                findings.Add(new("info",
                    $"{viaItinerary} voyage(s) n'ont pas d'heure d'arrivée dans stop_times : elle est déduite de la durée de l'itinéraire de la ligne (dritin). " +
                    "Vérifiez le tableau « Durées par ligne » ci-dessous (durée retenue et vitesse moyenne)."));
            if (raw.TripsOfDayWithoutStopTimesAndItinerary > 0)
                findings.Add(new("warn",
                    $"{raw.TripsOfDayWithoutStopTimesAndItinerary} voyage(s) n'ont ni stop_times ni itinéraire (dritin) pour leur ligne : leur heure d'arrivée est inconnue, ils sont écartés."));
        }

        var discarded = (int)raw.TripsOfDayOnActiveLine - loaded;
        if (discarded > 0)
            findings.Add(new("warn",
                $"{discarded} voyage(s) valides en base ne sont pas chargés par le moteur (heure d'arrivée absente ou incohérente avec le départ)."));
        else if (raw.TripsOfDay > 0)
            findings.Add(new("ok", $"Le moteur charge {loaded} voyage(s) : aucun voyage perdu à la conversion."));

        if (raw.TripsOfDayWithPlannedResources > 0)
            findings.Add(new("info",
                $"{raw.TripsOfDayWithPlannedResources} voyage(s) ont déjà au moins une ressource PRÉVUE (BUS_PR / CHAUFF_PR / REC_PR). " +
                "Mode « compléter » : conservés ; mode « remplacer » : replanifiés."));
        if (raw.TripsOfDayWithRealResources > 0)
            findings.Add(new("info",
                $"{raw.TripsOfDayWithRealResources} voyage(s) ont des ressources RÉELLES (*_RE) : considérés comme effectués, verrouillés, jamais modifiés."));

        if (raw.BusesTotal > 0 && raw.BusesFlaggedActive == 0)
            findings.Add(new("warn",
                $"drvehic.DEACTIF ne vaut 1 pour aucun des {raw.BusesTotal} bus : le champ semble non renseigné. " +
                "Tous les bus sont traités comme actifs (ResourceSource:BusActiveMode = Auto). Excluez les bus hors service via resource_unavailability ou renseignez DEACTIF."));
        if (activeBuses == 0)
            findings.Add(new("error", "Aucun bus actif chargé (drvehic.DEACTIF)."));
        else
            findings.Add(new("ok", $"{activeBuses} bus actif(s) chargé(s) sur {raw.BusesTotal}."));

        var restTotal = raw.AgentRestDays.Sum(r => r.Count);
        var restTop = raw.AgentRestDays.OrderByDescending(r => r.Count).FirstOrDefault();
        if (restTop is not null && restTotal >= 10 && restTop.Count * 2 > restTotal)
            findings.Add(new("error",
                $"dragent.DECJOUR vaut « {restTop.Label} » pour {restTop.Count} agents sur {restTotal} ({restTop.Count * 100 / restTotal} %) : c'est probablement une valeur par défaut. " +
                "Avec la contrainte A03 active, presque aucun agent ne serait disponible ce jour de la semaine. Désactivez A03 (page Contraintes) tant que les jours de repos ne sont pas fiables."));

        foreach (var column in raw.ResourceColumns.Where(c => c.Zero > 0))
            findings.Add(new("info", $"trips.{column.Column} : {column.Zero} ligne(s) valent 0 ce jour-là : traitées comme « aucune ressource »."));

        if (drivers + receivers == 0 || drivers < 1 || receivers < 1)
            findings.Add(new("error",
                $"Agents : {drivers} apte(s) au poste de chauffeur, {receivers} au poste de receveur, {others} non planifiable(s). " +
                "Aucune allocation n'est possible. Par défaut tous les agents de dragent sont polyvalents (Qualifications:DefaultRole = Both)."));
        else if (drivers < 2)
            findings.Add(new("error", "Il faut au moins 2 agents distincts (un chauffeur et un receveur) pour affecter un voyage."));
        else
            findings.Add(new("ok",
                $"Agents : {drivers} apte(s) chauffeur, {receivers} apte(s) receveur, {others} non planifiable(s). " +
                "Valeurs de DECQUAL rencontrées (genre : sans effet sur le poste) : " +
                string.Join(", ", raw.AgentQualifications.Select(q => $"« {q.Label} » ({q.Count})")) + "."));

        if (raw.MissingOptionalTables.Count > 0)
            findings.Add(new("info",
                "Tables optionnelles absentes : " + string.Join(", ", raw.MissingOptionalTables) +
                ". Les congés/absences et maintenances ne sont pas pris en compte (aucune erreur)."));

        if (raw.TripStates.Count > 0)
            findings.Add(new("info",
                "Valeurs de trips.ETAT ce jour-là : " + string.Join(", ", raw.TripStates.Select(s => $"{s.Label} ({s.Count})")) +
                ". Aucune valeur n'est exclue par défaut (section TripSource:ExcludedStates)."));
        return findings;
    }
}
