-- Biletix search-sync pipeline (ksqlDB)
-- Joins the raw single-table Debezium CDC topics (events + venues + performers)
-- into a denormalized, snake_cased `events_enriched` topic that matches the
-- shape Modules.Search expects (EsEventDoc). The Elasticsearch sink connector
-- consumes events_enriched and writes the `events` index.
--
-- Apply with:  build/ksqldb/apply.sh   (POSTs each statement to the ksqlDB REST API)

SET 'auto.offset.reset' = 'earliest';

-- 1) Raw value-only streams over the Debezium topics (keys are {"Id": ...} structs,
--    so we read the Id from the value and re-key below).
CREATE STREAM events_raw (
  Id VARCHAR, Title VARCHAR, StartsAt VARCHAR, TotalTickets INT,
  VenueId VARCHAR, PerformerId VARCHAR
) WITH (KAFKA_TOPIC='biletix.public.events', VALUE_FORMAT='JSON');

CREATE STREAM venues_raw (Id VARCHAR, Name VARCHAR, City VARCHAR)
  WITH (KAFKA_TOPIC='biletix.public.venues', VALUE_FORMAT='JSON');

CREATE STREAM performers_raw (Id VARCHAR, Name VARCHAR)
  WITH (KAFKA_TOPIC='biletix.public.performers', VALUE_FORMAT='JSON');

-- 2) Re-key each stream by its primitive id (raw-string KAFKA key format) so we
--    can build TABLEs and do foreign-key joins.
CREATE STREAM events_rekeyed
  WITH (KAFKA_TOPIC='events_rekeyed', KEY_FORMAT='KAFKA', VALUE_FORMAT='JSON', PARTITIONS=1) AS
  SELECT Id AS id, Title AS title, StartsAt AS starts_at, TotalTickets AS total_tickets,
         VenueId AS venue_id, PerformerId AS performer_id
  FROM events_raw
  PARTITION BY Id;

CREATE STREAM venues_rekeyed
  WITH (KAFKA_TOPIC='venues_rekeyed', KEY_FORMAT='KAFKA', VALUE_FORMAT='JSON', PARTITIONS=1) AS
  SELECT Id AS id, Name AS name, City AS city
  FROM venues_raw
  PARTITION BY Id;

CREATE STREAM performers_rekeyed
  WITH (KAFKA_TOPIC='performers_rekeyed', KEY_FORMAT='KAFKA', VALUE_FORMAT='JSON', PARTITIONS=1) AS
  SELECT Id AS id, Name AS name
  FROM performers_raw
  PARTITION BY Id;

-- 3) Tables (latest row per id) used as the join right-hand sides / driver.
CREATE TABLE events_tbl (
  id VARCHAR PRIMARY KEY, title VARCHAR, starts_at VARCHAR, total_tickets INT,
  venue_id VARCHAR, performer_id VARCHAR
) WITH (KAFKA_TOPIC='events_rekeyed', KEY_FORMAT='KAFKA', VALUE_FORMAT='JSON');

CREATE TABLE venues_tbl (id VARCHAR PRIMARY KEY, name VARCHAR, city VARCHAR)
  WITH (KAFKA_TOPIC='venues_rekeyed', KEY_FORMAT='KAFKA', VALUE_FORMAT='JSON');

CREATE TABLE performers_tbl (id VARCHAR PRIMARY KEY, name VARCHAR)
  WITH (KAFKA_TOPIC='performers_rekeyed', KEY_FORMAT='KAFKA', VALUE_FORMAT='JSON');

-- 4) Denormalized output. ksqlDB does not allow two foreign-key joins in one
--    n-way statement, so we join in two steps: first add the performer name,
--    then the venue. Both CTAS results stay keyed by the event id, so the final
--    `events_enriched` Kafka key is the raw event id -> used as the ES doc id.
CREATE TABLE events_with_performer
  WITH (KAFKA_TOPIC='events_with_performer', KEY_FORMAT='KAFKA', VALUE_FORMAT='JSON', PARTITIONS=1) AS
  SELECT
    e.id            AS id,
    e.title         AS title,
    e.starts_at     AS starts_at,
    e.total_tickets AS total_tickets,
    e.venue_id      AS venue_id,
    e.performer_id  AS performer_id,
    p.name          AS performer_name
  FROM events_tbl e
  JOIN performers_tbl p ON e.performer_id = p.id;

-- Aliases are back-tick quoted so ksqlDB keeps them lower-case (it upper-cases
-- unquoted identifiers) to match EsEventDoc's snake_case field inferrer.
-- AS_VALUE(id) copies the event id (the Kafka key) into the value as well, so
-- `_source.id` exists for SearchHit.Id; the key still drives the ES document id.
-- KAFKA_TOPIC is 'events' on purpose: the Confluent ES sink uses the topic name
-- as the index name (topic.index.map is gone and topic-mutating SMTs are rejected),
-- so the output topic must be named exactly like the target ES index.
CREATE TABLE events_enriched
  WITH (KAFKA_TOPIC='events', KEY_FORMAT='KAFKA', VALUE_FORMAT='JSON', PARTITIONS=1) AS
  SELECT
    ep.id             AS `k_id`,
    AS_VALUE(ep.id)   AS `id`,
    ep.title          AS `title`,
    ep.starts_at      AS `starts_at`,
    ep.total_tickets  AS `total_tickets`,
    ep.performer_id   AS `performer_id`,
    ep.performer_name AS `performer_name`,
    ep.venue_id       AS `venue_id`,
    v.name            AS `venue_name`,
    v.city            AS `city`
  FROM events_with_performer ep
  JOIN venues_tbl v ON ep.venue_id = v.id;
