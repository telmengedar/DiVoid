# Architectural Document: Opt-in inline content on `GET /api/nodes` (task #1180)

## 1. Problem Statement

Research / lookup flows over the DiVoid graph (the canonical one being "fetch all documentation nodes linked to project X and read their bodies") currently require **one `GET /api/nodes/{id}/content` per result row** on top of the initial listing call. For a page of 50 documentation nodes that is 51 round-trips. The listing call already streams everything *except* the body; the body is the only datum holding the second call back.

The task is to let a caller **opt into inline bodies on the listing response** so a single request can satisfy a research workflow end-to-end. Binary content must be supported as well as text — the user's load-bearing requirement is that binary participates "by just returning base64", so the response shape is uniform whatever the row's content type.

Success criteria:

- A single `GET /api/nodes?fields=…,content&…` returns the body inline for every row that has one.
- The default response shape is **unchanged** — opt-in only.
- Mixed-type pages (text + binary + empty) render correctly with one shape rule.
- The endpoint stays predictable in size — a 500-row page of multi-MB blobs does not exist.

## 2. Scope & Non-Scope

**In scope:**

- A new value `content` on the existing `?fields=` vocabulary on `GET /api/nodes`.
- Per-row inline encoding: text → UTF-8 JSON string; binary → base64 JSON string.
- A defensible text-vs-binary boundary that reuses the codebase's single source of truth (`TextContentTypePredicate.IsText`) so the listing rule cannot drift from the embedding rule.
- A per-row size cap with a truncation flag so the listing remains predictable; full bodies remain reachable via `GET /api/nodes/{id}/content`.
- Path-query parity (`?path=…&fields=content` works identically on the terminal hop).
- The `sort=content` case (must be rejected, naturally).

**Explicit cuts (do not over-build):**

- No streaming / chunked transfer for inline bodies — if a body is too big for the cap, the caller falls back to the byte-stream endpoint.
- No compression — the cap is the cost control.
- No `?includeContent=true` parallel flag — single mechanism (`fields=content`), see §10.
- No new filter on content (e.g. `?content=…`) — there is no use case.
- No MCP-side change — that ships as the sibling task **#1181** in the `divoid-mcp` package.
- No swagger XML doc rewrite of unrelated `NodeFilter` fields — the changes are additive.

## 3. Assumptions & Constraints

