# Kafka Connect image for Biletix.
# The stock debezium/connect image ships the Debezium Postgres source but NOT the
# Confluent Elasticsearch sink connector, which the search-sync pipeline needs.
# Bake the ES sink plugin in so `docker compose up --build` yields a Connect node
# that can run both the Debezium source and the Elasticsearch sink out of the box.
FROM debezium/connect:2.7.3.Final

# 14.x supports Elasticsearch 8.x (matches the elasticsearch:8.15.0 service).
ARG ES_CONNECTOR_VERSION=14.1.2

RUN curl -fsSL -o /tmp/es-sink.zip \
      "https://hub-downloads.confluent.io/api/plugins/confluentinc/kafka-connect-elasticsearch/versions/${ES_CONNECTOR_VERSION}/confluentinc-kafka-connect-elasticsearch-${ES_CONNECTOR_VERSION}.zip" \
 && unzip -q /tmp/es-sink.zip -d /kafka/connect/ \
 && rm -f /tmp/es-sink.zip
