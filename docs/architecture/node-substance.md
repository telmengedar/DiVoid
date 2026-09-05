# Architectural Document: `Substance` — a client-filled compressed form on the node

## TL;DR

Add **one nullable text column** `Substance` to `Node`, exposed as `substance` on `NodeDetails` and as one `NodeMapper` field-mapping. **No new endpoint, no new DTO, no new MCP tool, no index, no service method.**

- **Read** — `GET /api/nodes/{id}` returns it automatically (that endpoint projects the whole mapper vocabulary; measured). Bulk read is opt-in: `GET /api/nodes?id=1,2,3&fields=id,substance`. `substance` is **not** added to `DefaultListFields`, so ordinary listings never carry it.
- **Create** — `substance` in the `POST /api/nodes` body (one column added to the existing INSERT).
- **Update** — `PATCH /api/nodes/{id}` `replace /substance` (via `[AllowPatch]`).
- **Delete** — the same op with `value: null`; NULL is the canonical absent state and is omitted from JSON. Identical verb to the existing `clear_severity` / `clear_root_node_id`.
- **MCP** — parameters on six existing tools (`get_node` +1 returned key; `patch_node` +`substance`/+`clear_substance`; the four `create_*` +`substance`). **Zero new tools**, so the repo's new-tool sign-off gate is not touched.

**Cost to read substance alone, measured on node #11367 today:** `GET /api/nodes/11367` = **374 B** and 1 round trip; the same node's content is **8,292 B** (`?fields=id,content` = 8,529 B). Bulk: 1 round trip for N nodes.

**Cost of the change:** the existing startup `CreateOrUpdateSchema<Node>` adds the column; existing rows get NULL; no backfill, no migration file, no deploy hand-step. **The mechanism differs per engine, and the deploy target is the cheap one:** on PostgreSQL Ocelot emits a genuine `ALTER TABLE … ADD COLUMN`; on SQLite it rebuilds the table — but it does that on *every* startup already, with or without this change (A9a/A9b, both settled below).

**Strongest rejected alternative:** a `content`-style endpoint pair (`GET`/`POST /api/nodes/{id}/substance`). Rejected because the consumer's flow is bulk assembly of many candidate nodes, and a per-node endpoint makes that N round trips where `?fields=` makes it one — while adding two endpoints, two service methods and two MCP tools.

---

**Repo path:** `docs/architecture/node-substance.md` (this document; the DiVoid node and the working-tree file are byte-identical — neither is a summary of the other).
**Source task:** DiVoid **#11367** (go-ahead 2026-09-05). Consumer context: **#11366** §2, **#11364**, **#11365** §3.
**Contracts held load-bearing:** Design Contracts **#1136** (§1 KISS/DRY/YAGNI, §5 Pre-Design Checklist), Code Contracts **#114** §0, divoid-mcp invariants **#6105**.

---

## 1. Problem Statement

