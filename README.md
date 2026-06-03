# Biletix — Modüler Monolit Bilet Sistemi

Biletix / Ticketmaster tarzı bilet satış sistemi, **modüler monolit** olarak. ASP.NET Core 8 + PostgreSQL + Redis + Kafka/Debezium + ksqlDB + Elasticsearch.

## Mimari

```
┌─────────────────── Biletix.Api (tek host :8080) ───────────────────┐
│                                                                     │
│   /events/*  ──► Modules.Events    ─┐                               │
│   /bookings/* ─► Modules.Bookings   │   IEventsModule interface     │
│                                     │   (in-process, aynı tx)       │
│                       ──────────────┘                               │
│   /search    ──► Modules.Search ─► Elasticsearch (read-only)        │
│                                                                     │
└─────────────────────┬──────────────────────────────────────────────┘
                      │ AppDbContext (tek)
                      ▼
              ┌──────────────┐    Redis (SETNX per-ticket lock)
              │  PostgreSQL  │
              │   biletix    │  WAL ──► Debezium ──► Kafka ──► ksqlDB join ──► `events` topic ──► ES Sink ──► Elasticsearch
              └──────────────┘                                                                                    ▲
                                                                                                                  │
                                                                (Search modülü buradan okur) ───────────────────┘
```

Search'ün ihtiyaç duyduğu doküman denormalize (sanatçı + mekan adı + şehir) ve snake_case'tir;
ham tek-tablo CDC bunu üretemez. ksqlDB, `events`/`venues`/`performers` CDC topic'lerini foreign-key
join'leyip `events` adlı tek bir denormalize topic'e yazar (`build/ksqldb/search-pipeline.sql`); ES sink
bu topic'i `events` index'ine taşır. Tek truth source hâlâ Postgres; ES türetilmiş view.

### Modüller

| Modül | Sorumluluk | Endpoint |
|---|---|---|
| `Biletix.Modules.Events` | Etkinlik, mekan, sanatçı, bilet envanteri. `IEventsModule` public arayüzü ile bileti `Booked`'a çevirir. | `GET /events`, `GET /events/{id}`, `POST /events/seed` |
| `Biletix.Modules.Bookings` | Satın alma akışı. Redis SETNX ile aşırı satım koruması. Aynı transaction içinde `IEventsModule.MarkTicketsBookedAsync` çağırır. | `POST /bookings/{eventId}` |
| `Biletix.Modules.Search` | Elasticsearch üzerinde `multi_match` + fuzzy + date range. **Sadece okuma.** | `GET /search` |

Her modül kendi entity'lerini ve `IEntityTypeConfiguration<T>` yapılandırmalarını taşır; `AppDbContext` startup'ta `ApplyConfigurationsFromAssembly` ile bunları toplar. Modüller arası iletişim **in-process interface çağrısı** — RabbitMQ veya HTTP yok.

### Kritik garantiler

- **Aşırı satım koruması (CP yolu):** `TicketLockService.TryAcquireAllAsync` Redis `SET ticket:{id} {bookingId} NX EX 120` kullanır. 100 bilet için en fazla 100 başarılı booking; DB-seviyesinde lock kullanılmaz.
- **Atomik state geçişi:** Bir booking onaylandığında `bookings` INSERT ve `tickets` UPDATE **aynı Postgres transaction'ında** olur. Outbox ve message broker yok; çünkü artık tek DB var.
- **Search senkronizasyonu (AP yolu):** Postgres CDC (Debezium) → Kafka → Elasticsearch. Uygulama kodu ES'e yazmaz; tek truth source Postgres, ES türetilmiş view.
- **`LIKE` yok:** Arama Postgres'te değil ES'te koşar. `<500ms` hedefi `multi_match` ile karşılanır.

## Çalıştırma

```bash
cd build
docker compose up -d --build
```

API ayağa kalktığında migration otomatik koşar. Sonra CDC pipeline'ını kaydedin:

```bash
chmod +x register-connectors.sh
./register-connectors.sh
```

Bu script sırasıyla: `REPLICA IDENTITY FULL` set eder, Debezium source'u kaydeder, snapshot
topic'lerini bekler, ksqlDB join pipeline'ını uygular, Elasticsearch `events` index mapping'ini
oluşturur ve ES sink connector'ını kaydeder.

> Not: Stock `debezium/connect` image'ı ES sink connector'unu içermediği için Kafka Connect artık
> `build/connect/Dockerfile` ile custom build edilir (`confluentinc-kafka-connect-elasticsearch`
> plugin'i image'a gömülür). `docker compose up --build` bunu otomatik halleder.
>
> Not: Tek broker'lı kurulumda ksqlDB'nin transaction state log'u için broker'da
> `transaction.state.log.replication.factor=1` ve `transaction.state.log.min.isr=1` ayarlıdır
> (compose `kafka` servisinde). Aksi halde ksqlDB komut topic'i `InitProducerId` timeout'una düşer.

## Smoke Test

### 1. Etkinlik + 100 bilet seed et
```bash
curl -X POST http://localhost:8080/events/seed -H 'Content-Type: application/json' -d '{
  "title": "Tarkan İstanbul",
  "startsAt": "2026-09-15T20:00:00Z",
  "venueName": "Vodafone Park",
  "city": "İstanbul",
  "performerName": "Tarkan",
  "ticketCount": 100,
  "price": 1500
}'
```
Dönen `id`'yi `$EVENT_ID` olarak saklayın.

### 2. Etkinliği getir
```bash
curl http://localhost:8080/events/$EVENT_ID | jq '.tickets | length'   # 100
```

### 3. Arama (CDC çalışıyorsa)
```bash
curl "http://localhost:8080/search?keyword=tarkan"
```

### 4. Overselling testi
```powershell
./scripts/oversell-test.ps1 -EventId <EVENT_ID>
```
110 paralel istek; beklenti: en fazla bilet sayısı kadar 200, kalanlar 409.

### 5. Booking sonrası status
```bash
curl http://localhost:8080/events/$EVENT_ID | jq '[.tickets[] | select(.status=="Booked")] | length'
```

## Ports

| Servis | Host port |
|---|---|
| Biletix.Api | **8080** |
| Postgres | 5432 |
| Redis | 6379 |
| Kafka | 9092 |
| Kafka Connect REST | 8085 |
| ksqlDB REST | 8088 |
| Elasticsearch | 9200 |

## Sonraki iterasyon

- API Gateway / rate limiting (YARP)
- Bekleme Salonu (Redis sorted set + SignalR)
- JWT auth, gerçek ödeme (Iyzico/Stripe)
- Observability (OpenTelemetry + Jaeger)
- Modülleri ileride mikroservise ayırmak gerekirse: `IEventsModule` çağrıları event-based replacement (RabbitMQ) ile değiştirilir; sınırlar bugünden net.
