# Architectural Document: Graph Path Query (`GET /api/nodes?path=...`)

## Goals

Collapse the *"resolve A → resolve B linked to A → list C linked to B"* class of question into a single round trip that compiles to one server-side joined SQL statement. The motivating example, *"open tasks for project DiVoid in organization Pooshit"*, is three round trips today; under this feature it is one. The feature is purely additive: existing single-hop `?linkedto=` traversal stays exactly as it is and remains the recommended path for trivial cases.

The endpoint must:

- Accept a textual *path expression* describing an N-hop walk through the graph, with per-hop filtering on type, name, status, and id.
- Return a flat list of nodes from the **terminal hop** in the same envelope and field shape as `GET /api/nodes` (`{ "result": [...], "total": N, "continue": ... }`).
- Be designed so that boolean operators (`or`, `and`, `not`) extend the grammar without redesigning it. v1 may ship a subset; the extension hook must already exist.
- Plumb `CancellationToken` from the HTTP request all the way to the SQL execution call, so request timeouts and client disconnects abort the database work.

## Non-goals

- Replacing or deprecating any existing filter on `GET /api/nodes`. The new path query is *advanced mode*; everyone keeps using `?linkedto=` for the easy cases.
- Aggregation operators (`count`, `sum`, `group by`). The result is always a flat node list.
- Mutating queries (delete-by-path, link-by-path, etc.). Read-only.
- Cross-database / federated paths. Single graph, single database.
- Caching of resolved paths. The SQL statement and the database planner are the cache.
- Hard depth/breadth ceilings. The cancellation token plus Kestrel's request timeout are the safety net; the operator is trusted.
- A new response envelope. Mixed-type results are flat; consumers group client-side.

## Current state

`GET /api/nodes` (`Backend/Services/Nodes/NodeService.cs:89` / `:141`) accepts arrays for `id`, `type`, `name`, `status`, and a single-hop `linkedto` filter implemented as a Union of two `NodeLink` directions (`:113-120`, with the in-source `// TODO: Lateral Join in Ocelot` marker). Multi-hop traversal is not expressible; clients stitch the hops together by issuing N requests and forwarding ids. Schema, tables, indices, and the `NodeMapper` join (`Backend/Models/Nodes/NodeMapper.cs:41-46`, joining `Node` to `NodeType`) are all reused as-is by this feature.

---

## A. Grammar and parser shape

### Decision

**Bracketed per-hop selectors separated by `/`, embedded in a single `path=` query parameter on `GET /api/nodes`.** A small hand-written **recursive-descent parser** consumes the string and produces an in-memory query tree.

### v1 grammar (informal EBNF)

```
path        = segment ( "/" segment )*
segment     = "[" predicate ( "," predicate )* "]"
            | "[" "]"                              ; "any node" wildcard hop
predicate   = key ":" valueList
key         = "id" | "type" | "name" | "status"
valueList   = value ( "|" value )*                 ; OR within a key
value       = bareToken | quotedString
bareToken   = <one or more chars excluding , ] | : / [ " whitespace>
quotedString= '"' <any char, with \" and \\ escapes> '"'
```

Concrete examples (URL-encoding omitted for readability):

| Intent | Path |
|---|---|
| Open tasks for project DiVoid in org Pooshit | `[type:organization,name:Pooshit]/[type:project,name:DiVoid]/[type:task,status:open]` |
| Tasks **or** questions linked to project 42 | `[id:42]/[type:task\|question]` |
| Anything linked to project DiVoid | `[type:project,name:DiVoid]/[]` |
| Open or new tasks linked to a known project | `[id:42]/[type:task,status:open\|new]` |
| All projects with name starting `Di` (wildcard reuse) | `[type:project,name:Di%]` |

`fields`, `count`, `continue`, `sort`, `descending` remain top-level query parameters and apply to the **terminal hop only** — they have no per-hop meaning.

### v1 operator cut

