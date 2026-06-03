#!/usr/bin/env bash
# Sets up the whole search-sync pipeline on a fresh cluster:
#   Postgres (CDC) -> Debezium -> Kafka -> ksqlDB (join) -> events topic -> ES sink -> Elasticsearch
# Safe to run once after `docker compose up -d --build`.
set -e
CONNECT_URL="${CONNECT_URL:-http://localhost:8085}"
KSQL_URL="${KSQL_URL:-http://localhost:8088}"
ES_URL="${ES_URL:-http://localhost:9200}"

echo "Waiting for Kafka Connect at $CONNECT_URL..."
until curl -fsS "$CONNECT_URL/" >/dev/null; do sleep 2; done

echo "Setting REPLICA IDENTITY FULL..."
docker exec biletix-postgres psql -U biletix -d biletix -c "
  ALTER TABLE events     REPLICA IDENTITY FULL;
  ALTER TABLE venues     REPLICA IDENTITY FULL;
  ALTER TABLE performers REPLICA IDENTITY FULL;
  ALTER TABLE tickets    REPLICA IDENTITY FULL;
  ALTER TABLE bookings   REPLICA IDENTITY FULL;
"

echo "Registering Debezium Postgres source..."
curl -fsS -X POST -H 'Content-Type: application/json' --data @connectors/debezium-postgres.json "$CONNECT_URL/connectors" | jq .

echo "Waiting for Debezium to publish the source topics (snapshot)..."
until docker exec biletix-kafka kafka-topics --bootstrap-server localhost:29092 --list 2>/dev/null \
        | grep -q '^biletix.public.events$'; do sleep 2; done

echo "Waiting for ksqlDB at $KSQL_URL..."
until curl -fsS "$KSQL_URL/info" >/dev/null 2>&1; do sleep 2; done

echo "Applying ksqlDB join pipeline (events + venues + performers -> events topic)..."
# Strip full-line comments so the ksqlDB CLI parser stays quiet.
grep -v '^[[:space:]]*--' ksqldb/search-pipeline.sql \
  | docker exec -i biletix-ksqldb ksql "$KSQL_URL"

echo "Creating Elasticsearch 'events' index mapping..."
curl -fsS -X PUT "$ES_URL/events" -H 'Content-Type: application/json' \
  --data @elasticsearch/events-index.json || echo " (index may already exist — continuing)"
echo ""

echo "Registering Elasticsearch sink..."
curl -fsS -X POST -H 'Content-Type: application/json' --data @connectors/elasticsearch-sink.json "$CONNECT_URL/connectors" | jq .

echo "Done. Try:  curl \"http://localhost:8080/search?keyword=tarkan\""
