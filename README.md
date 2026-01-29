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
```bash
# Create order
curl -X POST http://localhost:8080/order -H "Content-Type: application/json" \
  -d '{"id": "550e8400-e29b-41d4-a716-446655440000", "customerId": 123, "items": [{"productId": "PROD-001", "quantity": 2}]}'

# Check order
curl http://localhost:8080/order/550e8400-e29b-41d4-a716-446655440000

# View stats (observability)
curl http://localhost:8080/stats
```

**RabbitMQ UI:** http://localhost:15672 (guest/guest)

---

## 📐 Design Decisions

### Why RabbitMQ over Redis?
| Factor | RabbitMQ ✓ | Redis |
|--------|------------|-------|
| Message acknowledgments | Built-in | Manual implementation |
| Redelivery on failure | Automatic | Manual |
| Management UI | Included | Separate tool needed |
| Complexity | Simple for queues | Better for caching |

**Conclusion:** RabbitMQ provides reliable delivery out-of-the-box with less code.

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
| **Inventory** | ProductId, Quantity (placeholder for stock validation) |
| **OutboxMessage** | Ensures RabbitMQ publish even if initial publish fails |

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
- TotalAmount is calculated server-side during processing (simulated as random for demo)
- Single instance deployment — RowVersion handles concurrency, no distributed locks
- Development environment — no HTTPS, no authentication
- Inventory entity exists as placeholder — not integrated into order validation yet

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

**7 tests** covering: outbox, idempotency, concurrency, error handling.

> **Note:** A load test client exists in `OrderProcessing.Client/` for internal verification.