Toni, verbatim (#11367, GO-AHEAD 2026-09-05):

> *"i like to add another property to nodes. "Substance" - substance is supposed to hold the most compressed version of the content you can think of, no narration, no prose, no layout - its supposed to contain a version agents can read and still get the content without having to process all of that data which is only supposed to make it readable for humans. The property is filled by clients (its kinda optional you could say) - so its only relevant to exist and that our endpoints and mcp provides the necessary CRUD."*

And the consumer-side requirement the go-ahead preserved (#11367, question 1):

> *"Whichever is chosen has to be **fetchable without the full content**, since the whole point is not paying for the bytes you are trying to avoid."*

That is the whole requirement. Two sentences of ask; per **RULING 2026-09-03** (#1220) the solution is sized against that, out loud: **one column, one DTO property, one mapper mapping, one INSERT column, one attribute.** If this document proposed a new type, a new service or a new endpoint, the ratio would itself be the finding.

## 2. Scope & Non-Scope

### In scope

| | |
|---|---|
| Persistence | `Node.Substance` — nullable text column, no index |
| Wire | `NodeDetails.Substance`, one `NodeMapper` field-mapping named `substance` |
| Create | `substance` accepted in the `POST /api/nodes` body |
| Read | inline on `GET /api/nodes/{id}`; opt-in via `?fields=substance` on list and path queries |
| Update | `PATCH /api/nodes/{id}` `replace /substance` |
| Delete | `PATCH /api/nodes/{id}` `replace /substance` with `value: null` |
| MCP | `substance` parameter/return key on six existing tools |
| Tests | backend HTTP tests + MCP smoke coverage (§16) |

### Explicitly out of scope — named, not merely absent

- **Generating substance, and any evaluation of its quality.** Processor's problem (#11367, #11366 §2). Nothing here blocks it.
- **Embeddings / semantic search.** `EmbeddingInputComposer`, `RegenerateContentEmbedding` and `RegenerateEmbeddingViaBranches` are **untouched**. `substance` is not composed into the embedding input and is not indexed. #11367 q2 is deferred by the go-ahead and by the task's own words.
- **Staleness tracking** — hash pairs, derived-from markers, drift detection. Not designed. The measured gap this leaves is stated as an open question in §15.1, not designed around.
- **Backfill** of existing nodes. Existing rows are NULL and stay NULL until a client writes.
- **A `noSubstance` / `substance` list filter or sort.** Nobody asked; the plausible caller is the generation pass, which is out of scope. §15.2.
- **`divoid_search` returning substance.** Its field list is fixed in `search.py:156`. §12.4 argues the rejection; §15.3 names the seam.
- **Frontend.** No workspace/canvas surface.
- **Server-side size enforcement.** The go-ahead says advisory at most; §11.3 states the bound that already exists.

## 3. Does the "capture the ORIGINAL, derive the compressed form" ruling forbid this?

The brief flagged this and it must be answered plainly, because `Substance` **is** a compressed form stored as a field, which is superficially what the #1220 §2 addendum (2026-08-28, Toni) rules out.

**It does not forbid this design. It permits it — explicitly.** Checked against the addendum's own text, not a paraphrase of it:

| Addendum's binding condition | This design |
|---|---|
| *"the range was discarded at the moment of capture, silently and irreversibly"* — the failure is **loss at the capture point** | `Content` is retained **unchanged**. No field is removed, truncated, or replaced. Nothing is discarded, so there is nothing to be irreversible about. |
| *"A field whose declared purpose is a derived value is a lossy capture point"* | `Content` remains the capture point and its declared purpose is unchanged. `Substance` is a second field beside it, not the capture point. |
| *"Derived values are computed, cached or added as siblings — never the primary carrier"* | `Substance` is **added as a sibling** — one of the three shapes the clause names as permitted. The `birthday` precedent (#8331) the addendum itself cites is the same shape: raw stays in the model, a derived form is added beside it. |
| *"…computed on demand"* | Not available here, and the addendum's own wording permits the alternative: **"computed, cached, or added as siblings"**. #11367 establishes that condensation is not machine-derivable on demand — *"Models generate prose even when instructed to be compact"* — so the derived value is stored rather than recomputed. That is the "cached" branch. |

**What would falsify this reading** — the addendum *would* bind if any of these were in the design, and none are:

1. `Content` replaced by, or truncated in favour of, `Substance`.
2. `Substance` returned in a position where a caller asked for `Content` (a silent substitution).
3. `Substance` becoming the default body a read path returns, so that the original is reachable only by extra effort.

The residual worry the addendum is really about — a compressed form silently standing in for the original — reappears here not at capture but at **read**, and it lands on the client: a consumer that assembles `Substance` and treats it as the node is reading a lossy form. #11367 assigns that acceptance test explicitly to the filler (*"does the extract still answer the question the full node answered"*). The server-side consequence is one rule, honoured throughout §10: **`substance` is never returned in place of `content`, under any field selection.**

## 4. Assumptions & Constraints — measured, not assumed

Every row was read from the tree at `526eab4` or measured against the live API on 2026-09-05.

| # | Fact | Where |
|---|---|---|
| A1 | `GET /api/nodes/{id}` projects the **whole** `NodeMapper` vocabulary, not `DefaultListFields`. Proof: it returns `x` and `y`, which are **not** in `DefaultListFields` (`NodeMapper.cs:40`). Measured response for #11367: `id, type, name, status, rootNodeId, contentType, x, y, ownerId, access, created, lastUpdate`. | `NodeService.cs:1273,1279`; live |
| A2 | `content` is the one mapping excluded from that projection, because its setter writes to `RawContent` (`[IgnoreDataMember]`) and `PostProcess` only encodes it when `content` is in the requested field set. | `NodeMapper.cs:47–52`, `NodeDetails.cs` |
| A3 | Consequence of A1+A2: a **new field-mapping appears on `GET /api/nodes/{id}` with no further change**, and does not appear in list responses unless requested. This is exactly the `x`/`y` behaviour, not a new inconsistency. | A1, A2 |
| A4 | List and path queries default `filter.Fields` to `DefaultListFields` when the caller supplies none. | `NodeService.cs:549, 792, 874` |
| A5 | An unknown `?fields=` value returns **HTTP 400 `badparameter`** with the available list — it does not degrade silently. Measured: `?fields=id,substance` today → `400 {"code":"badparameter","text":"Unknown field 'substance'. Available: …"}`. | live |
| A6 | An unknown PATCH path returns **400** (`PropertyNotFoundException`), and a property without `[AllowPatch]` returns **400** (`NotSupportedException`). The path check runs before the row lookup — measured: PATCH `/substance` on a non-existent node id returns 400, not 404. | `DatabasePatchExtensions.cs:43–48`; live |
| A7 | `replace` with `value: null` is the established clear verb for a nullable column and is already tested for two of them. | `NodePatchHttpTests.Patch_Severity_ReplaceNull_ClearsValue`, `NodeRootNodeIdTests.Patch_ReplaceRootNodeId_WithNull_ClearsValue` |
| A8 | An unsized `string` property is the repo's shape for unbounded text: `Message.Body` carries arbitrary markdown with no `[Size]`, and the only `[Size]` on a string in the whole model is `Message.Subject` (256). | `Models/Messages/Message.cs:32, 40`; `grep '\[Size('` over `Backend/Models` |
| A9 | Schema evolution is `SchemaService.CreateOrUpdateSchema<Node>` at startup — there is no migrations folder. | `Init/DatabaseModelService.cs:35` |
| A9a | **On PostgreSQL the added column is a genuine `ALTER TABLE … ADD COLUMN`.** The class DiVoid executes is `Pooshit.Ocelot.Schemas.SchemaService` — `DatabaseModelService` constructs it and calls `CreateOrUpdateSchema<T>` (**not** the parallel `Entities.Schema.SchemaUpdater`, which DiVoid never touches). `PostgreInfo.MustRecreateTable` returns `false` unconditionally in **both** overloads, so `SchemaService` always takes the `AlterTableOperation` branch, which emits `ADD COLUMN`. Corroborated by Ocelot’s own test, which pins the exact statement `ALTER TABLE test ADD COLUMN "url" character varying`. | source, `C:/dev/claude/Pooshit.Ocelot` at the **0.23.0 build commit `9594f9e`**: `Ocelot/Schemas/SchemaService.cs:47`, `:149`, `:154–160`; `Ocelot/Info/PostgreInfo.cs:677–684` and `:654`; `Ocelot.Tests/Postgres/PostgresSchemaUpdateTests.cs:48–57`. DiVoid call site: `Backend/Init/DatabaseModelService.cs:30, 35` |
| A9b | **On SQLite the `node` table is rebuilt on every startup, and not because of this change.** On the live path, `SchemaService` compares `existingSchema.Index` with `targetSchema.Index` — **both `IndexDescriptor[]`** — using `.Equals`, which for arrays is reference equality and is therefore **always false**, even though `IndexDescriptor` overrides `Equals` with element-level value comparison that the collection-level call never reaches. `SQLiteInfo.MustRecreateTable` ORs that comparison in, so SQLite rebuilds unconditionally. **The dead comparison is not SQLite-specific:** the same two comparisons gate the “did anything change at all” early return, so that return never fires on **any** engine — Postgres reaches the `ADD COLUMN` branch and re-runs `UpdateIndices`/`UpdateUniques` every startup too. That is an upstream Ocelot defect, filed by the operator against Pooshit.Ocelot; it changes nothing for this design, which depends only on which branch Postgres takes. Measured: three consecutive startups against one scratch copy of `DiVoid.db3` moved `node.rootpage` 22 → 12 → 7 → 5, and **runs 2 and 3 carried no schema delta at all**. Both rows preserved throughout; `substance` landed at its declaration position (between `content` and `embedding`), which is the rebuild signature John and QA each observed. | source at `9594f9e`: `Ocelot/Schemas/SchemaService.cs:144` (dead early return), `:149`, `:151–152`; `Ocelot/Info/SQLiteInfo.cs:687–693`; `Ocelot/Schemas/TableSchema.cs:16, 21`; `Ocelot/Schemas/IndexDescriptor.cs:49`; measurement 2026-09-05 against a scratch copy only — `Backend/DiVoid.db3` never opened for write |
| A10 | The `Created`/`LastUpdate` backfill in `DatabaseModelService` exists because those are **non-nullable** with a `[DefaultValue]` sentinel. A nullable column needs no analogue. | `Init/DatabaseModelService.cs:40–45` |
| A11 | `LastUpdate` is bumped by node PATCH, content POST **and** content PATCH alike — so it cannot distinguish "content changed" from "substance written". | `NodeService.cs:996, 1168, 1237` |
| A12 | No `MaxRequestBodySize` is configured; Kestrel's default (30,000,000 B) is the effective request bound, the same bound `Message.Body` documents. | `Program.cs:19–21` (no `options.Limits` line anywhere in the tree) |
| A13 | `divoid_list` passes `fields` through verbatim, unfiltered — no client-side vocabulary check (invariant 6 compliant). | `list_nodes.py:231, 264–265` |
| A14 | `divoid_search` builds a **fixed** field list and accepts no caller `fields`. | `search.py:156–163` |
| A15 | The four `create_*` MCP tools each build their own `node_body` dict and `POST /nodes`, then post content separately. | `create_node.py:142`, `create_task.py:211`, `create_documentation.py:190`, `create_session_log.py:142` |

**Constraints inherited:** SQLite and PostgreSQL both supported; `<Nullable>disable</Nullable>` in `Backend.csproj` (no `?` on reference types); K&R braces, 4-space indent (`.editorconfig`); the MCP is a pure client wrapper that adds no backend behaviour (`divoid-mcp/CLAUDE.md`).

## 5. Architectural Overview

```
POST /api/nodes            ─┐
  { …, "substance": "…" }   │
                            ├──► NodeService.CreateNode ──► INSERT node(… , Substance)
PATCH /api/nodes/{id}      ─┤        (one column added to the existing statement)
  [{replace /substance …}]  │
                            └──► NodeService.Patch ──► DatabasePatchExtensions.Patch
                                     (works because of [AllowPatch]; no service change)

GET  /api/nodes/{id}   ──► NodeMapper (whole vocabulary)  ──► { …, "substance": "…" }   [A1/A3]
GET  /api/nodes?fields=id,substance
                       ──► NodeMapper (requested fields)  ──► rows of { id, substance }

                          Content ────────────── untouched. Not read, not written,
                                                 not truncated, not substituted.
                          Embedding ──────────── untouched. Substance is not composed in.
```

The entire change is **one property threaded through the paths that already exist for every other scalar on `Node`**. The `Severity` (#1605, commit `7297ffa`) and `RootNodeId` (#3375, commit `6e661b8`) commits are the two exact precedents, and their file footprints (6–7 files, 3–22 production lines each) are the size this should land at.

## 6. Components & Responsibilities

| Component | Owns | Does **not** own |
|---|---|---|
| `Node` (entity) | the stored value and its nullability | any interpretation of the value; any size rule |
| `NodeDetails` (DTO) | the wire name `substance` on read and on `POST` | encoding — it is a plain string, unlike `Content` which needs `InlineContentEncoder` |
| `NodeMapper` | one field-mapping `substance` ↔ `Node.Substance`; **not** a `DefaultListFields` member | the decision to *fetch* it — that is the caller's `?fields=` |
| `NodeService.CreateNode` | writing the supplied value on insert | validating, normalising or defaulting it |
| `DatabasePatchExtensions` | update + clear, unchanged, via `[AllowPatch]` | nothing new |
| divoid-mcp tools | passing the value through in both directions | any judgement about content — invariant 6: the backend is the authority (#6105) |

**No new component.** Every row above is an existing component gaining one line to three lines.

## 7. Interactions & Data Flow — the four CRUD verbs

### Create
`POST /api/nodes` with `substance` in the body. Absent ⇒ NULL. No server default — absence is meaningful, exactly as for `Severity` (`CreateNode_WithoutSeverity_SeverityIsNullAfterGet`).

### Read
1. **Single node** — `GET /api/nodes/{id}` returns it inline, no opt-in (A1/A3). This is what satisfies Toni's *"you have it right at hand all the time"* (#11367).
2. **Many nodes** — `GET /api/nodes?id=1,2,3&fields=id,substance` (or `?linkedto=`, `?rootNodeId=`, `?path=`, `?query=` — all route through the same field selection, A4). One round trip, N rows, no content bytes.
3. **NULL is omitted** from the JSON, as every null-valued property already is (measured: `severity` is absent from `GET /api/nodes/11367`).

### Update
`PATCH /api/nodes/{id}` body `[{"op":"replace","path":"/substance","value":"…"}]`. Write access on the node is required by the existing gate; no new authorization branch (unlike `/access` and `/ownerId`, which have owner/admin branches — `substance` is an ordinary field).

### Delete
**The same operation with `value: null`.** `[{"op":"replace","path":"/substance","value":null}]` sets the column NULL, which is the only "absent" state and is omitted from every response. This is the same verb `severity` and `rootNodeId` already use (A7), and it is why no `DELETE /api/nodes/{id}/substance` route is needed.

> **Stated limit, not a hedge:** `value: ""` stores an empty string verbatim and is *not* normalised to NULL. The server stores what it is given (the go-ahead: *"The server stores and returns what it is given"*). So `""` and NULL are two spellings of "no substance" that a reader can distinguish — `""` serialises as `"substance":""`, NULL is omitted. Normalising would be server-side interpretation of a client-owned field, which the ruling excludes. §15.4 records this for Toni if he wants it decided the other way.

## 8. Data Model (Conceptual)

`Node` gains one attribute:

| | |
|---|---|
| Name | `Substance` |
| Type | nullable text, unbounded (unsized `string`, per A8) |
| Index | **none** — nothing filters, sorts or joins on it |
| Patchable | yes (`[AllowPatch]`) |
| Default | NULL, for new rows and for every pre-existing row |
| Owner | the client that writes it |
| Relationship to `Content` | sibling. Independent lifecycle: writing one never reads or writes the other. |

No new entity, no new table, no new relationship. The `[Index("node")]` / `[Index("nodestatus")]` composites are untouched.

## 9. Contracts & Interfaces — REST

| Operation | Request | Response / effect | Failure |
|---|---|---|---|
| Create with substance | `POST /api/nodes`, body `{"type":…,"name":…,"substance":"…"}` | 200 + created node (which includes `substance`, A1) | unchanged |
| Create without | same, key absent | column NULL; key omitted from the response | — |
| Read one | `GET /api/nodes/{id}` | `substance` present when non-null, omitted when null | 404 unchanged |
| Read many | `GET /api/nodes?…&fields=id,substance` | rows carrying only the requested fields | unknown field ⇒ 400 `badparameter` (A5) — including against a backend that predates this change |
| Update | `PATCH /api/nodes/{id}`, `[{"op":"replace","path":"/substance","value":"…"}]` | 200 + updated node; `LastUpdate` bumped | 400 on a pre-change backend (A6); 404 when not writable |
| Delete | same with `"value":null` | column NULL; key omitted thereafter | as above |
| Sort | `?sort=substance` | **works** — the mapper key exists and the column is text. No guard is added. | — |

**Invariants the implementation must hold:**

- **I1** — `substance` is never returned in place of `content`, under any field selection or content type.
- **I2** — no read or write of `substance` reads, writes or truncates `Content`.
- **I3** — no read or write of `substance` regenerates or clears `Embedding`.
- **I4** — `substance` is absent from `DefaultListFields`, so a caller who does not ask never pays for it.

**Deliberately *not* added, and why** — the `content` path needs three special cases that `substance` does not: `?fields=content` implicitly adds `contentType` (`NodeService.cs:796`), `sort=content` is rejected (`:785`), and `InlineContentEncoder` chooses UTF-8 vs base64. `substance` is a plain sortable string with no companion type field, so all three are absent by construction rather than by omission.

## 10. Contracts & Interfaces — divoid-mcp

**No new tool.** Six existing tools gain a parameter or a key. This matters beyond ergonomics: `divoid-mcp/CLAUDE.md` requires human sign-off from the repo owner for a *new tool*, and this design does not trigger that gate.

| Tool | Change | Lines |
|---|---|---|
| `divoid_get_node` | add `"substance": data.get("substance")` to the returned dict; name it in the docstring and `_TOOL_DESCRIPTION` | +1 code |
| `divoid_patch_node` | `substance: str \| None = None` and `clear_substance: bool = False`; compose `replace /substance` (value or `None`); include both in the `no_fields_to_patch` guard | +6 code |
| `divoid_create_node` | `substance: str \| None = None` → `node_body["substance"]` | +2 |
| `divoid_create_task` | same | +2 |
| `divoid_create_documentation` | same | +2 |
| `divoid_create_session_log` | same | +2 |
| `divoid_list` | **no code change** — `fields` already passes through verbatim (A13). Name `substance` in `_TOOL_DESCRIPTION` as an available field. | docs only |

**Invariant compliance (#6105), stated because this package has been bitten here:**

- **Invariant 6 (no client vocabulary).** `substance` is passed through as an opaque string. **No length check, no "is this prose?" check, no normalisation, no rejection of any value the backend accepts.** The shape of a good substance is a client convention and the backend has no opinion; the wrapper must have none either.
- **Invariant 5 (guard before HTTP).** `clear_substance` participates in the existing `_check_invariants` `no_fields_to_patch` computation — a structural invariant (a PATCH with no ops is a no-op), not a vocabulary rule. Nothing new is guarded.
- **Invariant 4 (bytes not strings).** Does **not** apply: `substance` travels inside the JSON body of `POST /nodes` / `PATCH /nodes/{id}`, which `http_client.post_json` / `patch_json` already handle. It must **not** be routed through `post_bytes` — that path exists for the content blob, and re-using it would create the second content endpoint this design exists to avoid.
- **Invariant 3 (no retries)** and **1/2 (key containment, stderr)** are untouched.

**DRY check (#1267):** the `node_body` addition is a **2-line block × 4 sites = 8**, below the ~15–20 threshold, so it is inlined at each site and no helper is extracted. Note that the surrounding `node_body` construction is *already* duplicated 4× (A15); this design does not extend that duplication meaningfully and does not refactor it — a `node_body` builder is a separate cleanup with its own justification, and folding it into this change would make a 2-sentence feature touch four files structurally.

## 11. Cross-Cutting Concerns

**11.1 Authorization.** None new. Read follows `BuildVisibilityPredicate(write: false)`, write follows `write: true`, both applied at the query level exactly as for `name`/`status`. `substance` is **not** owner-or-admin gated — it is ordinary content-adjacent data, not an access-control field like `/access` or `/ownerId`.

**11.2 Consistency and concurrency.** Last-write-wins on a single column, inside the existing single-statement UPDATE. Two clients writing `substance` concurrently is the same race the repo already accepts for `name` and `status`; no locking, no optimistic concurrency token is added, because none exists for any other field and inventing one here would be a new mechanism for a problem nobody reported.

**11.3 Size.** No server-side cap. The effective bound is Kestrel's default request-body limit, 30,000,000 B (A12) — the same bound `Message.Body` documents. The go-ahead makes any tighter limit "advisory at most", and an advisory limit the server does not enforce is documentation, which belongs in the tool description, not in code. **Falsifier:** if a `MaxRequestBodySize` is ever configured in `Program.cs`/`Startup.cs`, that number moves; nothing in this design pins it.

**11.4 Observability.** The existing `logger.LogInformation("Patching node '{nodeId}'", …)` and `"Creating node '{name}'"` lines already cover these calls. **Do not log the substance value** — it is user content and the log line has no need of it.

**11.5 Error handling.** Entirely inherited: 400 from `PropertyNotFoundException`/`NotSupportedException`, 404 from `NotFoundException<Node>`, all already mapped by the `Pooshit.AspNetCore.Services` middleware. No new exception type, no new handler.

**11.6 Idempotency.** `replace /substance` with the same value is naturally idempotent apart from the `LastUpdate` bump, which is pre-existing behaviour of every PATCH.

## 12. Quality Attributes & Trade-offs

### 12.1 The cost of reading substance — measured, in bytes and round trips

| What the caller does | Round trips | Bytes on the wire (node #11367, measured 2026-09-05) |
|---|---|---|
| `GET /api/nodes/11367` (metadata + substance) | 1 | **374 B** envelope + `len(substance)` |
| `GET /api/nodes?id=11367&fields=id,name` (floor for a list row) | 1 | **151 B** |
| `GET /api/nodes?id=…&fields=id,substance` for N nodes | **1** | ~40 B/row + Σ`len(substance)` |
| `GET /api/nodes?id=11367&fields=id,content` (what substance avoids) | 1 | **8,529 B** for this one node |
| `GET /api/nodes/{id}/content` for the worst node named in #11367 | 1 | **195,448 B** |

So: **reading substance never puts content bytes on the wire.** *Falsifier for that sentence:* a caller who explicitly requests `?fields=substance,content` gets both — that is the caller's choice, not the design's. Separately, `GET /api/nodes/{id}` issues an unqualified `Load<Node>()` (`NodeService.cs:1273`), so what that statement selects *inside the database* is an existing property of that endpoint which this design neither changes nor measures; it never reaches the wire (A2).

### 12.2 Pre-Design Checklist (#1136 §5), answered in order

**KISS / DRY / YAGNI**
- *No new type mirroring an existing one* — ✅ no new type at all.
- *No new abstraction with one implementation* — ✅ none.
- *No element justified by "we might need X later"* — ✅ nothing speculative ships. Search integration, staleness, filters and a size cap are all named as out-of-scope or open questions, never built.
- *No deprecation period / feature flag / compatibility shim* — ✅ none. The field is additive and NULL-defaulted; old clients that never send or request it are unaffected.
- *DRY math for every inline-at-N-sites decision* — ✅ §10: 2 lines × 4 sites = 8, below threshold, inlined.

**Existing systems first**
- *Existing surface audited* — ✅ `?fields=` opt-in already exists for exactly this class (`content`, `links`, `linkDetails`); `[AllowPatch]` already exists for update+clear; `POST /api/nodes` already accepts arbitrary `NodeDetails` scalars. All four CRUD verbs land on machinery that is already there.
- *If a new layer is proposed, the concrete reason is named* — ✅ **no new layer is proposed.** §12.4 records why the endpoint-pair layer was rejected against #1136 §2's own criteria (no different lifecycle, no different access pattern, no different security boundary, no different scale).
- *A new persisted data point names the concrete decision it enables in 4 weeks* — ✅ Processor's retrieval harness admits candidates against a byte budget and today truncates by bytes at admission (#11365 §3); a pre-authored substance replaces that truncation. Named consumer, named decision, named window.
- *Consumer chain recursed* — ✅ column → DTO → `GET`/list response → Processor's assembly. The chain terminates in a named consumer, not in "the API serialises it".

**Configurability**
- ✅ no config knob is introduced. No size limit, no feature flag, no toggle.

**Less is better**
- *can-it-be-deleted / merged / inlined, per element:* the column cannot be deleted (it is the feature); the DTO property cannot be merged into `Content` without becoming the lossy capture point §3 forbids; the mapper mapping cannot be inlined because `?fields=` resolves through the mapper's dictionary. **Four candidate elements were deleted** and are recorded in §12.4: an endpoint pair, a `DefaultListFields` entry, an index, and a `NodeFilter` field.
- *Trade-offs named when the complex design wins* — n/a, the simple design wins; the trade-off actually made is §12.3.
- *Radical-clean shape when the existing surface has no consumer* — n/a, nothing is being removed.

**Data deliverables** — no SQL deliverable, no migration file, no backfill script. A9/A10 are the reason.

**Document discipline** — Code Contracts #114 and Design Contracts #1136 cited as load-bearing (header); scope inventory explicit (§2); out-of-scope listed, not merely absent (§2); no predecessor design is superseded by this one.

### 12.3 The one real trade-off

**`GET /api/nodes/{id}` returns substance unconditionally; list requires `?fields=substance`.** That asymmetry is inherited from A1/A3, not chosen — it is exactly how `x` and `y` already behave.

- *Downside, concretely:* a caller who fetches one node and wants only metadata now receives substance bytes it did not ask for. There is no opt-out on that endpoint, because it takes no filter (`NodeController.cs:67`).
- *Probability and cost:* bounded by the field's own purpose — a "maximally compressed" value, and NULL on every node until a client writes one. Today that is 0 B on every node in the graph.
- *Cost of the alternative:* adding a `?fields=` parameter to `GET /api/nodes/{id}` is a new query surface on an endpoint that has never had one, and would change the response shape for every existing caller. That is a bigger change than the problem.
- **Call:** accept the asymmetry. If a caller ever needs metadata-without-substance, `GET /api/nodes?id=N&fields=…` already provides it today, in one round trip.

### 12.4 Alternatives considered and rejected

| Alternative | Why rejected |
|---|---|
| **`GET`/`POST /api/nodes/{id}/substance`, mirroring `content`** | `Content` earns its endpoint pair because it is arbitrarily large, may be binary, and is streamed (`GetNodeData` returns a `Stream`; `UploadContent` reads the raw request body). Substance is small text by construction and needs none of that. Decisive against it: the consumer's flow is **bulk** assembly of many candidates, and a per-node endpoint makes that **N round trips** where `?fields=` makes it **one** — while adding 2 routes, 2 service methods, 2 interface members and 2 MCP tools (which would trigger the sign-off gate). Against #1136 §2's bar for a new layer: no different lifecycle, access pattern, security boundary or scale. |
| **Add `substance` to `DefaultListFields`** | Would put it on every list response, including the workspace canvas listing up to 500 nodes per page. That is the byte cost the field exists to avoid, paid in the opposite direction. `Severity` is in `DefaultListFields` because it is a bounded int that clients sort and filter on; `substance` is unbounded text that nobody filters on. |
| **A second content-type variant on the existing content blob** (#11367 q1's other branch) | Settled by the go-ahead — *"another **property** to nodes"* — and it would force `GET /{id}/content` to grow a selector, i.e. exactly the endpoint complication above. |
| **Index the column** | No filter, no sort predicate, no join uses it. An index on unbounded text costs writes and space for a query nobody has. |
| **A `NodeFilter.Substance` / `NoSubstance` filter** | The plausible caller is the generation pass, which is out of scope. Adding it now is YAGNI with no named consumer; §15.2 records the seam. |
| **Server-enforced size cap** | Go-ahead: advisory at most. A cap needs a number, and no number has an argument behind it. The §11.3 bound already exists. |
| **`include_substance` on `divoid_search`** | Nothing breaks without it: `divoid_search` → ids → `divoid_list(id=[…], fields=["id","substance"])` gets there in one extra call, today, with zero new code (A13). Adding a second parallel opt-in mechanism beside the `?fields=` one is #1136 §2 Form 2 (parallel layer). §15.3 names the seam and the measurement that would justify it. |
| **A staleness signal (hash or timestamp pair)** | Excluded by the go-ahead and not smuggled back in. The gap it leaves is real and measured (A11) — §15.1 surfaces it as a decision for Toni rather than designing it. |

## 13. Risks & Mitigations

| # | Risk | Mitigation |
|---|---|---|
| R1 | **A new MCP requesting `fields=substance` runs against a backend that predates the column** → HTTP 400 on every list call (A5), not a silent degrade. | This is the concrete reason the backend PR must merge **and deploy** before the MCP PR. §14. The MCP change to `divoid_list` is documentation-only, so the blast radius is confined to callers who explicitly pass `fields=["substance"]`. |
| R2 | **`CreateOrUpdateSchema` adds the column destructively, or differently, on the deploy engine.** | Settled by A9a/A9b rather than by precedent. PostgreSQL — the deploy target — takes the `ADD COLUMN` branch by construction (`MustRecreateTable` → `false`), so there is no table copy, no extended lock and no row-count-dependent cost on the `node` table (**10,482 rows**, counted on the live instance 2026-09-05 — the review’s “11k+” was an estimate and this replaces it). SQLite rebuilds, but rebuilds on every startup already, so this change introduces no new behaviour there — measured row-preserving across three restarts. Residual verification in §16: run the suite, then start against a **copy** of `Backend/DiVoid.db3` and read a pre-existing node back. |
| R3 | **The INSERT column list and value list drift out of alignment** — `CreateNode` builds two positional lists (`NodeService.cs:121–122`); an added column with a mis-ordered value writes substance into the wrong field. | Positional, so the compiler will not catch it. Guarded by `CreateNode_WithSubstance_SubstancePersistedToDatabase` **plus** the existing create tests for `severity`/`rootNodeId`/`status`, which fail if the ordering shifts. This is the bug #157 trap the `Severity` commit explicitly called out. |
| R4 | **A future embedding change composes `substance` into the embedding input**, silently making it search-visible — the thing #11367 q2 deferred. | I3 is stated as an invariant in §9 and pinned by a named test in §16. `EmbeddingInputComposer.Compose` takes `(name, content, contentType)` and gains no parameter here. |
| R5 | **The MCP grows a length or shape check** on substance (invariant-6 violation, the `create_task._ALLOWED_STATUSES` shape). | §10 states the prohibition explicitly and §16 pins it with a test that a 50 KB substance passes through unaltered. |
| R6 | **Clients read substance and act as though it were content** (the §3 residual). | Out of the server's hands by construction; #11367 assigns the acceptance test to the filler. The server's contribution is I1 — never substitute — and the fact that `substance` and `content` are separate names on the wire. |

## 14. Rollout / PR decomposition

**Two PRs, backend first — the design agrees with the operator's expectation, and R1 is the concrete reason rather than a convention.**

| PR | Contents | Blocked by |
|---|---|---|
| **PR 1 — backend** | `Node.Substance`, `NodeDetails.Substance`, `NodeMapper` mapping, `CreateNode` INSERT column, backend tests, this document | — |
| **PR 2 — divoid-mcp** | six-tool parameter/key additions, tool-description updates, smoke coverage | PR 1 **merged and deployed** (A5/R1) |

**No third unit falls out.** The change touches no other package: the frontend is out of scope, no CLI verb is affected (`CliDispatcher` does not touch node fields), and nothing is being removed or refactored. The `node_body` duplication noted in §10 is pre-existing and is deliberately *not* bundled — bundling it would make a two-line feature restructure four files.

**No deploy hand-step — and that claim now rests on the deploy engine, not on the dev engine.** The column is added by the existing startup hosted service (A9). On PostgreSQL that is an `ALTER TABLE … ADD COLUMN` (A9a), so no table copy occurs and the `node` table (10,482 rows) costs no more than an empty one. There is no migration to run, no backfill to schedule, and no flag to flip. Rollback is `ALTER TABLE … DROP COLUMN`, or simply leaving an unused nullable column in place.

**Stated limit, and the input class that would falsify it.** A9a is read from `Pooshit.Ocelot` **source** and from that repository’s own Postgres test. It is **not** measured against a running PostgreSQL instance, because the only PostgreSQL carrying this schema is production and nothing here was run against production. The shape that would make it false: an Ocelot build whose `PostgreInfo.MustRecreateTable` does not return `false`. Postgres would then take the `RecreateTable` path (`SchemaService.cs:152`) — `ALTER TABLE node RENAME TO node_original`, create, copy 10,482 rows, drop — and “no hand-step” would have to be re-decided on lock duration and disk headroom. Two checks were run against exactly that class, because the clone read is `0.23.1-preview` while `Backend.csproj` references `0.23.0-preview`. **The window is pinned by the package, not by a guessed tag boundary:** the restored `pooshit.ocelot/0.23.0-preview/pooshit.ocelot.nuspec` records `commit="9594f9e…"` as the commit the shipped assembly was built from, so the window is `9594f9e..HEAD`. Over that window: (a) `SchemaUpdater.cs` is **byte-identical**; (b) **no commit changes a `MustRecreateTable` occurrence** anywhere under `Ocelot/`. One commit in the window — `8f46e50` — does touch the live `Ocelot/Schemas/SchemaService.cs`, but only its `UpdateIndices` `DROP INDEX` path (adding `IF EXISTS`); it leaves the `MustRecreateTable` decision and the `ADD COLUMN` branch untouched, so A9a is unaffected. The version gap is closed by measurement rather than assumed away. What would settle it outright: one startup against a **disposable** PostgreSQL holding an existing `node` table, comparing `relfilenode` before and after. That is cheap, and it belongs **before the deploy**, not before the merge — it gates nothing in PR 1.

## 15. Open Questions

**15.1 — Staleness: the go-ahead pushed it to the client, and the client cannot currently detect it. Is that accepted?**
Not a request to design it — a measured consequence Toni should see before it is inherited. `LastUpdate` bumps on node PATCH, content POST and content PATCH alike (A11), so it moves when substance is written and gives a client no way to ask *"has content changed since I wrote this substance?"*. The client's only remaining options are (a) re-fetch and hash the content, which is the byte cost the field exists to avoid, or (b) keep its own node-id-keyed record — which is precisely the sidecar shape #11327 measured drifting silently over 12 of 25 rows. **My position: ship without it.** The field is useful, optional and client-filled, and a drift signal has no consumer until Processor's generation pass exists. If it is ever wanted, the cheapest form is one additional nullable timestamp written when `Content` changes, and it costs nothing today to not have it. **Flagging, not designing.**

**15.2 — Should the generation pass be able to find unfilled nodes?** A `noSubstance=true` list filter would answer *"which nodes still need substance?"* in one query. Its only caller is the generation pass, which is out of scope, so it is not designed. If Processor's harness is built and needs it, it is a ~12-line addition mirroring `NoSeverity` — worth a follow-up task, not worth predicting the shape of now.

**15.3 — `divoid_search` cannot return substance at all (A14), by design.** The workaround is one extra `divoid_list` call. If Processor measures that round trip as a real cost, `include_substance` on `divoid_search` is a 4-line change at `search.py:156–163`. Recommend filing it as a follow-up **after** a measurement, not before.

**15.4 — Empty string vs NULL, and the fact that “unnormalised” is asserted but unguarded.** §7 leaves `""` unnormalised. If Toni prefers one canonical “no substance”, the rule would be “the service maps whitespace-only to NULL on write” — one line, one test. Not built, because normalisation is server-side interpretation of a client-owned field. **What is now recorded rather than implied:** no test in §16 pins the unnormalised-storage property. QA measured this directly — a trim-plus-blank-to-null mutation of the mapper setter survives the whole suite 682/682, because every substance literal in the ten tests is already trimmed and non-blank and S4 exercises `null` rather than `""`. So “the server stores what it is given” is currently a **stated intent, not a guarded invariant**. If it is meant to be load-bearing it needs one test that writes a value normalisation would change (leading/trailing whitespace, or `""`) and asserts it comes back byte-identical; that test is what the surviving mutation would kill. **Not added to §16 unasked** — it is a decision, and it is Toni’s.

**15.5 — Nothing in this design is blocked.** John can implement PR 1 in full from §16 with no answer to any of the above.

## 16. Implementation Guidance for the Next Agent

### PR 1 — backend, in order

1. **`Backend/Models/Nodes/Node.cs`** — add `Substance` (`string`, `[AllowPatch]`, **no `[Index]`, no `[Size]`**). The XML `<summary>` says **what the field is, in one tight line, and nothing more**, per Code Contracts **#114 §4**, which routes rationale out of doc comments and into this document. The shipped line is the reference form: *“client-supplied compressed form of `Content`; null when unset, never written by the server.”* **Do not put “not embedded, not indexed” — or any other implementation choice — in the comment.** Those are rationale and they live here: §2 (embeddings out of scope), §8 (`Index: none`), §9 (I3).
2. **`Backend/Models/Nodes/NodeDetails.cs`** — add `Substance` (`string`). Doc comment names the `?fields=substance` opt-in for list responses and the unconditional presence on `GET /api/nodes/{id}`.
3. **`Backend/Models/Nodes/NodeMapper.cs`** — one `FieldMapping<NodeDetails, string>("substance", DB.Property<Node>(n => n.Substance, "node"), (n, v) => n.Substance = v)` in `Mappings()`. **Do not touch `DefaultListFields` (line 40). Do not touch `PostProcess`.**
4. **`Backend/Services/Nodes/NodeService.cs`** — `CreateNode` only: add `n => n.Substance` to `.Columns(…)` and `node.Substance` to `.Values(…)` **at the same position in both lists** (lines 121–122). No other method changes. Do not touch `Patch`, `UploadContent`, `PatchContent`, `GetNodeById`, `ListPaged`, `ListPagedByPath`, or any embedding path.
5. **Tests** — table below.
6. **Verify** — `dotnet test Backend.tests/Backend.tests.csproj`, then `dotnet run --project Backend` against a **copy** of `Backend/DiVoid.db3` (point `Database__Source` at the copy; the tracked-in-place dev DB is deliberately kept without the column) so the *update* path runs against an existing table rather than the CREATE path, and read one pre-existing node back through `GET /api/nodes/{id}`. Expect a **table rebuild** on SQLite, not an `ALTER` — that is A9b and it is normal on every startup; what this step checks is that the rows survive and the column reads back, not which statement was issued (R2).

Files **not** in PR 1: `NodeController.cs` (no signature change), `NodeFilter.cs`, `INodeService.cs`, `DatabaseModelService.cs`, `DatabasePatchExtensions.cs`, `InlineContentEncoder.cs`, anything under `Services/Embeddings/`.

### PR 1 — coverage. Each row names the **test**, not a mechanism.

New file `Backend.tests/Tests/NodeSubstanceHttpTests.cs` unless noted. Every row states the substitution that must make it fail.

| # | Test | Pins | Fails when |
|---|---|---|---|
| S1 | `CreateNode_WithSubstance_SubstancePersistedToDatabase` | POST → GET round-trip; guards the positional INSERT (R3) | the `Columns`/`Values` pair is mis-ordered, or the column is omitted |
| S2 | `CreateNode_WithoutSubstance_SubstanceIsNullAfterGet` | absence is meaningful; no server default | any default value is introduced |
| S3 | `Patch_ReplaceSubstance_UpdatesField` | update verb + `[AllowPatch]` | `[AllowPatch]` is dropped (400) or the mapping is missing |
| S4 | `Patch_ReplaceSubstance_WithNull_ClearsValue` | the **delete** verb: `replace /substance` with `value: null` leaves the stored value absent | `replace null` stops clearing |
| S5 | `GetById_ReturnsSubstanceInline` | A1/A3 — the single-node read needs no opt-in | the mapping is added to a path that excludes it from `GetNodeById` |
| S6 | `ListPaged_WithoutFieldsOpt_In_OmitsSubstance` | **I4** — the default listing does not carry it | `substance` is added to `DefaultListFields` |
| S7 | `ListPaged_FieldsSubstance_ReturnsSubstanceAndNoContent` | **I1 + I2** — the requested field arrives and `content` does not | `substance` is aliased onto the content mapping, or `content` leaks into the row |
| S8 | `Patch_ReplaceSubstance_DoesNotAlterContent` (PATCH substance → GET `/content` byte-compare) | **I2** | any substance write path touches `Content` |
| S9 | `Patch_ReplaceSubstance_LeavesEmbeddingUntouched` — SQL/branch-level, alongside `EmbeddingPatchSqlShapeTests` | **I3**; R4 | `TouchesName` is widened, or `substance` is composed into the embedding input |
| S10 | `CreateNode_WithLargeSubstance_RoundTripsUnaltered` (≥50 KB) | that **nothing in this repo's own code** truncates or rewrites a 51,200-byte value on the round trip | any of our create / map / read paths truncates or alters the value |

**Falsifier for this table as a whole:** any row whose named test would still pass against an implementation lacking the claimed property. S6 and S7 are the two that carry the design’s load — S6 is the only guard on I4, and it fails loudly the moment someone “helpfully” adds `substance` to the default field list.

**That falsifier was run against this table, and it caught two rows.** QA executed the mutation each row promises will kill it (review **#11379**). Eight rows held. Two did not, and both are corrected above rather than defended: S4 previously also claimed to catch server-side normalisation — a trim-plus-blank-to-null mutation survives 682/682, and §15.4 now records that property as unguarded; S10 previously claimed the bound-type property “on either engine” — `[Size(64)]` survives 682/682 because SQLite does not enforce `VARCHAR(n)` and the suite runs only on SQLite (its 9 Postgres tests skip). **The engine-side type-mapping axis is therefore untested by this suite by construction**, and what would test it is a Postgres-backed run of S10, not another SQLite assertion.

### PR 2 — divoid-mcp

1. `get_node.py` — one returned key + description.
2. `patch_node.py` — `substance` / `clear_substance`, composed exactly like `severity` / `clear_severity`; both added to the `no_fields_to_patch` computation; the module’s supported-paths list gains a `/substance` entry in the same one-line form as its `/severity` and `/rootNodeId` neighbours. **Same discipline as PR 1 step 1: say what the parameter is, not why the design chose it.**
3. `create_node.py`, `create_task.py`, `create_documentation.py`, `create_session_log.py` — one optional param, one `node_body` key each.
4. `list_nodes.py` — `_TOOL_DESCRIPTION` only; **no code change**.
5. **No length check, no shape check, no normalisation anywhere** (invariant 6; R5).
6. Smoke coverage in `tests/smoke/`: create-with-substance → get_node → patch → clear → get_node, and a ≥50 KB value passed through unaltered (R5). Run the smoke suite in an **isolated venv only** — never `pip install -e .` into the environment backing the operational MCP (`divoid-mcp/CLAUDE.md`).

---

*Design: sarah-software-architect, 2026-09-05. Repo path: `docs/architecture/node-substance.md`. Task #11367.*