| Operator | v1 | Hook for later |
|---|---|---|
| Multi-value within a key (`status:open\|new`) — a per-key OR | **In** | — |
| Multi-key conjunction within a segment (implicit AND between predicates) | **In** | — |
| Wildcard match on `name`/`status` (existing `%`/`_` semantics) | **In** | — |
| Top-level boolean grouping across segments (`(A/B) or (A/C)`) | **Deferred** | Parser already emits a `SegmentNode` tree; introduce `OrNode`/`AndNode` peers. SQL composer recurses; `OrNode` becomes a `UNION` of two compositions. |
| `not` on a key (`status:!closed`) or whole segment (`![type:archived]`) | **Deferred** | Parser produces `Predicate { Negated:bool }` and `Segment { Negated:bool }` from day one; v1 simply rejects `!` as a parse error. Composer turns negation into `NOT IN` / anti-join. |
| `and` *across types in one hop* (`[type:task & status:open]`) | **In** (this is just the implicit comma-AND) | — |
| Free-form parenthesized boolean expression replacing the path | **Deferred** | Same `OrNode`/`AndNode` mechanism. Grammar gains `expr = segment \| "(" expr ")" \| expr "or" expr \| expr "and" expr`. |

The decisive principle: anything *within* a single hop is in v1 (it is a one-table selector); anything that combines *whole hops* with boolean logic is deferred and slots in via the same node tree the v1 parser already builds.

### Sub-decisions

1. **Bracket form, comma separator.** Confirmed verbatim from Toni's preferred shape. Values are bare tokens; quoted strings exist as a future hook for values containing `,`, `]`, `|`, or whitespace. Today no production node has such a name; if one ever does, the quoted-string production handles it without a grammar change.
2. **Path separator `/`.** It is unreserved inside a query-string value, so `?path=a/b/c` does not need percent-encoding. (`/` is reserved only in the path component of a URL.) `>` and `→` were considered; `/` won because it matches Toni's example and the visual mental model is "a path."
3. **Boolean operators in the hop.** `|` is the within-key OR. There is no within-segment OR across keys in v1 (`type:task,status:open` is unambiguously AND). Across-hop boolean composition is the deferred case; see the v1 cut table above.
4. **`not` semantics — chosen prefix `!`** when shipped: `[type:task,status:!closed]` for "not closed", `![type:archived]` for "exclude this hop's branch." `!` was preferred over `-` because `-` is a legal character in node names. v1 rejects `!` at parse time so the syntax is reserved for the future implementation.
5. **Id segments.** `[id:42]` is the canonical form. The `<#42>` syntax from Toni's original capture is not adopted because it requires URL-encoding `<` and `>` and breaks the bracket-uniform shape. Multiple ids work for free: `[id:42|43|44]`. **Open question: should `<#42>` be accepted as parser sugar?** Recommendation: no for v1, add later if Toni misses it.
6. **Empty segment `[]`.** Means "any node linked to the previous hop." This is the clean expression of *"all neighbours"* and is the trailing-hop idiom for raw exploration.
7. **Reserved characters and URL-encoding.** Inside the `path=` value: `[`, `]`, `:`, `,`, `|`, `/` are all legal in a query-string value and need no encoding under RFC 3986. `?`, `#`, `&`, `+`, `=`, and space MUST be percent-encoded by the client. The parser receives the already-decoded string from ASP.NET binding, so the parser itself sees raw characters.

### Reasoning

Recursive descent is the right shape for a grammar this small: maybe 80–150 lines of straight-line code, no parser-generator dependency, easy to instrument with position-aware error messages, and trivial to extend (the deferred operators map to new productions, not a rewrite). Regex-and-split was considered and rejected; it cannot cleanly reject malformed input or report position, and it does not extend to nested boolean grouping.

### Alternatives considered

- **Path-style URI (`GET /api/query/<segments>`)** — rejected for v1 per Toni's binding decision (option B); kept as a documented future-work sugar layer that compiles down to the same parser internally.
- **JSON-encoded query parameter** — most expressive, ugliest in a URL bar, lowest discoverability. Not justified when the bracket grammar covers the v1 expressivity goal.
- **Parser combinators / PEG library** — overkill for a grammar this small; introduces a dependency for negligible win.