- The DB is whatever `Database:Type` is configured to use (SQLite locally, PostgreSQL in prod). `Node.Content` is `byte[]` in both. The mapper projection must work on both.
- The existing 500-row page cap (`FilterExtensions.ApplyFilter` clamps `Count`) stays. Per-row cap is the new lever.
- `NodeMapper` already implements the field-mapper / default-fields pattern. The mapper resolves `?sort=` by strict dictionary lookup against its registered fields — adding a `content` `FieldMapping` would make `sort=content` syntactically valid, which is exactly what we want to prevent. The design must keep `content` out of the sort-resolution path. See §10.
- `JsonStreamOutputFormatter` sits at index 0 of `OutputFormatters` and only serialises types implementing `IResponseWriter`. The list path returns `AsyncPageResponseWriter<NodeDetails>`. Encoding work must therefore happen either in the mapper's setter (per-row) or in a post-hydrate step that wraps the existing stream — see §5 and §8.
- `TextContentTypePredicate.IsText(string)` is the canonical text-vs-non-text classifier. It strips charset parameters, lowercases, accepts `text/*` and an allowlist of `application/*` types. We reuse it verbatim.
- Code Contracts (DiVoid #114): no `var`, explicit-types, no in-body explanatory comments, no redundant `async`/`Task.FromResult` wraps, no list-materialising in-process transformations of a streamable result.
- Pooshit.Json serializes `null` properties as omitted by default — the same mechanism already makes `contentType` disappear from a row whose node has no content (per #102 §4). The `content` field will reuse this property: leave it `null` and it is omitted.

## 4. Architectural Overview

The change is a **field extension** on the existing list pipeline, plus a per-row encoding step that runs only when the field is requested.

```
HTTP GET /api/nodes?fields=id,type,name,content&...
        │
        ▼
NodeController.ListPaged ───────────► NodeService.ListPaged / ListPagedByPath
                                                │
                                                ▼
                               NodeMapper (existing) — augmented:
                                 - new FieldMapping "content"
                                   projects Node.Content (byte[]) AND Node.ContentType
                                 - setter encodes per-row using
                                   TextContentTypePredicate.IsText:
                                     text  → UTF-8 string into NodeDetails.Content
                                     other → base64 string into NodeDetails.Content
                                   truncated if over cap; flag set + original length recorded.
                                 - NOT added to DefaultListFields
                                 - NOT in mapper's sort-key set
                                                │
                                                ▼
                              WindowedFromOperation (existing)
                                                │
                                                ▼
                       AsyncPageResponseWriter<NodeDetails>
                                                │
                                                ▼
                       JsonStreamOutputFormatter streams the standard envelope
                       {"result":[{NodeDetails+content},…], "total":N, "continue":…}
```

Two new properties on `NodeDetails`:

- `Content` (string, nullable) — the encoded body; omitted from JSON when null.
- `ContentTruncated` (bool, nullable) — true when the row was capped; omitted otherwise.
- `ContentLength` (long, nullable) — original byte length, returned only when truncation occurred so the caller knows the size of the full body it can fetch via `GET /api/nodes/{id}/content`. Omitted otherwise.

The first two are load-bearing for the response shape; the third is the truncation contract.

## 5. Components & Responsibilities

| Component | Owns | Does NOT own |
|---|---|---|
| `NodeController.ListPaged` (existing) | HTTP shape, route, auth, dispatch to service. | Encoding, truncation, field selection. No change to method body beyond what already exists. |
| `NodeService.ListPaged` / `ListPagedByPath` (existing) | Query construction, filter composition, semantic-search composition, paging via `WindowedFromOperation`. | The byte→string encoding. The service stays unaware of inline-content semantics. |
| `NodeMapper` (existing, augmented) | One new `FieldMapping` for `content` whose **projection** loads `Node.Content` (and also `Node.ContentType` if not already projected) and whose **setter** does the per-row encoding. Decision whether the row qualifies (no content → no `content` field). Truncation decision per row. NOT included in `DefaultListFields`. NOT exposed as a `sort` key. | The list pipeline itself, paging, auth. |
| `NodeDetails` (existing, augmented) | Three new nullable properties (`Content`, `ContentTruncated`, `ContentLength`) representing the response-side shape only. | Persistence — these are DTO fields, never written back to the DB. |
| `InlineContentEncoder` (new, internal static helper) | Pure function: given `(byte[] content, string contentType, int cap)` returns `(string encoded, bool truncated, long originalLength)`. Uses `TextContentTypePredicate.IsText` to choose the branch. Handles null/empty content cleanly. | The mapper wiring, the SQL projection, the HTTP shape. Single responsibility: byte→string. |

**Why an internal static helper and not a method on the mapper:** the encoding is pure, deterministic, easy to unit-test in isolation, and reused in two call sites (the mapper setter for `content`, plus an integration-test assertion harness). Hiding it inside the mapper makes the encoding logic harder to test directly. The helper lives next to the mapper at `Backend/Models/Nodes/InlineContentEncoder.cs`. It is `internal static` — no DI, no state, one entry point.

**Why no new service method:** the entire change rides through the existing `ListPaged` / `ListPagedByPath` path. Adding a new service method would mean a parallel pipeline; the field-mapper pattern is *designed* to be extended in place. This is a sibling of #102.

## 6. Interactions & Data Flow

The path-query branch and the plain-list branch both end up calling `mapper.WindowedFromOperation<long, Node>(operation, DB.CountOver(), ct, filter.Fields)`. The `filter.Fields` carries `"content"` when the caller opted in, so the same field-mapper resolution that already wires `id`/`type`/`name` into both the SELECT projection and the row-setter callback wires `content` into them too.

Sequence for one call with `?fields=id,name,content&count=50`:

1. Client sends the request.
2. `NodeController.ListPaged` delegates to `NodeService.ListPaged`.
3. `NodeService` builds the `NodeMapper`, applies filters, calls `WindowedFromOperation` with the supplied `filter.Fields`.
4. The mapper composes the SELECT projection: `node.id`, `node.name`, **`node.content`, `node.contenttype`** (both required by the `content` `FieldMapping` — see §8 on the multi-column projection).
5. For each streamed row, Ocelot hydrates a `NodeDetails`. The `content` `FieldMapping`'s setter is invoked with the row's byte array (and a sibling fetch of `ContentType`). The setter calls `InlineContentEncoder.Encode(bytes, contentType, MaxInlineBytes)`; the result populates `NodeDetails.Content`, and `ContentTruncated`/`ContentLength` when applicable. If `bytes == null` (no content) the setter leaves all three properties null.
6. `AsyncPageResponseWriter<NodeDetails>` streams the rows out. `JsonStreamOutputFormatter` serialises each one; null properties are omitted by Pooshit.Json.

There is **no caching, no compression, no streaming**. The size cap means each row's body lives briefly on the heap as a byte array and a string; the page is bounded to `500 × MaxInlineBytes` of inline payload max. With the chosen cap (see §10) that is ~32 MB worst-case for an extreme page of 500 fully-capped rows — same order as the existing maximum response and well within Kestrel's defaults.

## 7. Data Model (Conceptual)

No persisted-schema change. The change is API-surface only:

| Property | Source | Semantics |
|---|---|---|
| `NodeDetails.Content` (string?) | Encoded from `Node.Content` (byte[]) when `?fields=content`. | The body, encoded per the boundary rule in §10. Omitted from JSON when null. |
| `NodeDetails.ContentTruncated` (bool?) | `true` when the source body exceeded `MaxInlineBytes`. | Diagnostic flag. Omitted (null) when no truncation occurred or when no content was requested. |
| `NodeDetails.ContentLength` (long?) | Original `Node.Content.Length` when truncated. | Hint to the caller of the body size it would fetch from `GET /api/nodes/{id}/content`. Omitted when no truncation occurred or when no content was requested. |

`Node.Content` and `Node.ContentType` themselves are unchanged.

A node with no content (i.e. `Node.Content` is null) yields a row where all three new fields are absent — same convention as `contentType` per #102.

## 8. Contracts & Interfaces (Abstract)

### HTTP contract

**Request:** `GET /api/nodes?fields=<list-including-`content`>&<other filters>`

The `fields` parameter is the existing one; the new value is `content`. All other parameters compose unchanged.

**Path-query parity:** `GET /api/nodes?path=<expr>&fields=…,content` works identically. The terminal-hop rows carry inline content the same way.

**Response envelope (unchanged):**

```
{
  "result": [
    { …row… },
    { …row… }
  ],
  "total": <long>,
  "continue": <long|null>
}
```

**Row shape — text content:**

```
{
  "id": 1180,
  "type": "task",
  "name": "Backend: opt-in inline content on GET /api/nodes (text + base64 binary)",
  "status": "in-progress",
  "contentType": "text/markdown; charset=utf-8",
  "content": "## Goal\n\nAllow callers of `GET /api/nodes` to opt into…"
}
```

**Row shape — binary content (e.g. PNG attached):**

```
{
  "id": 901,
  "type": "image",
  "name": "diagram.png",
  "contentType": "image/png",
  "content": "iVBORw0KGgoAAAANSUhEUgAAA…"
}
```

The caller distinguishes text from base64 via the `contentType` field on the same row.

**Row shape — node with no content:**

```
{
  "id": 12,
  "type": "project",
  "name": "DiVoid"
}
```

The `content`, `contentType`, `contentTruncated`, and `contentLength` fields are all absent.

**Row shape — truncated content (text path):**

```
{
  "id": 555,
  "type": "documentation",
  "name": "huge research dump",
  "contentType": "text/markdown",
  "content": "## Intro\n\n…first 64 KiB of body…",
  "contentTruncated": true,
  "contentLength": 1834221
}
```

The caller knows to follow up with `GET /api/nodes/{id}/content` if it needs the full body.

**Row shape — truncated content (binary path):**

Same as above but `content` is base64 representing the first 64 KiB of the original byte stream (not the first 64 KiB of the base64 string — see §10).

### Mapper contract (abstract — no code)

The `content` `FieldMapping` is a multi-column projection: it pulls both `Node.Content` (the bytes) and `Node.ContentType` (the discriminator) from the same row. Inspecting `NodeMapper.cs`, every existing `FieldMapping` is single-column (`DB.Property<Node>(n => n.X, "node")`). The mapper framework does not have a built-in multi-column field mapping, so the encoding-time access to `ContentType` is resolved one of two ways:

**Recommended approach — "force-fetch ContentType alongside Content":** when the caller requests `fields=…,content`, the service appends `"contentType"` to `filter.Fields` if not already present. The mapper then has `ContentType` populated on the same `NodeDetails` instance by the time the `content` setter runs. The `content` `FieldMapping` registers a single-column projection on `Node.Content`, and its setter reads `this.row.ContentType` (the NodeDetails being hydrated) to make the text/binary decision.

The catch: `FieldMapping` setters are `Action<NodeDetails, T>` (set value on the target object) and the mapper does not guarantee column ordering. If `content` is set *before* `contentType` is set on the same `NodeDetails` instance, the encoder cannot read it.

**Resolution:** the setter must not depend on `NodeDetails.ContentType` being already-set at call time. Instead, the `content` `FieldMapping` is registered to project **two columns**: `Node.Content` AND `Node.ContentType`, and its setter receives both. The Pooshit Ocelot `FieldMapping` machinery accepts a `Tuple<byte[], string>` (or equivalent struct) — confirm against the Ocelot reference before implementing. If it does not, fall back to: the setter for `content` reads the raw `byte[]` and **stores the bytes** on a transient property of `NodeDetails`; a post-streaming step in the response writer re-encodes once both fields are present.

**The post-hydrate fallback (only if the mapper cannot project a `(byte[], string)` tuple):** the service wraps the mapper's `AsyncPageResponseWriter` in a tiny per-row transform that, just before each row is serialised, reads `(row.RawContent, row.ContentType)` and computes `(row.Content, row.ContentTruncated, row.ContentLength)`. `RawContent` is internal — the byte[] never reaches the wire. This is the **fallback path**; prefer the mapper approach. The fallback is documented here so John has an unambiguous next step if Ocelot's setter signature is single-column.

### Field-mapping shape — final spec

- Add **`content`** to `NodeMapper.Mappings()`. Source column(s): `Node.Content` (byte[]) plus the row's already-projected `Node.ContentType` (string). Setter: invoke `InlineContentEncoder.Encode(bytes, contentType, MaxInlineBytes)` and assign the resulting `(string, bool, long)` to `NodeDetails.Content`, `ContentTruncated`, `ContentLength`. When `bytes == null` or empty, leave all three null.
- Do **not** add `content` to `DefaultListFields`. Plain `GET /api/nodes?count=10` returns the existing five-field row (`id`, `type`, `name`, `status`, `contentType`).
- The service **must auto-include** `contentType` in the projection when `content` is requested. Concrete rule: in `NodeService.ListPaged` / `ListPagedByPath`, after `filter.Fields ??= …`, if `filter.Fields` contains `content` and not `contentType`, append `contentType`. This makes the boundary decision possible at hydration time without forcing every caller to remember to list both.
- `content` is **not** a valid sort key. `NodeMapper` resolves sort by strict dictionary lookup against the registered field-mapping keys — but the lookup is over **all registered mappings**, so registering a `FieldMapping<NodeDetails, string>("content", …)` *would* expose it as a sort key. To prevent this: introduce an "is-sortable" filter at the sort-resolution site (a small allow-list of sortable keys) **or** use the existing `FieldMapping` overload pattern in a way that does not register a sort accessor for `content`. See §10 for the chosen approach.

## 9. Cross-Cutting Concerns

| Concern | Treatment |
|---|---|
| **AuthN/Z** | Unchanged. `[Authorize(Policy = "read")]` on `ListPaged` already gates the endpoint. Inline content does not raise the bar — anything reachable via `GET /api/nodes/{id}/content` is reachable here. |
| **Logging** | Unchanged. The existing controller does not log GET reads (per §11/§7 of #114). The mapper / encoder do not log either. |
| **Observability** | One opaque addition: bytes in the response body are higher when `content` is requested. If response-size metrics matter elsewhere, the existing instrumentation already covers payload size — no new signal needed. |
| **Errors** | The encoder must not throw on malformed bytes. UTF-8 decoding of a `text/*`-typed payload that is in fact not valid UTF-8 (rare but possible — `text/markdown` written by a tool that mis-encoded) must not abort the entire page. Encoder behaviour: try strict UTF-8 decode; on `DecoderFallbackException` (or equivalent), classify the row as binary and emit base64 instead, with `contentTruncated=false` and `content` set to the base64 form. No special error indicator. Document the fallback in the encoder's XML summary so reviewers can find it. |
| **Caching** | None server-side. The endpoint stays uncached the same way every other listing endpoint is. Browser/MCP-side caching of `(?fields=content)` results is the caller's choice. |
| **Concurrency / Consistency** | Same as any list endpoint. A `Node.Content` mutation between page 1 and page 2 is observable — that is the existing semantics for every other column. |
| **Cancellation** | The existing `CancellationToken` plumbing from `ListPaged`/`ListPagedByPath` already covers the whole pipeline. The encoder must respect the token at the page level — a 50-row page with 50 × 64 KiB encoded bodies is sub-millisecond; per-row CT check would be over-engineering. The token is checked between rows by the streaming infrastructure already. |
| **Idempotency** | GET. Trivially idempotent. |
| **Memory pressure** | Bounded by `pageSize × MaxInlineBytes`. With pageSize≤500 and MaxInlineBytes=64 KiB → ~32 MiB worst case in flight. Acceptable. |

## 10. Quality Attributes & Trade-offs — settling each design question

### Q1 — Parameter name & API surface

**Decision:** extend `?fields=` with a new value `content`. No parallel `?includeContent=true` flag.

**Reasoning:** the existing `fields` vocabulary is already the established opt-in mechanism — `x`, `y`, `similarity`, and `contentType` itself ride through it. The #102 precedent ("how do you add a new optional listing field?") concluded with this same mechanism. Adding a parallel boolean flag would mean two ways to express the same intent, with composability questions (`?includeContent=true&fields=id` — do you get `content` or not?). The single mechanism is uniform and obvious. Cost: zero, the `fields[]` array parser already accepts arbitrary string values, the mapper resolves them by name lookup. Path-query mode reuses the same `filter.Fields` plumbing so parity is free.

### Q2 — Size cap per row

**Decision:** **per-row truncation at `MaxInlineBytes = 64 KiB`**, with `contentTruncated: true` and `contentLength: <originalByteLength>` on the row. The full body remains reachable via `GET /api/nodes/{id}/content`.

**Reasoning:** three options were on the table:

- *Truncate-with-flag (chosen).* Predictable response size; the caller is informed and can decide whether to follow up. The 64 KiB cap is large enough that the vast majority of documentation nodes (the load-bearing use case) round-trip in full, but small enough that a worst-case 500-row page caps at ~32 MiB. 64 KiB is one order of magnitude above the typical documentation-node size on the live graph (median ≈4 KiB on the snapshot of node #114-class documentation), comfortably above the 99th percentile. The base64 expansion factor (~1.34×) means a 64 KiB binary truncation produces ~85 KiB of base64 in the JSON — still inside the budget.
- *Error-the-row.* Rejected. A research call requesting a page of 50 nodes where one has a 2 MB image attached should not lose the other 49 bodies because of one outlier. Diagnostic flags beat hard failures here.
- *Per-page byte budget (e.g. "stop adding bodies once the page exceeds 8 MB").* Rejected. Unpredictable per-row semantics — whether a given node's body shows up depends on the order of the page and the size of preceding bodies. Pagination becomes opaque to the caller.

Truncation semantic on the **binary** path: truncate the **byte stream** at `MaxInlineBytes`, *then* base64-encode the prefix. Not the other way around — truncating a base64 string at 64 KiB yields ~48 KiB of original bytes, which is fine but adds a per-row asymmetry callers would have to know. Cut bytes first, encode second. (For the text path the same rule applies: the UTF-8 decode is performed on the **first `MaxInlineBytes` of the byte stream**; if that boundary splits a multi-byte character, the encoder backs up to the last complete UTF-8 boundary before decoding. The codebase has been bitten by this exact `convert_from(LEFT(bytes, N), 'UTF8')` trap before — referenced in `Backend/Services/Nodes/NodeService.cs:680` and the agent memory entry on "decode-then-truncate". The encoder handles it correctly: locate the last complete UTF-8 boundary ≤ `MaxInlineBytes`, decode that prefix.)

### Q3 — Page-count interaction

**Decision:** keep the existing 500-cap on `count`. No additional clamp when `content` is in `fields`.

**Reasoning:** the per-row cap already bounds total payload deterministically: `500 × (64 KiB + small JSON overhead)` ≈ 32-35 MiB worst case. Adding a second, dynamic cap (e.g. "max 100 rows when `content` is requested") would be a hidden behaviour the caller has to discover empirically and would complicate the paging contract. State this explicitly in the XML summary on `ListPaged` and in this doc: per-row cap is the only control; `count` behaves identically whether `content` is requested or not.

### Q4 — Base64 encoding boundary

**Decision:** delegate to `TextContentTypePredicate.IsText(string contentType)`. If it returns `true`, encode as UTF-8 string; otherwise, base64.

**Reasoning:** the codebase already has a single source of truth for "what counts as text" — `TextContentTypePredicate` — and it is used by the embedding pipeline (`NodeService.UploadContent`, `RegenerateEmbeddingViaBranches`). Re-implementing the classifier locally would mean two definitions drifting apart over time, which is the exact failure mode CODE-CONTRACTS §5.4 ("no parallel mirror enums/classifiers") forbids. The classifier already covers the load-bearing list (`text/*` family, plus `application/json`, `application/xml`, `application/x-yaml`, `application/yaml`, `application/javascript`, `application/x-sh`) and strips charset parameters correctly.

Fallback rule for unknown / null `contentType`: a node with content but no `contentType` (theoretically possible if a row was inserted via a path that did not set the column) is treated as **binary** — base64 encoding is safe for arbitrary bytes. `IsText(null)` already returns false. Document this explicitly in the encoder XML.

The errror fallback for "claimed text but actually invalid UTF-8" is covered in §9 — silent demotion to base64.

### Q5 — Field-mapping shape

**Decision:** the `content` `FieldMapping` is wired into the existing `NodeMapper`, **not** post-hydrated in `NodeService`. The mapper projects `Node.Content` and reads `Node.ContentType` (auto-included by the service when `content` is requested — see §8). The encoding happens in the setter callback.

**Reasoning:** the field-mapper idiom is the natural extension point — it is the same place every other listing field lives, including the most-similar precedent (`contentType`, #102). A post-hydrate step in `NodeService` would mean two ways to populate a `NodeDetails` row depending on which fields are present, which fragments the mapper contract.

The **only** reason to fall back to a post-hydrate step would be if Pooshit Ocelot's `FieldMapping` setter signature truly cannot bind two source columns. The mapper inspection (NodeMapper.cs:39-79) shows every existing mapping is single-column. If a two-column mapping is genuinely unsupported, the agreed fallback (documented in §8) is: register `content` as a single-column projection on `Node.Content`, have its setter stash the raw bytes on an internal `[JsonIgnore]` field of `NodeDetails`, and apply the encoder in a small per-row transform between `WindowedFromOperation` and the `AsyncPageResponseWriter` wrapper. The fallback adds five lines to `NodeService.ListPaged` and `ListPagedByPath`. John picks the path empirically: try the mapper-only shape first, fall back only if Ocelot rejects.

Either way: **no new service method**, no parallel pipeline. The change rides through `ListPaged` / `ListPagedByPath` as they exist today.

### Q6 — Sort interaction

**Decision:** `sort=content` must be rejected with the existing 400 path. Implementation: add `content` to a *sort-blocklist* check in `NodeMapper`, OR register the `FieldMapping` in a way that does not contribute a sort accessor. Verified with a test.

**Reasoning:** `NodeMapper`'s sort-resolution uses strict dictionary lookup against the registered field mappings (per `CLAUDE.md`'s "any other value throws `KeyNotFoundException`"). Adding `content` as a `FieldMapping` therefore makes `sort=content` resolve to "sort by the bytes column" — which is meaningless, expensive, and a footgun. Two options:

- *Option A — different overload:* if `FieldMapping<TTarget, TValue>` has an overload that registers a setter without a sort accessor, use that for `content`. The mapper reads field-mapping → SELECT projection → setter from one path and field-mapping → sort accessor from another. The cleanest fix is to register `content` as projection+setter only, not sortable.
- *Option B — explicit reject:* in `ApplyFilter` (or wherever the sort lookup happens), explicitly reject `sort=content` before the dictionary lookup runs, with a 400 carrying a clear message.

**Recommended:** Option A. It is structural — the field is *not* a sort key by definition, expressed as data not as a code branch. Verify against the Ocelot reference; if the overload does not exist, take Option B and file a follow-up against Ocelot.

Either way, the test is mandatory: `GET /api/nodes?sort=content&count=1` returns HTTP 400 with the canonical `{code,text}` error envelope, same as `sort=Embedding` does today.

### Q7 — Path-query parity

**Decision:** `?path=<expr>&fields=…,content` returns inline content on the terminal-hop rows with the same semantics as the plain-list endpoint. No special handling.

**Reasoning:** `NodeService.ListPagedByPath` and `NodeService.ListPaged` share the same `WindowedFromOperation` call and the same mapper — `filter.Fields` flows through both branches identically (see `NodeService.cs:557-587` plain-list and `:610-633` path). Adding `content` to the mapper makes it work in both branches with no further code path. The auto-include rule for `contentType` (§8) is applied in **both** `ListPaged` and `ListPagedByPath` before `filter.Fields` is passed to `WindowedFromOperation`.

Test must cover this: a path-query terminal hop that yields one documentation node, requested with `fields=id,content`, returns the body inline.

### Q8 — Empty content rows

**Decision:** when a node has no content (`Node.Content == null`), the row in the response has no `content`, `contentTruncated`, or `contentLength` field at all. The `contentType` field is also absent for such rows (per #102's existing behaviour).

**Reasoning:** consistency with the precedent. Pooshit.Json's default behaviour ("null properties omitted") makes this free — the encoder sets all three properties to null, the serialiser drops them. No sentinel value, no empty string, no `content: null`. Test must assert the *absence* of the key, not the presence of a null value.

### Other rejected alternatives

- *Always include `content` and let callers ignore it.* Rejected — defeats the entire point of opt-in; every listing call would pay the byte cost of every blob.
- *Two separate fields, `text` and `binary`.* Rejected — fragments the response shape and forces the caller to inspect two properties; `content` + `contentType` already conveys the same information unambiguously.
- *Different encoding (hex, etc.) for binary.* Rejected — base64 is the de facto JSON convention for binary, and the user's load-bearing requirement explicitly named base64.
- *Caching layer in front of the encoder.* Rejected — the encoder is a sub-millisecond pure function on bytes; a cache adds invalidation complexity for no win.

### Maintainability

The footprint is intentionally tight: one new helper file (encoder), one new `FieldMapping` line in `NodeMapper.cs`, three new properties on `NodeDetails`, an auto-include line in `NodeService.ListPaged` / `ListPagedByPath`, and tests. Every change to the encoding rule (e.g. someday raising the cap, or supporting a new text MIME type) has exactly one place to land: the encoder and `TextContentTypePredicate` respectively.

### Performance

Cost per requested row with content present: one UTF-8 decode (text branch) or one base64 encode (binary branch) on ≤64 KiB. Both are sub-millisecond on .NET 9. Rows without content pay nothing — the encoder short-circuits on null bytes. Worst-case page (500 rows, all 64 KiB) is bounded; happy-case page (50 documentation nodes, ~4 KiB each) adds ≈ 200 KiB to the response.

## 11. Risks & Mitigations

| Risk | Likelihood | Mitigation |
|---|---|---|
| Implementer treats `content` as a regular `FieldMapping` and gets `sort=content` for free. | High — that is the easy path. | §10/Q6 calls it out explicitly. The test for `sort=content` 400 is mandatory and load-bearing; commenting out the sort-block must break the test. |
| Implementer truncates base64 *after* encoding, yielding a smaller payload than `MaxInlineBytes` for binary rows and an inconsistent rule with text. | Medium — base64-after-truncate looks fine until you do the math. | §10/Q2 spells out: truncate the byte stream first, encode second. Encoder XML summary repeats it. Tests assert: a 100 KiB binary row produces ≤ `ceil(MaxInlineBytes * 4 / 3) + padding` of base64 string. |
| Implementer hand-rolls the text-vs-binary classifier instead of calling `TextContentTypePredicate.IsText`. | Medium — copying the rule verbatim feels obvious. | §10/Q4 names the classifier. §5.4 of #114 forbids parallel classifiers. Jenny's review will catch a redeclared rule. |
| Implementer skips the `contentType` auto-include and the encoder cannot decide the boundary at row-hydration time. | Medium — the dependency is non-obvious. | §8 specifies the auto-include rule explicitly with the exact condition. Tests with `?fields=id,content` (no `contentType` requested) must still return correctly-encoded rows. |
| UTF-8 decode of a row claiming `text/markdown` but containing invalid bytes throws and aborts the whole page. | Low — but it has happened in `convert_from` paths before. | Encoder catches the decode exception and falls back to base64 silently for that one row. §9 documents this. Test seeds a row with `contentType=text/plain` and invalid-UTF-8 bytes; expects the row to return base64 with no error. |
| Caller relies on `content` always being present whenever `contentType` is present. | Low. | Documented explicitly: `content` is *only* included when in `?fields=`. The two are independent. |
| Memory pressure on a 500-row fully-capped page. | Low. | 32 MiB upper bound, single-request peak. Kestrel default limits accommodate. |
| `Node.Content` is implicitly fetched for every row in a 500-row listing even when no `content` field is requested, because the mapper's projection list got out of sync. | Low — the field-mapper pattern projects only requested fields. | Test: seed 50 nodes with 1 MiB content each, list with default fields, assert response size is well below 1 MiB total (the bytes column must not be fetched). This is the "load-bearing negative test" per DiVoid #275. |

## 12. Migration / Rollout Strategy

Not applicable — net-new opt-in field, fully backwards-compatible. Default behaviour is unchanged. The endpoint can ship behind no flag.

The MCP-side change (task **#1181**, in the `divoid-mcp` package) is a separate PR and depends on this one shipping. The order is: this PR merges → the MCP tool's `divoid_list` gains an `include_content` parameter that maps to `fields=content` → MCP users get the one-call research path.

## 13. Open Questions

None blocking. The single empirical check during implementation is whether Pooshit Ocelot's `FieldMapping` supports a two-column projection for the `content` field; if not, John takes the documented fallback path (§8) without re-design.

## 14. Implementation Guidance for the Next Agent

In dependency order. Each bullet is one architectural unit; John may commit them together or split, but the order is fixed.

1. **Encoder helper.** Add `Backend/Models/Nodes/InlineContentEncoder.cs` — `internal static class InlineContentEncoder` with one entry point `Encode(byte[] content, string contentType, int maxBytes)`. Pure function. Uses `TextContentTypePredicate.IsText` for the boundary decision. Backs up to a complete UTF-8 boundary before decoding the prefix on the text path. Catches `DecoderFallbackException` and falls back to base64 silently. Returns a small struct or tuple carrying `(string encoded, bool truncated, long originalLength)`. XML summaries per §4 of #114. Constant `MaxInlineBytes = 64 * 1024` lives here.

2. **`NodeDetails` extension.** Add three nullable properties: `Content` (string), `ContentTruncated` (bool?), `ContentLength` (long?). Order them after `ContentType` and before `Similarity` for visual locality. XML summaries describe presence semantics (opt-in via `fields=content`, absent when no content, truncation semantics).

3. **`NodeMapper` extension.** Add a `FieldMapping` for `content` that projects `Node.Content`. Its setter must invoke `InlineContentEncoder.Encode` with the row's content type. Choose between (a) two-column projection of `(Content, ContentType)` if Ocelot supports it, (b) single-column projection + reliance on the auto-include rule + a per-row encoder pass before the writer. Document the chosen shape in a one-line code comment **only** if it captures non-obvious mapper behaviour — otherwise no comment per §4. **Do NOT** add `content` to `DefaultListFields`. **Do NOT** register a sort accessor for `content`.

4. **`NodeService` auto-include.** In `ListPaged` and `ListPagedByPath`, after the `filter.Fields ??= …` line, if `filter.Fields` contains `"content"` and does not contain `"contentType"`, append `"contentType"`. Two-line change in each method. No other service-side logic changes.

5. **Sort-blocker.** Verify `sort=content` returns HTTP 400. If the mapper resolves it (because the `FieldMapping` registration exposed it as sortable), use the overload that suppresses the sort accessor, or add an explicit reject. The test for this is load-bearing — see step 7.

6. **API doc node #8 update.** Append `content` to the documented `fields` vocabulary. One-line edit. (Strictly speaking this is a graph-side change, done after the PR opens; mention it in the PR body.)

7. **Tests (load-bearing per DiVoid #275)** under `Backend.tests`:
   - **Default-shape regression.** `GET /api/nodes?count=5` returns rows with no `content`/`contentTruncated`/`contentLength` properties (assert key absence, not null-value).
   - **Text inline.** Seed a `text/markdown` node with body "## Hello". `GET /api/nodes?fields=id,content&id=<id>` returns `{"id":…,"content":"## Hello"}`. No `contentTruncated`. No `contentLength`. `contentType` also present (auto-included).
   - **Binary inline.** Seed an `image/png`-typed node with N bytes. `GET /api/nodes?fields=id,content&id=<id>` returns `{"id":…,"content":"<base64 of the bytes>"}`. Round-trip decode the base64 and assert byte-equality with the original.
   - **Empty content.** Seed a node with no content. `GET /api/nodes?fields=id,content&id=<id>` returns `{"id":…}` — no `content`, no `contentType`.
   - **Truncation — text.** Seed a `text/plain` node with `MaxInlineBytes + 1024` bytes of ASCII. Response: `content` has the first `MaxInlineBytes` decoded; `contentTruncated=true`; `contentLength=<original>`.
   - **Truncation — binary.** Seed an `application/octet-stream` node with `MaxInlineBytes + 1024` random bytes. Response: `content` base64-decodes to exactly the first `MaxInlineBytes` of the original.
   - **Multi-byte UTF-8 boundary.** Seed a `text/plain` node whose `MaxInlineBytes`-th byte falls in the middle of a multi-byte character (e.g., a series of 3-byte CJK characters arranged so the cut splits one). Response: `content` decodes cleanly, no replacement characters, length is `<= MaxInlineBytes` and ends at the last complete code-point.
   - **Invalid UTF-8 in text-typed content.** Seed a `text/plain` node with explicitly-invalid byte sequence (e.g. lone surrogate). Response: row carries `content` as base64 (fallback path), `contentTruncated=false`, no exception thrown, response succeeds.
   - **Mixed page.** Seed 3 text + 2 binary + 2 empty-content nodes. `GET /api/nodes?fields=id,name,content` returns all 7 with their respective shapes.
   - **`sort=content` rejected.** `GET /api/nodes?sort=content` returns HTTP 400.
   - **Path-query parity.** Seed a documentation node linked to a project node. `GET /api/nodes?path=[id:<projectId>]/[type:documentation]&fields=id,content` returns the documentation row with inline content.
   - **No-content not-fetched** (load-bearing negative test, per #275). Seed 5 nodes each with 1 MiB of content. `GET /api/nodes?count=5` (default fields) — assert the response body size is < 50 KiB (proves the bytes column is not projected when `content` is not in `fields`).
   - **Load-bearing substitution probe** (per #275): temporarily comment out the encoder call in the mapper setter and assert the text-inline and binary-inline tests both **fail with a concrete value mismatch** (not green-on-removal). Document the substitution in the PR body.

8. **Architectural doc commit.** This document, committed at `docs/architecture/list-inline-content.md` on the implementation branch as part of the same PR.

9. **PR.** One PR, branched from `main` tip. Push under the `pooshit` profile (telmengedar/DiVoid repo requirement, DiVoid #184). Do **not** bundle anything else into this PR (no MCP changes — those are #1181). PR body references this doc and DiVoid #1180.

### Smells to actively guard against during implementation (lifted from #114 §3)

- Local re-implementation of the text-vs-binary boundary instead of calling `TextContentTypePredicate.IsText` — §5.4 violation.
- `var` anywhere in the new code — §3 violation.
- Banner comments inside method bodies — §4 violation. XML summaries on the encoder, mapper field, and DTO properties carry the intent.
- `Task.FromResult(...)` wrapping `AsyncPageResponseWriter` for the post-hydrate fallback — §3 violation. The fallback path stays properly async.
- A separate `IInlineContentService` or similar — over-decomposition. The encoder is a pure helper, not a service.
- A new controller method for "list with content" — wrong architectural seam. The change rides the existing `ListPaged`.
- In-process truncation of the **encoded** string instead of the byte prefix — wrong order, fails the binary symmetry test.
- Adding `content` to `DefaultListFields` "for convenience" — wrong, breaks the default-unchanged invariant.
