# Order Processing Service

Async order processing microservice: **.NET 10 + PostgreSQL + RabbitMQ**

---

## 🚀 How to Run

```bash
cd OrderProcessing
docker-compose up --build
```

Wait for logs: `Database migrated`, `Connected to RabbitMQ`.

**Test the API:**
### Windows (cmd)
```cmd
# Create order
curl -X POST http://localhost:8080/order -H "Content-Type: application/json" -d "{\"id\": \"550e8400-e29b-41d4-a716-446655440000\", \"customerId\": 123, \"items\": [{\"productId\": \"PROD-001\", \"quantity\": 2}]}"
 
# Check order
curl http://localhost:8080/order/550e8400-e29b-41d4-a716-446655440000
 
# View stats (observability)
curl http://localhost:8080/stats
```

### Linux/macOS (bash)

```bash
# Create order
curl -X POST http://localhost:8080/order -H "Content-Type: application/json" \
  -d '{"id": "550e8400-e29b-41d4-a716-446655440002", "customerId": 123, "items": [{"productId": "PROD-001", "quantity": 2}]}'
 
# Check order
curl http://localhost:8080/order/550e8400-e29b-41d4-a716-446655440000
 
# View stats (observability)
curl http://localhost:8080/stats
```

**RabbitMQ UI:** http://localhost:15672 (guest/guest)

---

## 📐 Design Decisions

### Why RabbitMQ?

I chose RabbitMQ as the message broker for asynchronous order processing.


**Why not Kafka?** Overkill for single-service order processing. Kafka shines for event sourcing, log aggregation, multi-consumer scenarios.

**Why not Redis?** No built-in acks/redelivery. Would need manual implementation of reliability patterns.

**Conclusion:** RabbitMQ is the sweet spot — reliable delivery with minimal code, perfect for task queues.

### Architecture
```
POST /order → DB (Order + Outbox) → OutboxProcessor → RabbitMQ → Worker → DB (Processed)
```

- **Outbox Pattern**: Order and OutboxMessage saved in single transaction. If RabbitMQ fails, OutboxProcessor retries every 1 second.
- **Idempotency**: Client-provided Order ID prevents duplicates. Worker checks status before processing.
- **Optimistic Locking**: RowVersion column prevents race conditions in concurrent processing.

### Entities
| Entity | Purpose |
|--------|---------|
| **Order** | CustomerId, Items (JSON), TotalAmount, Status, RowVersion |
| **Inventory** | ProductId, Quantity, Price — used by worker to calculate total and decrement stock |
| **OutboxMessage** | Ensures RabbitMQ publish even if initial publish fails |

### Business Logic (Worker)
1. Parse order items from JSON
2. For each item: lookup `Inventory.Price`, check stock, decrement `Quantity`
3. Calculate `TotalAmount` = Σ(price × quantity)
4. Mark order as Processed

---

## ⚖️ Trade-offs

| Decision | Benefit | Downside |
|----------|---------|----------|
| **Outbox Pattern** | Guaranteed delivery | Extra table, 1s latency |
| **TotalAmount calculated by worker** | Simpler API | Price not known at order time |
| **Single RabbitMQ connection** | Simple code | Not ideal for high load |
| **EF Core auto-migrations** | No manual SQL | Less control in production |

---

## 📝 Assumptions

- Client generates unique Order IDs (UUIDs) — acts as idempotency key
- TotalAmount is calculated server-side from Inventory prices during processing
- Single instance deployment — RowVersion handles concurrency, no distributed locks
- Development environment — no HTTPS, no authentication
- Inventory is pre-seeded with sample products (PROD-001, PROD-002, PROD-003)

---

## 📊 Observability

**Endpoint:** `GET /stats`

```json
{
  "timestamp": "2026-01-29T14:30:00Z",
  "queue": { "messagesInQueue": 0 },
  "lastMinute": { "created": 5, "processed": 3 },
  "totalByStatus": [{ "status": "Processed", "count": 10 }],
  "totalOrders": 12
}
```

Logs show order processing events. For production: Prometheus + Grafana.

---

## 🧪 Testing

```bash
cd OrderProcessing.Tests
dotnet test
```

**13 tests** covering: outbox, idempotency, concurrency handling, inventory pricing, stock decrement.

> **Note:** A load test client exists in `OrderProcessing.Client/` for internal verification.

---

## 🔧 Advanced

### Persistence & Container Restart

| Component | Data Location | On Restart |
|-----------|---------------|------------|
| **PostgreSQL** | Docker volume `postgres_data` | ✓ Data survives |
| **RabbitMQ** | Docker volume `rabbitmq_data` | ✓ Queues + messages survive |
| **App container** | Stateless | ✓ Reconnects to DB and RabbitMQ |

**Test it:** `docker-compose down && docker-compose up` — orders and queue state persist.

**Full reset:** `docker-compose down -v` — removes volumes, fresh start.

### Exactly-Once: Failure Scenarios

| Scenario | What Happens | How It's Handled |
|----------|--------------|------------------|
| **App crashes after DB save, before RabbitMQ publish** | Order in DB, no message in queue | OutboxProcessor picks up unprocessed OutboxMessage, publishes to RabbitMQ |
| **RabbitMQ down during publish** | OutboxProcessor retries every 1s | Eventually publishes when RabbitMQ recovers |
| **Worker crashes mid-processing** | Message not ACKed | RabbitMQ redelivers; worker checks `Status`, skips if already Processed |
| **Worker crashes after DB save, before ACK** | Order Processed, message redelivered | Worker sees `Status=Processed`, skips, ACKs |
| **Duplicate message in queue** | Same OrderId twice | Worker idempotency check: `if Status=Processed → skip` |
| **Two workers process same order** | Race condition | Optimistic locking: one succeeds, other gets `DbUpdateConcurrencyException` → skips |
| **DB down during processing** | Worker can't save | Exception → NACK → RabbitMQ requeues message |
| **Duplicate POST request** | Same OrderId submitted twice | Endpoint checks `FindAsync(id)`, returns existing order |

### Message Flow Guarantees

```
1. POST /order
   └─ BEGIN TRANSACTION
       ├─ INSERT Order (Pending)
       └─ INSERT OutboxMessage
      COMMIT ← atomic, both or neither

2. OutboxProcessor (background, 1s loop)
   └─ SELECT * FROM OutboxMessages WHERE ProcessedAt IS NULL
       └─ RabbitMQ.Publish(OrderId)
           └─ UPDATE OutboxMessage SET ProcessedAt = NOW()

3. Worker (on message received)
   └─ SELECT Order WHERE Id = ?
       ├─ IF Status = Processed → ACK, skip
       └─ ELSE → Process, UPDATE Status = Processed
           └─ SaveChanges (with RowVersion check)
               ├─ Success → ACK
               └─ ConcurrencyException → ACK (another worker won)
```

### What Exactly-Once Does NOT Cover

| Scenario | Why | Mitigation |
|----------|-----|------------|
| **Network partition during processing** | Message could be redelivered | Idempotency handles it |
| **Inventory double-decrement** | No row-level locking on Inventory | Would need `SELECT FOR UPDATE` or separate inventory service |
| **External API calls in worker** | Can't roll back external calls | Saga pattern or compensation logic |