---

## B. SQL composition strategy

### Decision

Each segment compiles to a **filter on `Node` (joined to `NodeType` for type-name resolution)** producing an id-set. Hops 1..N are stitched together by joining `NodeLink` undirected — exactly the same Union-of-two-directions trick that `NodeService.cs:113-120` uses today, generalized over a list of hops. The terminal hop is the `LoadOperation<Node>` actually executed; intermediate hops are sub-queries materialized into the predicate of the next hop via `IN (subquery)`.

Conceptually, a 3-hop path compiles to a query of this shape (illustrative SQL only — implementation is a `LoadOperation<Node>` chain):

```
SELECT n.* FROM Node n
JOIN NodeType t ON n.TypeId = t.Id
WHERE <terminal-hop predicate on n,t>
  AND n.Id IN (
    SELECT n2.Id FROM Node n2
    JOIN NodeLink l2 ON (l2.SourceId = n2.Id OR l2.TargetId = n2.Id)
    WHERE <hop2 predicate on n2>
      AND (CASE WHEN l2.SourceId = n2.Id THEN l2.TargetId ELSE l2.SourceId END) IN (
        SELECT n1.Id FROM Node n1 ... WHERE <hop1 predicate>
      )
  )
```

The Union-direction trick stays inside the hop join, just as it does in the single-hop filter today. Each hop independently re-applies the wildcard / `IN`-vs-`LIKE` switch for `name` and `status` from `GenerateFilter`.

### Lateral-join dependency

**The v1 design does not require lateral joins.** The Union-on-`NodeLink` pattern at `NodeService.cs:116-118` already composes for multi-hop because each hop's id set is materialized as an `IN (subquery)` predicate on the next, not as a row-level lateral. Ocelot task #6 (`Support Lateral Joins`) is **not a blocker** for this feature.

Where lateral joins *would* help is performance: replacing `IN (subquery)` with a lateral correlated join would let Postgres prune earlier in plans with selective leaves. That is a v2 optimization, not a v1 requirement. The design surfaces this so John does not delay shipping for Ocelot #6 and Toni knows the perf headroom is real but later.

### Sub-decisions

1. **Per-hop filter compilation.** Refactor `NodeService.GenerateFilter` so its body becomes `GenerateHopFilter(HopFilter hop)` operating on a single hop's predicates. The old call site (the existing `NodeFilter`) keeps working by constructing one `HopFilter` from the existing `NodeFilter` fields. The new path endpoint calls `GenerateHopFilter` once per parsed segment.
2. **Hop chaining.** A new helper `ComposeHops(hops, database)` walks the parsed hop list left-to-right, each producing a `LoadOperation<Node>` whose predicate is `n.Id IN (linkSubquery(prevHop))`. The terminal hop is *not* materialized as a subquery — it is the `LoadOperation<Node>` returned to `AsyncPageResponseWriter<NodeDetails>`.
3. **Paging and sort.** `count`, `continue`, `sort`, `descending` are top-level query parameters, applied to the terminal hop's `LoadOperation<Node>` via `ApplyFilter(filter, mapper)` exactly as today. Sort keys remain the four `NodeMapper` keys (`id`, `type`, `name`, `status`); attempting to sort by anything else throws `KeyNotFoundException` and surfaces as 400, identical to current behaviour.
4. **`total` count semantics.** Computed in the same SQL statement as the page rows via a `COUNT(*) OVER ()` window function (Ocelot 0.18 `ExecutePagedAsync`). No second query is issued; the database computes the total count as a redundant column on every returned row, and Ocelot resolves it from the first row received. This replaces the earlier two-query pattern (page + separate `SELECT COUNT(*)`) and makes a `?nototal=true` opt-out unnecessary — the windowed count adds negligible overhead compared to the page query itself, so the perf gain that motivated the flag no longer exists. The `?nototal=true` parameter was shipped briefly and has been removed.
5. **Wildcard semantics.** Inherited from the existing filter: any `name` or `status` value containing `%` or `_` flips that key's predicate to OR-chained `LIKE`. This applies *per hop independently*.

