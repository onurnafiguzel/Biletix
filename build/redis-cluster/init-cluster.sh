#!/bin/sh
# Idempotent Redis Cluster bootstrap, run once by the `redis-cluster-init` one-shot
# service after all six nodes are healthy. Mirrors the repo's "run-once on a fresh
# cluster" convention (see register-connectors.sh), but runs automatically in-compose.
set -eu

# Static IPs assigned in docker-compose.yml (default network, subnet 172.28.0.0/16).
NODES="172.28.0.11 172.28.0.12 172.28.0.13 172.28.0.14 172.28.0.15 172.28.0.16"
FIRST="172.28.0.11"

echo "redis-cluster-init: waiting for all 6 nodes to answer PING..."
for ip in $NODES; do
  until [ "$(redis-cli -h "$ip" -p 6379 ping 2>/dev/null)" = "PONG" ]; do
    echo "  ...$ip not ready yet"; sleep 1
  done
  echo "  $ip up"
done

# Already formed (e.g. a restart with persisted nodes.conf)? Then do nothing — this is
# what makes `docker compose up` safe to run repeatedly.
if redis-cli -h "$FIRST" -p 6379 cluster info 2>/dev/null | grep -q 'cluster_state:ok'; then
  echo "redis-cluster-init: cluster already formed (cluster_state:ok) — nothing to do."
  exit 0
fi

# First 3 listed become masters, last 3 become their replicas (--cluster-replicas 1).
# --cluster-yes skips the interactive "type yes to accept" prompt.
echo "redis-cluster-init: creating cluster (3 masters + 3 replicas)..."
redis-cli --cluster create \
  172.28.0.11:6379 172.28.0.12:6379 172.28.0.13:6379 \
  172.28.0.14:6379 172.28.0.15:6379 172.28.0.16:6379 \
  --cluster-replicas 1 --cluster-yes

echo "redis-cluster-init: done."
redis-cli -h "$FIRST" -p 6379 cluster info | grep cluster_state || true
