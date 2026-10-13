# SmartPlanif — Allocation Bus / Chauffeur / Receveur (C# / .NET 8, Clean Architecture, Blazor)

Allocation de bus, chauffeurs et receveurs aux voyages avec **contraintes choisies par l'utilisateur**
(désactivée / dure / souple) et **heuristique + métaheuristique** (construction par phases + ALNS avec recuit simulé).

## Démarrage
```powershell
dotnet build
dotnet test
dotnet run --project src\SmartPlanif.Web        # http://localhost:5080
```
Sans chaîne de connexion, l'application fonctionne en **mode démonstration** (36 voyages générés par jour, 12 bus, 28 agents polyvalents).
## Brancher votre base MySQL (la table `trips` est le résultat)

La table `trips` est supposée remplie pour toute l'année. SmartPlanif n'écrit que les colonnes **prévues** :
`BUS_PR`, `CHAUFF_PR`, `REC_PR`. Les colonnes **réelles** (`BUS_RE`, `CHAUFF_RE`, `REC_RE`) viennent du terrain : elles ne sont
**jamais modifiées**. Un voyage dont les trois ressources réelles sont renseignées est considéré comme **effectué** : il est
verrouillé (ses ressources restent occupées) et jamais replanifié.

1. Travaillez sur une **copie** de la base. Aucun script SQL n'est obligatoire.
2. `src/SmartPlanif.Web/appsettings.json` : renseignez `ConnectionStrings:TransportAi`
   (en développement : `dotnet user-secrets`, ne commitez jamais le mot de passe). Aucune configuration de rôles n'est nécessaire :
   `dragent.DECQUAL` est le genre, donc **tous les agents peuvent être chauffeur ou receveur** (`Qualifications:DefaultRole = Both`).
3. Lancez l'application, ouvrez la page **Diagnostic** pour un jour réel : elle explique ce que le moteur charge, ce qu'il écarte et pourquoi.
   Les mêmes contrôles sont disponibles en SQL dans `sql/003_manual_checks.sql`.
4. Page **Allocation** : d'abord en **simulation** (« Aucun »), puis « Journée complète seulement ».
5. Page **Plan enregistré** : relire le plan écrit dans `trips`, ou l'effacer.

Optionnel : `sql/001_optional_resource_unavailability.sql` (congés, maintenance), `sql/002_recommended_index.sql` (index sur `trips.DEDATED`).

