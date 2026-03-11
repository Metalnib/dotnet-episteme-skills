# Synopsis: HTTP Cross-Service Resolution

Synopsis traces outbound HTTP calls from your code to the internal endpoints they target across repositories. This is the core mechanism for mapping microservice dependencies.

## How resolution works

When Synopsis finds an HTTP call (e.g., `GetAsync`, `PostAsJsonAsync`), it:

1. **Identifies the HTTP client** — named client, typed client, or raw `HttpClient`
2. **Extracts the service hint** — client name, base URL host, or config key
3. **Extracts the request path** — from string literals or interpolated strings
4. **Combines base URL + path** into a full URI when possible
5. **Matches against known endpoints** — route pattern matching with service-affinity filtering

The result is an edge in the graph: `HttpClient → ExternalService → ExternalEndpoint → [resolved internal Endpoint]`

## HTTP client patterns (best to worst for resolution)

### Named HTTP clients (best)

```csharp
// Startup / Program.cs
services.AddHttpClient("CatalogApi", client =>
{
    client.BaseAddress = new Uri(config["Services:CatalogApi:BaseUrl"]);
});

// Usage
public class OrdersService
{
    private readonly IHttpClientFactory _factory;

    public async Task<Product> GetProduct(int id)
    {
        var client = _factory.CreateClient("CatalogApi");
        return await client.GetFromJsonAsync<Product>($"/products/{id}");
    }
}
```

**What Synopsis extracts:**
- Client name: `"CatalogApi"` (from `CreateClient` argument)
- Base URL: `"http://catalog-api:5000"` (from appsettings, matched by client name)
- Request path: `"/products/{id}"` (from string literal)
- Service hint stem: `"catalog"` → matches repo `catalog-api` or project `Catalog.Api`
- **Certainty: Inferred** (single match via name affinity)

### Typed HTTP clients (good)

```csharp
// Startup
services.AddHttpClient<ICatalogClient, CatalogClient>(client =>
{
    client.BaseAddress = new Uri(config["Services:CatalogApi:BaseUrl"]);
});

// Usage
public class CatalogClient : ICatalogClient
{
    private readonly HttpClient _http;

    public async Task<Product> GetProduct(int id)
    {
        return await _http.GetFromJsonAsync<Product>($"/products/{id}");
    }
}
```

**What Synopsis extracts:**
- Client name: `"CatalogClient"` (from containing type name)
- Base URL: from appsettings matched by type name
- Service hint stem: `"catalog"` → same affinity matching
- **Certainty: Inferred**

### Raw HttpClient with config base URL (adequate)

```csharp
public class PaymentGateway
{
    private readonly HttpClient _client;

    public PaymentGateway(HttpClient client, IConfiguration config)
    {
        _client = client;
        _client.BaseAddress = new Uri(config["Services:Payments:BaseUrl"]);
    }

    public async Task ChargeAsync(int orderId, decimal amount)
    {
        await _client.PostAsJsonAsync("/charges", new { orderId, amount });
    }
}
```

**What Synopsis extracts:**
- Client name: `"PaymentGateway"` (from containing type)
- Base URL: Synopsis searches appsettings for keys containing `"url"` + `"PaymentGateway"`
- Service hint stem: `"payment"` → matches repo `payment-service` or project `Payments.Api`
- **Certainty: Inferred** if base URL found, **Ambiguous** otherwise

### Raw HttpClient, no config (poor)

```csharp
var client = new HttpClient();
var result = await client.GetAsync("http://localhost:5002/api/inventory/check");
```

**What Synopsis extracts:**
- Client name: variable name `"client"` (weak signal)
- Base URL: none
- Full URL: `"http://localhost:5002/api/inventory/check"` (from string literal)
- Service hint: `"localhost"` (useless for affinity matching)
- **Certainty: Ambiguous** — route match only, no service affinity

### SendAsync with HttpRequestMessage (minimal)

```csharp
var request = new HttpRequestMessage(HttpMethod.Post, "/api/notify");
await _httpClient.SendAsync(request);
```

