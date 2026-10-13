-- OPTIONNEL. Congés, absences, suspensions (agents) et maintenance (bus).
-- Sans cette table, SmartPlanif fonctionne quand même : il ignore simplement les indisponibilités
-- (la page « Diagnostic » le signale). À créer seulement quand vous voulez gérer ces cas.
CREATE TABLE IF NOT EXISTS resource_unavailability (
  ID          BIGINT AUTO_INCREMENT PRIMARY KEY,
  KIND        ENUM('BUS','AGENT') NOT NULL,
  RESOURCE_ID BIGINT NOT NULL,          -- drvehic.DECODVH ou dragent.DECAGEN selon KIND
  START_AT    DATETIME NOT NULL,
  END_AT      DATETIME NOT NULL,
  REASON      VARCHAR(100),
  KEY IX_RU_LOOKUP (KIND, RESOURCE_ID, START_AT),
  CHECK (END_AT > START_AT)
);
