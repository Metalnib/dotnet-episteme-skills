# Domain Checklists
Use only sections relevant to changed code. If uncertain, run all sections but prioritize correctness/security/data-loss risks first.

`Use:` blocks give two variants: the scripts need a shell; agents without one (the plugin's read-only reviewers) use the Grep pattern instead.

## Correctness and API design (blocking first)
Check for:
- CancellationToken propagation and correct `OperationCanceledException` handling
- sync-over-async: no `.Result` / `.Wait()` / `GetAwaiter().GetResult()` on async work (deadlocks, thread-pool starvation)
- `async void` (only event handlers); fire-and-forget tasks have an owner and observe exceptions
- exception safety: no swallowed exceptions without decision; invariants hold when an exception escapes mid-operation (no partially mutated state left behind)
- `throw;` not `throw ex;` (stack trace loss)
- DI lifetimes: no captive dependency (singleton holding scoped/transient), no resolving scoped services from the root provider
- disposal ownership: `IDisposable`/`IAsyncDisposable` (`await using`), streams, `HttpResponseMessage`, `CancellationTokenRegistration`
- thread-safety and lifetimes (singleton with mutable state, reuse of non-thread-safe types)
- check-then-act races; `SemaphoreSlim` released on all paths (`try/finally`)
- deterministic behavior under retries and partial failures
- time and culture: `UtcNow` not `Now` for machine time; culture-invariant parse/format of machine data; `decimal` for money
- equality contracts: `Equals`/`GetHashCode` pairs; records with mutable collection members (structural equality lies); mutable objects as dictionary keys
- `IEnumerable` enumerated once (no multiple enumeration); no collection modification during enumeration
- NRT honesty: no null-forgiving `!` that hides a real nullability hole
- public API clarity:
  - naming, overloads, nullability, default values, backwards compatibility

## Style and maintainability
Check for:
- consistent naming, minimal cleverness
- small methods with clear responsibilities; guard clauses over deep nesting
- avoid unnecessary comments; prefer self-documenting code
- avoid duplicated logic; extract shared helpers where it reduces risk
- DI usage: interfaces over concretes, avoid service locator patterns unless justified
- no dead code left behind by the change

## Performance, low-GC, AOT/trimming
### Hot-path allocation
Flag:
- LINQ in hot paths
- closures and captures in loops
- `string` formatting/interpolation in tight loops
- `enum.ToString()` and boxing (prefer cached names or numeric)
- `DateTime.ToString(...)` in hot paths (prefer numeric timestamps or cached strategy)
- per-call allocations for headers/properties/options objects
- large-struct copies (pass by `in`/`ref readonly`); defensive copies from non-readonly structs in readonly fields
- collections: presized capacity where count is known; `TryGetValue` over `ContainsKey`+indexer; `FrozenDictionary`/`FrozenSet` for static lookups
- buffers: `ArrayPool`/`Span`/`stackalloc` over `byte[]` churn; avoid repeated large-object-heap (≥85 KB) allocations
- regex: `[GeneratedRegex]` (or compiled, cached) - never construct per call

### Concurrency and buffering
Check:
- explicit backpressure policy (bounded queue/channel)
- no unbounded buffers unless explicitly required
- `ValueTask` consumed exactly once - never awaited twice or stored
- no `Task.Run` offloading inside ASP.NET request paths; parallel fan-out bounded (`Parallel.ForEachAsync` with `MaxDegreeOfParallelism`, no unbounded `Task.WhenAll` over large sets)
- for `Channel<T>`:
  - `SingleReader = true` / `SingleWriter = true` where applicable
  - intentional full-mode behavior (wait, drop oldest, drop newest)

### Caching
Check:
- `IMemoryCache`/custom caches have size limits and expiration (unbounded growth is a slow OOM)
- cache stampede handled (single-flight `GetOrCreateAsync`, per-key locking) where recompute is expensive
- expensive reflection/metadata computed once and memoized, not per call

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
- log levels that avoid noisy retry-loop warning spam; expected domain failures are not `Error` (alert fatigue)
- exceptions logged as the exception object, once - no `ex.Message` interpolation, no log-and-rethrow double logging
- no secrets, tokens, or connection strings reachable through logged exception messages or config dumps
- metrics: latency, retries, buffer depth, DB call duration - with bounded label cardinality (no user IDs/raw URLs as tags)
- outbound calls and message handling wrapped in `Activity` spans; trace context propagated across HTTP headers and message headers
- retry exhaustion and circuit-breaker state changes are logged and metered (silence here hides outages)
- new external dependency → health check registered

## Security
Check for:
- no secrets in code/config/logs; secrets come from a secret store, not committed appsettings
- input validation (especially webhooks); webhook/callback signatures verified with `CryptographicOperations.FixedTimeEquals` (timing-safe)
- explicit authentication/authorization boundaries; object-level authorization (IDOR - ownership checks, not just roles); `[AllowAnonymous]` audited; default-deny for new endpoints
- injection: SQL parameterized only (`FromSqlInterpolated`, never string-built `FromSqlRaw`); no command/LDAP/XPath concatenation
- overposting/mass assignment: bind explicit DTOs, never entities
- safe serialization defaults: no `BinaryFormatter`; no Newtonsoft `TypeNameHandling.Auto/All` on untrusted input; XML readers with DTD processing off (XXE)
- crypto: `RandomNumberGenerator` (not `Random`) for anything security-relevant; no MD5/SHA1 for security purposes; passwords via PBKDF2/bcrypt/argon2; no hardcoded keys/IVs
- ReDoS: no catastrophic-backtracking regex on untrusted input; regex timeouts set
- path traversal on user-supplied paths/filenames; open redirect on user-supplied URLs
- TLS: no `ServerCertificateCustomValidationCallback` bypass; no credentials over `http://`
- token validation complete: issuer, audience, lifetime, and signature all enforced (no `ValidateX = false`)
- error responses leak nothing internal: no stack traces, connection strings, or raw vendor/transport text to callers
- SSRF controls on outbound HTTP (validation/allow-list)
- least-privilege assumptions for DB/broker credentials
- new NuGet dependencies: trusted feed, pinned version, no known CVEs

## Database (EF Core / PostgreSQL)
Use (shell): `./scripts/find-deps.sh DbContext` (PowerShell: `.\scripts\find-deps.ps1 -Target DbContext`)
Use (no shell): Grep for `DbContext|DbSet<|FromSql` across changed projects

Check for:
- correct tracking usage (`AsNoTracking` where appropriate)
- N+1 query patterns; `Include` cartesian explosion (`AsSplitQuery` for multiple collections)
- missing indexes implied by query shapes
- transaction boundaries aligned with business invariants; isolation level explicit for read-modify-write
- concurrency tokens / unique constraints for idempotency
- migrations safe for rolling deploys: additive first, no destructive drop/rename in the same release as the code that stops using it
- Npgsql time handling: UTC `DateTime`/`DateTimeOffset` for `timestamptz`; no `DateTimeKind.Unspecified` writes
- `EnableRetryOnFailure` (execution strategy) vs manual `BeginTransaction` conflicts; `TransactionScope` with `TransactionScopeAsyncFlowOption.Enabled`
- set-based `ExecuteUpdate`/`ExecuteDelete` over load-modify-save loops
- connection pooling and proper async calls
- avoid large materialization; prefer projection + pagination

## Messaging (RabbitMQ)
Use (shell): `./scripts/find-deps.sh IMessagePublisher` or `./scripts/find-deps.sh IChannel` (PowerShell: `.\scripts\find-deps.ps1 -Target IMessagePublisher`)
Use (no shell): Grep for `IMessagePublisher|IChannel|BasicPublish|BasicConsume` across changed projects

Check for:
- connection/channel lifecycle correctness and shutdown cleanup; graceful shutdown drains in-flight messages before closing
- publisher confirms when reliability is required
- manual ack only after successful processing (no auto-ack for work that can fail); bounded prefetch (QoS)
- poison messages: bounded redelivery ending in DLX/parking queue - never infinite requeue
- consumer idempotency (dedup key or natural idempotence) - at-least-once delivery is the assumption
- dual-write hazard: DB state change + publish in one business operation needs an outbox (or a documented accepted risk)
- message contract evolution: additive changes, tolerant reader (see the serialisation skill)
- retry policy and retryable exception classification
- topology declarations strategy (exchange/queue/bindings)
- mandatory publish and returned-message handling expectations
- backpressure behavior when buffer is full (drop vs block vs fail-fast)
- ordering assumptions explicit (single active consumer / partitioning) or explicitly not relied on

## HTTP integration (endpoints, adapters, consumers)
Use (shell): `./scripts/find-deps.sh HttpClient` or `./scripts/find-deps.sh IHttpClientFactory` (PowerShell: `.\scripts\find-deps.ps1 -Target HttpClient`)
Use (no shell): Grep for `HttpClient|IHttpClientFactory|ChannelFactory|ClientBase` across changed projects

Outbound clients and adapters (REST/SOAP):
- `IHttpClientFactory` / typed clients with correct lifetimes (no captured `HttpClient` in singletons; `PooledConnectionLifetime` so DNS changes are picked up)
- timeouts set and `CancellationToken` flowed into every outbound call
- retry policy only on idempotent operations; retryable exception/status classification; resilience policy ordering intentional (timeout/retry/circuit-breaker nesting)
- fault-to-typed-error mapping: raw vendor/transport text (fault reasons, endpoint URIs) must not reach app users
- all fault types the transport can throw are handled (SOAP faults, communication, timeout)
- `HttpResponseMessage`/content disposed; large payloads streamed (`ResponseHeadersRead`), not buffered

Inbound endpoints:
- contract changes are backward compatible or versioned (see cross-repo impact for consumers)
- status-code semantics match the error taxonomy; no 500 for expected domain failures
- input validation at the boundary, especially webhooks and callbacks

Consumers and adapters of external systems:
- idempotent handling of replays and duplicate deliveries
- poison/permanent-failure path distinct from transient retry
- ordering assumptions documented and enforced, or explicitly not relied on
