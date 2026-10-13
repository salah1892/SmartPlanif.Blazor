-- REQUÊTES DE CONTRÔLE À LANCER À LA MAIN (lecture seule) dans MySQL Workbench, avant d'utiliser l'application.
-- Remplacez la date par un jour réel de votre table trips.
SET @From = '2026-10-08', @ToExclusive = '2026-10-09';

-- 1. Combien de voyages ce jour-là ? (si 0 : DEDATED ne contient pas ce que l'on croit)
SELECT COUNT(*) AS voyages_du_jour FROM trips t
WHERE (t.DEDATED >= @From AND t.DEDATED < @ToExclusive) OR (t.DEDATED IS NULL AND t.TIME_DEPART >= @From AND t.TIME_DEPART < @ToExclusive);

-- 2. Plage de dates réellement présente dans trips
SELECT MIN(DEDATED) AS premier_jour, MAX(DEDATED) AS dernier_jour, COUNT(*) AS lignes FROM trips;

-- 3. Format des horaires : la partie DATE de TIME_DEPART est-elle la bonne ? (cas des bases héritées : 1900-01-01...)
SELECT t.ID, t.DEDATED, t.TIME_DEPART, (SELECT MAX(s.ARRIVAL_TIME) FROM stop_times s WHERE s.TRIP_ID = t.ID) AS arrivee
FROM trips t WHERE t.DEDATED >= @From AND t.DEDATED < @ToExclusive ORDER BY t.TIME_DEPART LIMIT 10;

-- 4. Voyages SANS ligne dans stop_times (leur arrivée est inconnue : ils seraient écartés)
SELECT COUNT(*) AS voyages_sans_stop_times FROM trips t
WHERE t.DEDATED >= @From AND t.DEDATED < @ToExclusive
  AND NOT EXISTS (SELECT 1 FROM stop_times s WHERE s.TRIP_ID = t.ID);

-- 5. Valeurs de ETAT (le schéma dit « 3 = terminé, 5 = autre » : confirmez le sens de chaque valeur)
SELECT t.ETAT, COUNT(*) AS nb FROM trips t WHERE t.DEDATED >= @From AND t.DEDATED < @ToExclusive GROUP BY t.ETAT;

-- 6. Déjà planifié / déjà réel ce jour-là
SELECT SUM(t.BUS_PR IS NOT NULL OR t.CHAUFF_PR IS NOT NULL OR t.REC_PR IS NOT NULL) AS avec_prevu,
       SUM(t.BUS_RE IS NOT NULL OR t.CHAUFF_RE IS NOT NULL OR t.REC_RE IS NOT NULL) AS avec_reel
FROM trips t WHERE t.DEDATED >= @From AND t.DEDATED < @ToExclusive;

-- 7. DECQUAL = genre (confirmé) : sans effet sur le poste, tous les agents peuvent être chauffeur ou receveur.
SELECT DECQUAL, COUNT(*) AS nb FROM dragent GROUP BY DECQUAL ORDER BY nb DESC;

-- 8. Jours de repos (1 = lundi ... 7 = dimanche ?)
SELECT DECJOUR, COUNT(*) AS nb FROM dragent GROUP BY DECJOUR ORDER BY DECJOUR;

-- 9. Doublons possibles entre dragent et drdentr (aucune clé étrangère entre les deux)
SELECT (SELECT COUNT(*) FROM dragent) AS dragent, (SELECT COUNT(*) FROM drdentr) AS drdentr,
       (SELECT COUNT(*) FROM dragent a JOIN drdentr d ON d.DECAGEN = a.DECAGEN) AS en_commun;

-- 10. Bus actifs et catégories
SELECT v.DEACTIF, COALESCE(c.DECATEG, '(sans catégorie)') AS categorie, COUNT(*) AS nb
FROM drvehic v LEFT JOIN drcatve c ON c.DECATVH = v.DECATVH GROUP BY v.DEACTIF, c.DECATEG;

-- 11. Après une allocation enregistrée : vérifier le résultat dans trips
SELECT t.ID, t.TIME_DEPART, t.BUS_PR, t.CHAUFF_PR, t.REC_PR, t.BUS_RE, t.CHAUFF_RE, t.REC_RE
FROM trips t WHERE t.DEDATED >= @From AND t.DEDATED < @ToExclusive ORDER BY t.TIME_DEPART LIMIT 20;

-- 11 bis. Année modèle 2025 : la table couvre-t-elle bien les 365 jours ? (doit renvoyer 365)
SELECT COUNT(DISTINCT DATE(DEDATED)) AS jours_2025, COUNT(*) AS voyages_2025 FROM trips WHERE YEAR(DEDATED) = 2025;

-- 11 ter. Colonnes de ressources : combien de lignes sont NULL, à 0 (= « aucune ressource »), ou valides ?
SELECT SUM(BUS_PR IS NULL) bus_pr_null, SUM(BUS_PR = 0) bus_pr_zero, SUM(BUS_PR > 0) bus_pr_valide,
       SUM(REC_PR IS NULL) rec_pr_null, SUM(REC_PR = 0) rec_pr_zero, SUM(REC_PR > 0) rec_pr_valide,
       SUM(BUS_RE IS NULL) bus_re_null, SUM(BUS_RE = 0) bus_re_zero, SUM(BUS_RE > 0) bus_re_valide
FROM trips WHERE DEDATED >= @From AND DEDATED < @ToExclusive;

-- 11 quater. Itinéraire des lignes du jour : DEDURTR est-elle cumulée (0, 15, 30...) ou par tronçon (15, 15, 15...) ?
SELECT i.DENUMLI, COUNT(*) AS arrets, MIN(i.DEDURTR) AS min_min, MAX(i.DEDURTR) AS max_min, SUM(i.DEDURTR) AS somme_min, MAX(i.DEKMSTA) AS km_max
FROM dritin i WHERE i.DENUMLI IN (SELECT DISTINCT DENUMLI FROM trips WHERE DEDATED >= @From AND DEDATED < @ToExclusive)
GROUP BY i.DENUMLI ORDER BY i.DENUMLI;
-- Séquence détaillée d'une ligne (remplacez 10) :
SELECT DENUMLG, DECSTAT, DEKMSTA, DEDURTR FROM dritin WHERE DENUMLI = 10 ORDER BY DENUMLG;

-- 11 quinquies. Bus : DEACTIF est-il renseigné ?
SELECT DEACTIF, COUNT(*) FROM drvehic GROUP BY DEACTIF;

-- 12. Contrôle de cohérence du plan prévu : un bus, chauffeur ou receveur ne doit jamais avoir deux voyages simultanés
--     (doit renvoyer 0 ligne). Remplacez BUS_PR par CHAUFF_PR ou REC_PR pour les autres ressources.
SELECT a.ID AS voyage_a, b.ID AS voyage_b, a.BUS_PR
FROM trips a JOIN trips b ON a.BUS_PR = b.BUS_PR AND a.ID < b.ID
WHERE a.DEDATED >= @From AND a.DEDATED < @ToExclusive AND b.DEDATED >= @From AND b.DEDATED < @ToExclusive
  AND a.TIME_DEPART < (SELECT MAX(s.ARRIVAL_TIME) FROM stop_times s WHERE s.TRIP_ID = b.ID)
  AND (SELECT MAX(s.ARRIVAL_TIME) FROM stop_times s WHERE s.TRIP_ID = a.ID) > b.TIME_DEPART;
