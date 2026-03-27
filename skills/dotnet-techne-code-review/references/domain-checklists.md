# Domain Checklists
Use only sections relevant to changed code. If uncertain, run all sections but prioritize correctness/security/data-loss risks first.

## Correctness and API design (blocking first)
Check for:
- CancellationToken propagation and correct `OperationCanceledException` handling
- exception safety (no swallowed exceptions without decision)
- thread-safety and lifetimes (singleton with mutable state, reuse of non-thread-safe types)
- deterministic behavior under retries and partial failures
- public API clarity:
  - naming, overloads, nullability, default values, backwards compatibility

## Style and maintainability
Check for:
- consistent naming, minimal cleverness
- small methods with clear responsibilities
- avoid unnecessary comments; prefer self-documenting code
- avoid duplicated logic; extract shared helpers where it reduces risk
- DI usage: interfaces over concretes, avoid service locator patterns unless justified

## Performance, low-GC, AOT/trimming
### Hot-path allocation
Flag:
- LINQ in hot paths
- closures and captures in loops
- `string` formatting/interpolation in tight loops
- `enum.ToString()` and boxing (prefer cached names or numeric)
- `DateTime.ToString(...)` in hot paths (prefer numeric timestamps or cached strategy)
- per-call allocations for headers/properties/options objects

### Concurrency and buffering
Check:
- explicit backpressure policy (bounded queue/channel)
- no unbounded buffers unless explicitly required
- for `Channel<T>`:
  - `SingleReader = true` / `SingleWriter = true` where applicable
  - intentional full-mode behavior (wait, drop oldest, drop newest)

### AOT/trimming
Check:
- JSON in libraries prefers source generation (`JsonSerializerContext` / `JsonTypeInfo`)
- if reflection JSON is supported, annotate public APIs with:
  - `[RequiresDynamicCode]`
  - `[RequiresUnreferencedCode]`
- trimmer warnings fixed at root cause or suppressed with boundary-level justification

## Logging and observability
Check for:
- structured logs with stable IDs: `EventId`, `CorrelationId`, `CausationId` (avoid PII)
- `LoggerMessage` source generators on hot paths
- log levels that avoid noisy retry-loop warning spam
- useful metrics: latency, retries, buffer depth, DB call duration

## Security
Check for:
- no secrets in code/logs
- input validation (especially webhooks)
- explicit authentication/authorization boundaries
- safe serialization defaults (avoid dangerous polymorphic deserialization)
- SSRF controls on outbound HTTP (validation/allow-list)
- least-privilege assumptions for DB/broker credentials

## Database (EF Core / PostgreSQL)
Use:
- Bash: `./scripts/find-deps.sh DbContext`
- PowerShell: `.\scripts\find-deps.ps1 -Target DbContext`

Check for:
- correct tracking usage (`AsNoTracking` where appropriate)
- N+1 query patterns
- missing indexes implied by query shapes
- transaction boundaries aligned with business invariants
- concurrency tokens / unique constraints for idempotency
- connection pooling and proper async calls
- avoid large materialization; prefer projection + pagination

## Messaging (RabbitMQ)
Use:
- Bash: `./scripts/find-deps.sh IMessagePublisher` or `./scripts/find-deps.sh IChannel`
- PowerShell: `.\scripts\find-deps.ps1 -Target IMessagePublisher`

Check for:
- connection/channel lifecycle correctness and shutdown cleanup
- publisher confirms when reliability is required
- retry policy and retryable exception classification
- topology declarations strategy (exchange/queue/bindings)
- mandatory publish and returned-message handling expectations
- backpressure behavior when buffer is full (drop vs block vs fail-fast)