### Calendrier : le passé est grisé
* Seules les journées **à partir d'aujourd'hui** (aujourd'hui inclus) sont planifiables et modifiables. Une journée passée est consultable
  (page « Plan enregistré », page « Diagnostic ») mais **grisée** : ni planification, ni écriture, ni effacement. La règle est appliquée côté
  serveur (`PlanningCalendar`), pas seulement dans l'interface.
* Pour tester sur des données anciennes : `Planning:Today` (format `AAAA-MM-JJ`) fixe la date de référence.

### Année modèle (désactivée)
Votre table contient les années 2025 à 2027 : le jour demandé est lu **tel quel** (`TripSource:TemplateYear = null`). L'option reste disponible
si un jour vous n'avez qu'une année à réutiliser : le jour demandé est alors lu sur le même jour de l'année modèle, horaires ramenés sur le jour demandé.

### Particularités de votre base (relevées par la page Diagnostic)
* **Pas de `stop_times` pour les voyages futurs** : l'heure d'arrivée est déduite de la durée de l'itinéraire de la ligne (`dritin.DEDURTR`).
  `TripSource:ItineraryDuration` = `Auto` (détecte durées cumulées ou par tronçon, départage par la vitesse moyenne), `MaxCumulative` ou `SumOfLegs`.
  Le tableau « Durées par ligne » de la page Diagnostic montre les valeurs lues, la durée retenue et la vitesse : **vérifiez-le**.
* **`drvehic.DEACTIF` = 0 pour tous les bus** : champ non renseigné. `ResourceSource:BusActiveMode` = `Auto` (tous actifs tant que DEACTIF n'est renseigné pour aucun bus),
  `FlagIsOne` (strict) ou `AllActive`.
* **Identifiant 0 = « aucune ressource »** dans `BUS_PR`, `CHAUFF_PR`, `REC_PR`, `BUS_RE`, `CHAUFF_RE`, `REC_RE` (comme NULL).
* **`dragent.DECJOUR` = 2 pour presque tous les agents** : valeur par défaut probable. **Désactivez la contrainte A03** (page Contraintes) tant que les jours
  de repos ne sont pas fiables, sinon les jours concernés ne pourront pas être planifiés.

### Règles de l'enregistrement
* **Jour de service** = `trips.DEDATED` (à défaut la date de `TIME_DEPART`).
* **Journée complète seulement** (défaut recommandé) : on n'écrit que si TOUS les voyages du jour sont affectés et que le contrôle indépendant ne trouve aucune violation. Sinon rien n'est écrit et le plan existant est conservé.
* **Compléter** : les voyages déjà planifiés sont conservés (et bloquent leurs ressources) ; seuls les autres sont planifiés.
* **Remplacer** : toute la journée est replanifiée ; à l'enregistrement, les voyages restés sans ressource sont remis à `NULL`.
* L'écriture se fait en **une transaction** : tout ou rien. Un agent ou un bus inexistant (clé étrangère) annule tout.
* `trips.ETAT` est **ignoré** pour l'instant. Quand les états seront définis (terminé, en cours, annulé...), `TripSource:ExcludedStates` permettra d'exclure ceux qui ne doivent pas être planifiés.
* **Agents** : chaque voyage exige deux agents différents (un chauffeur, un receveur) pris dans le même pool ; un agent ne peut jamais faire deux voyages qui se chevauchent, ni les deux postes du même voyage.

## Écrans
| Page | Rôle |
|---|---|
| Contraintes | Pour chaque règle : Désactivée / Dure / Souple + poids. Les impossibilités physiques (chevauchement, bus inactif) sont verrouillées. |
| Types de ligne | Catégories de bus autorisées (B06) et préférées (BP03) par type de ligne (ex. confort pour l'interurbain). |
| Paramètres | 8 h, amplitude, repos, remise en service, km... et réglages ALNS. Validation avant sauvegarde. |
| Diagnostic | Lecture seule : compare la base à ce que le moteur charge (voyages écartés, rôles non reconnus, tables absentes...). |
| Allocation | Aperçu + diagnostic de faisabilité, méthode, mode compléter/remplacer, enregistrement dans `trips`, progression en direct, plan, causes d'échec, contrôle indépendant. |
| Plan enregistré | Relire ou effacer le plan prévu (`*_PR`) d'une journée. |

Les réglages sont enregistrés dans `src/SmartPlanif.Web/App_Data/allocation-settings.json`.

## Architecture (les dépendances pointent vers le Domain)
```
Web (Blazor) -> Infrastructure (MySQL/Dapper, démo, JSON) -> Application (cas d'usage, ports) -> Domain
```
| Dossier Domain | Contenu |
|---|---|
| Entities, Scheduling | Voyage, bus, agent ; `ScheduleBook` (plannings, Clone/Remove pour la métaheuristique) |
| Rules | Règles dures `IHardRule<T>` et souples `ISoftRule<T>` (une classe par règle) |
| Constraints | `ConstraintCatalog` : liste de TOUTES les contraintes, modes supportés, poids par défaut. Point central d'extension |
| Allocation | `TripAllocator` (décision pour un voyage), constructions, `ObjectiveFunction`, `PlanValidator`, `FeasibilityAnalyzer` |
| Alns | `AlnsImprover`, opérateurs de destruction/réparation |

## Algorithme
1. **Diagnostic** : demande maximale simultanée vs ressources (bus par type de ligne, chauffeurs, receveurs, heures).
2. **Construction par phases** : types de ligne triés par tension (demande / bus compatibles) ; dans chaque phase, voyages les plus contraints d'abord ; plannings partagés entre phases.
3. **ALNS + recuit simulé** : détruire 5-15 % du plan, réparer (toujours faisable), accepter selon l'objectif ; poids des opérateurs adaptatifs ; seule la faisabilité dure est visitée.
4. **Passe de complétion** puis **contrôle indépendant** : un plan qui viole une règle dure n'est jamais enregistré.

Objectif (à minimiser) : `pénalité x voyages non affectés + déficit de score souple + poids x écart-type de charge`.
La pénalité domine : un meilleur score n'est jamais obtenu en perdant un voyage.

## Étendre
* **Nouvelle règle** : créer la classe (`Rules/Hard` ou `Rules/Soft`), l'ajouter dans `ConstraintCatalog` (une ligne), écrire un test. L'interface la propose automatiquement.
* **Nouvel opérateur ALNS** : implémenter `IDestroyOperator` / `IRepairOperator` et l'ajouter dans `AlnsImprover`.
* **Autre source de données** : implémenter les ports de `Application/Abstractions/Ports.cs`.

## Vérifications effectuées lors de la génération
Domain, Application, Infrastructure et Web ont été compilés sans erreur ; les 58 tests ont été exécutés avec succès
(avec un lanceur de remplacement, faute d'accès à NuGet) ; sur les données de démonstration : glouton 34/36, construction par
phases 35/36, ALNS 36/36 voyages affectés, 0 violation.
Toutes les requêtes SQL (lecture des voyages, écriture des colonnes *_PR, effacement, diagnostic, scripts du dossier sql) ont été
exécutées sur un MySQL 8.0 chargé avec le schéma de votre dump et une année de données de test (13 140 voyages).
Non vérifiés : l'exécution du code C# des dépôts MySQL avec les vraies bibliothèques Dapper/MySqlConnector (NuGet inaccessible
à la génération ; ils ont été compilés contre des substituts), vos données réelles, et les interactions dans le navigateur.

## Limites et points à valider avec la société
* Requêtes MySQL validées sur le schéma fourni avec des données de test, pas sur vos données : signification de `trips.ETAT`, format réel de `TIME_DEPART`, contenu de `DECQUAL`.
* Aucune modification manuelle voyage par voyage dans l'interface (replanifier la journée ou corriger dans la base) ; pas d'historique des changements.
* Heure d'arrivée dérivée de `stop_times` ; aller-retour liés (`HAVERET`) traités comme des voyages indépendants.
* B05 (capacité) inactive tant que le nombre de voyageurs prévu n'existe pas ; `drdentr` ignorée (aucune relation avec `dragent`).
* Scores pendant la recherche = scores à l'insertion (approximation pour la vitesse) ; scores exacts recalculés en fin d'allocation.
* Valeurs par défaut (8 h, 12 h d'amplitude, 30 min de repos, 15 min de remise en service) à valider.