**What Synopsis extracts:**
- Request path: `null` (Synopsis doesn't trace `HttpRequestMessage` construction)
- **Certainty: Unresolved** — the edge exists but target is unknown

## appsettings configuration

Synopsis reads all `appsettings*.json` files found during workspace discovery. It flattens nested JSON into colon-delimited keys and searches for URL patterns.

### Configuration patterns that Synopsis understands

**Recommended (strongest signal):**
```json
{
  "Services": {
    "CatalogApi": {
      "BaseUrl": "http://catalog-api:5000"
    },
    "PaymentService": {
      "BaseUrl": "http://payment-service:5001"
    }
  }
}
```

Synopsis looks for config keys containing both `"url"` (or `"baseurl"`) and the client name hint. The key `Services:CatalogApi:BaseUrl` matches client hint `"CatalogApi"` because the key contains both `"url"` and `"CatalogApi"`.

**Also works:**
```json
{
  "ExternalServices": {
    "CatalogApiUrl": "http://catalog-api:5000",
    "PaymentsBaseUrl": "http://payment-service:5001"
  }
}
```

**Weak (avoid):**
```json
{
  "ApiUrl": "http://some-service:5000"
}
```

Generic key name `"ApiUrl"` has no service identity. If multiple services share this pattern, Synopsis can't distinguish which URL belongs to which client.

### How base URL lookup works

For a client with name hint `"CatalogClient"`, Synopsis searches in order:

1. **Config keys in the same repo** that contain both `"url"`/`"baseurl"` AND `"CatalogClient"` (or its value contains the hint) → returns matching URL value
2. **Config keys in the same repo** that contain `"url"` → returns first valid URL (weak fallback)
3. If nothing found → `null` base URL, resolution relies on request path only

## Service-affinity matching

When matching an HTTP call to a target endpoint, Synopsis uses the service hint to narrow candidates. The hint comes from:

| Source | Example | Extracted hint |
|---|---|---|
| Named client argument | `CreateClient("CatalogApi")` | `"CatalogApi"` |
| Typed client class name | `class CatalogClient` | `"CatalogClient"` |
| HttpClient variable name | `var catalogHttp = new HttpClient()` | `"catalogHttp"` |
| Base URL host | `http://catalog-api:5000` | `"catalog-api"` |

The hint is normalized by stripping common suffixes and separators:

| Input | Normalized stem |
|---|---|
| `CatalogClient` | `catalog` |
| `catalog-api` | `catalog` |
| `Catalog.Api` | `catalog` |
| `repo-catalog` | `catalog` |
| `svc-catalog-service` | `catalog` |
| `PaymentGateway` | `payment` |
| `orders-service` | `orders` |

Stripped suffixes: `Client`, `Service`, `Api`, `Gateway`, `Proxy`, `Handler`, `Server`
Stripped prefixes: `repo-`, `svc-`, `service-`

The normalized stem is compared against the target endpoint's **repository name** and **project name**. A match means the HTTP call is likely targeting that service.

## What produces each certainty level

| Certainty | Meaning | When |
|---|---|---|
| **Exact** | Never used for HTTP resolution | HTTP calls are inherently runtime-resolved |
| **Inferred** | Single matching endpoint found via route + service affinity | Named/typed client with config URL, target repo identifiable |
| **Ambiguous** | Multiple endpoints matched, or route-only match without affinity | Generic client name, overlapping routes, no config |
| **Unresolved** | Request path couldn't be extracted statically | `SendAsync`, dynamic URLs, non-constant expressions |

## Recommendations for best resolution

1. **Use `IHttpClientFactory` with named clients** — the client name is Synopsis's strongest signal
2. **Put base URLs in appsettings with descriptive keys** — `Services:CatalogApi:BaseUrl` not `ApiUrl`
3. **Name your repos and projects consistently** — if the client is `CatalogClient`, the target repo should contain `catalog`
4. **Use string literals for request paths** — `"/products/{id}"` is extractable, `$"{baseUrl}/products/{id}"` with a variable `baseUrl` is not
5. **Avoid `new HttpClient()`** — no name, no config, no service identity

## Graph output for HTTP calls

For each HTTP call, Synopsis produces this subgraph:

```
[Method]        OrdersService.GetProduct
    ↓ UsesHttpClient
[HttpClient]    CatalogClient (baseUrl: http://catalog-api:5000)
    ↓ CallsHttp
[ExternalService] catalog-api
    ↓ CallsHttp
[ExternalEndpoint] GET /products/{id}
    ↓ ResolvesToService (Inferred)
[Endpoint]      GET /products/{id}  (in repo catalog-api, project Catalog.Api)
    ↓ CrossesRepoBoundary
[Endpoint]      GET /products/{id}  (cross-repo edge from orders → catalog)
```

Querying this:
```bash
# Who calls catalog-api?
synopsis query impact --node "catalog-api" --direction upstream --graph graph.json

# What does OrdersService depend on?
synopsis query symbol --fqn "OrdersService" --blast-radius --graph graph.json

# Show all cross-repo calls
synopsis query impact --node "CrossesRepoBoundary" --graph graph.json
```
