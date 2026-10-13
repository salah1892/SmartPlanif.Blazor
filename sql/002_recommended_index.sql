-- RECOMMANDÉ pour une table trips remplie sur toute l'année : SmartPlanif sélectionne les voyages par jour de service
-- (trips.DEDATED). Sans index, chaque lecture parcourt toute la table.
CREATE INDEX IDX_TRIPS_DEDATED ON trips (DEDATED);
