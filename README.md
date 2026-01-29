# Order Processing Service

A microservice that processes orders asynchronously using .NET 10, PostgreSQL, and RabbitMQ.

## Prerequisites

- [Docker Desktop](https://www.docker.com/products/docker-desktop/) installed and running

## How to Run

### 1. Start the infrastructure (PostgreSQL + RabbitMQ + App)

```bash
cd OrderProcessing
docker-compose up --build
```

Wait until you see logs indicating the app is ready (database migrated, connected to RabbitMQ).

### 2. Test the API

**Generate a GUID (for testing):**
```bash
curl http://localhost:8080/guid
```

**Create an order:**
```bash
curl -X POST http://localhost:8080/order \
  -H "Content-Type: application/json" \
  -d '{
    "id": "550e8400-e29b-41d4-a716-446655440000",
    "customerId": 123,
    "items": [{"productId": "PROD-001", "quantity": 2}]
  }'
```

**Check order status:**
```bash
curl http://localhost:8080/order/550e8400-e29b-41d4-a716-446655440000
```

### 3. Access RabbitMQ Management UI

- URL: http://localhost:15672
- Username: `guest`
- Password: `guest`

## API Endpoints

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/order` | Submit a new order (returns 202 Accepted) |
| GET | `/order/{id}` | Get order by ID |
| GET | `/stats` | Get queue depth, order counts, and processing metrics |
| GET | `/guid` | Generate a new GUID (helper endpoint) |

### Stats Endpoint

```bash
curl http://localhost:8080/stats
```

> **Note:** This is a simplified solution. For production, metrics like "processed in last minute" would typically use Prometheus + Grafana with built-in .NET `System.Diagnostics.Metrics` classes. We keep it simple here to avoid bloating the infrastructure for this example.

Returns:
```json
{
  "timestamp": "2026-01-29T14:30:00Z",
  "queue": { "messagesInQueue": 0 },
  "lastMinute": { "created": 5, "processed": 3 },
  "totalByStatus": [
    { "status": "Pending", "count": 2 },
    { "status": "Processed", "count": 10 }
  ],
  "totalOrders": 12
}
```

## Monitoring

### RabbitMQ Management UI

- **URL:** http://localhost:15672
- **Login:** `guest`
- **Password:** `guest`

Use this UI to monitor queues, message rates, and connections in real-time.

## Design Decisions

### Why RabbitMQ?
- Simple to set up and use
- Reliable message delivery with acknowledgments
- Built-in management UI for monitoring
- Good fit for this use case (single queue, simple routing)

### Idempotency
- Order ID is provided by the client (acts as idempotency key)
- Duplicate orders with the same ID return 202 without creating duplicates
- Worker checks order status before processing to prevent double-processing

### Code-First Database
- EF Core migrations run automatically on startup
- No manual SQL scripts required
- Database schema is created from C# entity classes

### Trade-offs
- **Simplified delivery guarantee**: Save to DB first, then publish to queue. If crash happens between these steps, order stays in `Pending` state. A reconciliation job could be added later.
- **No Outbox Pattern**: For simplicity, we don't use transactional outbox. This is acceptable for a test assignment.
- **In-memory queue connection**: Single RabbitMQ connection shared across the app. For production, connection pooling would be better.

### Pricing Strategy (Current vs Production)

**Current implementation (demo):**
- `TotalAmount` is `null` when order is created
- Worker calculates a random value during processing (simulates backend calculation)

**Production approach options:**
1. **Price Catalog Service** — separate microservice that stores product prices. Worker queries it by ProductId to calculate total.
2. **Inventory table with prices** — add `Price` column to `Inventory` entity. Worker joins Items with Inventory to sum up.
3. **Event-driven pricing** — subscribe to price change events, cache prices locally, calculate during processing.
4. **Snapshot pricing** — store price at order creation time (requires fetching prices in POST handler, adds latency).

## Assumptions

- Client generates unique Order IDs (UUIDs)
- TotalAmount is calculated server-side during order processing
- Single instance deployment (no distributed locking needed)
- Development environment (no HTTPS, no authentication)