### Alternatives considered

- **CTE-per-hop (`WITH hop1 AS (...), hop2 AS (...) ...`)** — equivalently expressive, but Ocelot's CTE support is not as mature as its subquery-`IN` support, and the subquery shape already exists in the codebase. Reject.
- **One giant N-way join with no nested subqueries** — the planner can sometimes do better when hops are flattened, but at the cost of being much harder to compose programmatically and much harder to extend to deferred boolean operators (which need set-algebra primitives like `UNION`).
- **Server-side recursive CTE** for unbounded depth — a different feature (transitive closure / ancestors-of). Out of scope here; the path query has a *known* depth.

---

## C. CancellationToken plumbing

### Decision

Thread `CancellationToken` from controller to SQL. The chain is:

```
HTTP request
  -> NodeController.PathQuery(NodePathFilter, CancellationToken ct)   [auto-bound from HttpContext.RequestAborted]
       -> INodeService.ListPagedByPath(NodePathFilter, CancellationToken ct)
            -> parser produces hop tree (synchronous, no IO, ct unused)
            -> ComposeHops produces LoadOperation<Node> (no IO, ct unused)
            -> AsyncPageResponseWriter<NodeDetails> constructor receives ct
                 -> at write time: operation.ExecuteEntitiesAsync(ct)
                                   countDelegate(ct) -> Load<Node>(DB.Count()).Where(...).ExecuteScalarAsync<long>(ct)
```

### Pooshit availability check

Before John starts: **verify which of `ExecuteEntitiesAsync`, `ExecuteEntityAsync`, `ExecuteScalarAsync<T>`, and `ExecuteAsync` accept a `CancellationToken` overload in the version of `Pooshit.Ocelot` currently referenced.** This is a one-grep job in the restored `Pooshit.Ocelot` assembly metadata.

Two paths from that check:

1. **All execution methods accept a token.** Plumb it through. Done.
2. **Some do not** (most likely the older `AsyncPageResponseWriter<T>` count delegate path). Two-step mitigation:
   - **v1 ship:** plumb the token to whatever methods accept it (the entity-streaming path is the priority; long count queries are the secondary concern). Document in the design doc and a TODO that the missing overloads are an upstream Pooshit gap, not a DiVoid bug.
   - **Follow-up:** open a task on Pooshit to add token overloads. Not a blocker for the feature.

### What cancellation actually means

- **Client disconnects:** Kestrel triggers `HttpContext.RequestAborted`, which is the `ct` we received. The SQL execution call observes it and aborts.
- **Request timeout (Kestrel default or operator override):** same mechanism, same path.
- **Manual cancellation in tests:** `WebApplicationFactory<Program>` integration tests can pass a `CancellationToken` and assert it propagates, using a deliberately slow query (e.g., a self-join over a synthetic large dataset).

### Reasoning

Toni explicitly named cancellation as the soft safety net replacing hard depth/breadth caps. Without working cancellation the soft cap fails open and a runaway query keeps a connection saturated until the database itself decides to give up.

### Alternatives considered

- **Hard depth/breadth caps as primary defence** — explicitly rejected by Toni. Trust the operator.
- **Server-side query timeouts at the SQL layer** (`SET statement_timeout`) — orthogonal and complementary; can be added as a separate hardening ticket. Does not replace `CancellationToken` because it does not respond to client disconnect, only to wall-clock duration.

---

## D. Error handling

### Decision

The new endpoint reuses the existing `Pooshit.AspNetCore.Services` error pipeline. Three error classes are spec-relevant:

| Failure | HTTP status | `code` | `text` |
|---|---|---|---|
| Path is unparseable (syntax error) | 400 | `badparameter` | `"Path query syntax error at column N: <reason>"` |
| Path references unsupported key (e.g., `[foo:bar]`) | 400 | `badparameter` | `"Unsupported key 'foo' in path segment N (allowed: id, type, name, status)"` |
| Path uses a deferred operator in v1 (e.g., `!`) | 400 | `badparameter` | `"Operator '!' is reserved and not yet supported"` |
| Path resolves to no nodes at one or more hops | 200 | — | Empty `result`, `total: 0`. **Not an error.** |
| Sort key not in mapper (`?sort=foo`) | 400 | `badparameter` | (Existing `KeyNotFoundException` path) |
| `count > 500` | 400 | (existing clamp behaviour — currently silently capped to 500) | — |
| Cancellation (client disconnect / timeout) | Match the existing pipeline. ASP.NET Core typically surfaces this as no response (connection torn down) rather than a status code. **Investigate during implementation:** if `OperationCanceledException` reaches the middleware, confirm it produces no spurious 500 in the logs. |

### Sub-decisions

- **Empty intermediate hop is not an error.** Toni's framing — *"if multiple paths match the same criteria, that is a valid result set"* — extends naturally to the empty case. A path that produces zero terminal nodes is no different from a `?type=task&status=nonexistent` query that returns nothing today.
- **Position-aware parser errors.** The recursive-descent parser carries the input string and a current column index; every parse error includes the column. Cheap to implement and dramatically better DX.
- **Aligned with `IErrorHandler<T>` (PR #11).** The new path-parser error is a parameter validation error, which already has a typed handler. No new handler type needed.

### Reasoning

The error model on this endpoint is parameter-validation-heavy (a query language is one big input parameter). Reusing the existing `badparameter` code keeps clients from special-casing path errors. Resolution-time emptiness as a 200 result aligns with Toni's "names are not unique, multiple matches is fine" principle and with how every other DiVoid filter behaves.

### Alternatives considered

- **422 Unprocessable Entity for parser errors** — RFC 9110 leans 400 here because the request itself is malformed (the query parameter cannot be interpreted), not because it is well-formed-but-rejected by business rules. 400 wins.
- **499 Client Closed Request for cancellation** — non-standard (nginx-ism). Not part of the existing convention. Skip.

---

## E. Out of scope (explicitly)

- **Caching of resolved paths.** The SQL query plan plus the database's own page cache is the cache. Adding an application-layer cache here would invalidate on every node create/link/unlink and yield negligible win for the expected query volume.
- **Aggregation operators** — no `count(*)`, no `sum`, no `group by`. The result is a flat node list. Aggregation is a separate feature that, if it ever lands, deserves its own endpoint or response shape.
- **Mutating queries via path** — no `DELETE /api/nodes?path=...`, no `POST /api/nodes/path/.../link`. Read-only.
- **Cross-DB / federated paths** — single graph, single Pooshit.Ocelot connection.
- **Hard depth/breadth caps** — replaced by `CancellationToken` plus Kestrel timeouts.
- **Path-as-URI sugar (`GET /api/query/...`)** — explicitly future work per Toni's binding decision; the v1 parser is structured so the sugar layer can produce the same hop tree without a second parser implementation.

---

## Schema deltas

**None.** This is a read-only feature that reuses the existing `Node`, `NodeLink`, and `NodeType` tables and indices. The composite `[Index("node")]` on `Node.TypeId` + `Node.Name` already supports the per-hop filtering shape. `NodeLink` is heavily referenced in `IN`-subqueries on both `SourceId` and `TargetId`; if those columns lack individual indices, that is a pre-existing performance concern unrelated to this feature, but worth a one-line check during implementation.

## Migration / rollout plan

1. **Additive endpoint.** No existing endpoint changes shape, body, or response. The existing `?linkedto=` filter continues to be the recommended one-hop tool.
2. **No feature flag needed.** The new endpoint is reachable only when a client sends `?path=`; clients that never send it see no behavioural change.
3. **API reference (graph node 8) updated** as part of the John-and-Jenny implementation chain, after the design lands.
4. **Onboarding doc (graph node 9)** — *do not* rewrite the multi-round-trip examples to use path queries yet; the path query is advanced mode. Add a sidebar pointing to it.

## Open questions for Toni

1. **Should `<#42>` be accepted as parser sugar for `[id:42]`** to honour the original capture, or is the unified bracket form sufficient? Recommendation: bracket-only in v1.
2. **Wildcard within `type` and `id`?** Today `name` and `status` flip to `LIKE` on `%`/`_`; `type` does not (it joins through `NodeType`), and `id` is numeric. Recommendation: keep current behaviour, do not add wildcards on `type`/`id` in v1.
3. **Token-overload availability in Pooshit.Ocelot** — needs a one-grep verification before implementation begins. Not a design unknown, just a fact-check that gates *how complete* the cancellation propagation can be in v1.

## Implementation guidance for John

Build phases, in order. Each phase is independently reviewable; the chain produces no broken intermediate state.

1. **Confirm the Pooshit cancellation overload surface.** One grep over the restored `Pooshit.Ocelot` assemblies for `CancellationToken` on the `Execute*` methods. If gaps exist, capture which ones in a follow-up note.
2. **Introduce `NodePathFilter`** as a sibling to `NodeFilter`, carrying the raw `path` string plus the inherited `ListFilter` paging/sort/fields. Bind it from the query string on the same `[HttpGet]` action that already exists, but on a sibling action distinguished by the presence of `path`.
3. **Build the parser** as a self-contained class with these public methods (described, not coded): *parse a path string into an ordered list of `HopFilter` objects, throw a parameter-validation exception with column information on syntax errors.* The internal node tree must already include placeholders for `Negated` flags on predicates and segments and an envelope for `OrNode`/`AndNode` peers, even if v1 rejects them at parse time. Unit-test the parser exhaustively against happy and adversarial inputs before any database integration.
4. **Refactor `NodeService.GenerateFilter` to `GenerateHopFilter(HopFilter)`** preserving the existing `NodeFilter` call site by adapting it (`NodeFilter` builds one `HopFilter`). This refactor must be a no-op on the existing endpoint's behaviour — verified by the existing test suite.
5. **Implement `ComposeHops`** as the helper that chains `HopFilter` objects into a single `LoadOperation<Node>` using the same Union-on-`NodeLink` shape as the current `linkedto` implementation. Build incrementally: 1 hop (recovers the existing single-hop semantics), 2 hops, N hops.
6. **Wire the new controller action and service method.** `INodeService.ListPagedByPath(NodePathFilter, CancellationToken)` returning `AsyncPageResponseWriter<NodeDetails>`, mirroring `ListPaged`.
7. **Plumb `CancellationToken`** end-to-end. Where Pooshit lacks an overload, inject `ct.ThrowIfCancellationRequested()` at the boundaries we control (start of execute, between hops if composition does any work) so a disconnect aborts before the next stage.
8. **HTTP-level integration tests** via the `WebApplicationFactory<Program>` setup PR #11 introduced. Required scenarios:
   - 1-hop path equivalent to existing `?type=&status=&linkedto=` (parity test).
   - 2-hop and 3-hop happy paths against a seeded graph.
   - Empty intermediate hop returns 200 with empty result.
   - Syntactic errors return 400 with column information.
   - Deferred operators (`!`, parenthesized boolean groups) return 400 with the *"reserved"* message.
   - Wildcard `name`/`status` per hop behaves identically to the existing single-hop filter.
   - Sort, paging, and `fields` apply to the terminal hop only.
   - A long-running query is cancelled by client disconnect (slow seeded data + a token-cancelled HTTP client).
9. **API reference update (graph node 8).** Document the new `path` parameter, the grammar with examples, the v1 operator cut, and the deferred-operator error surface.
10. **Future-work tickets to file (do not implement now):**
    - Boolean operator extension (`or`, `and`, `not` across segments).
    - `<#id>` parser sugar.
    - `GET /api/query/<segments>` URI-style sugar layer.
    - Pooshit `CancellationToken` overload gaps, if any were found in step 1.
    - Lateral-join optimization (Ocelot task #6) — performance, not correctness.
