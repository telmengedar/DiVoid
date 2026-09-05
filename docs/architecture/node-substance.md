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

### TL;DR — amendment 2026-09-05 (§17): the server clears `Substance` on every content write

This document now carries **two** decisions. The one above shipped as PR #182; this one is not built.

**What** — a successful write to `Node.Content` sets `Substance` to NULL; absence is the client's signal to regenerate. **How** — one assignment term added to each of the **two** UPDATE statements that already write `Content` (`NodeService.cs:1168`, `:1237`), so clear and content write are the **same statement in the same transaction**. **Unconditional** — it fires even when the new bytes equal the old.

- **NULL, never `""`.** Clients test null / absent / empty-or-whitespace alike as "regenerate".
- **Ordering:** write substance **after the last content write**, not merely second. Substance-then-content loses the substance, correctly.
- **Cost: two production lines.** No new statement, read, endpoint, response, column or config.
- **Strongest rejected:** clear only when the bytes changed. `UploadContent` does not read existing content, so that costs a full-blob read (~195 KB worst case); byte-equality cannot tell a cosmetic edit from a substantive one, so it protects only *no-op* writes; and `LastUpdate` and `Embedding` are already rewritten unconditionally on the same writes.

---

**Repo path:** `docs/architecture/node-substance.md` (this document; the DiVoid node and the working-tree file are byte-identical — neither is a summary of the other).
**Source task:** DiVoid **#11367** (go-ahead 2026-09-05) — §§1–16. **Amendment:** DiVoid **#11405** (2026-09-05) — §17, the clear-on-content-write ruling. **Corrections to §10 / §16 PR 2:** QA review **#12175** (2026-09-05) — the shipped MCP mechanism and its measured cost, replacing a prescription that was rejected before implementation. Consumer context: **#11366** §2, **#11364**, **#11365** §3.
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
| **Clear on content write** *(amendment, §17)* | every successful write to `Node.Content` sets `Substance` NULL, in the same statement |
| Tests | backend HTTP tests + MCP smoke coverage (§16, §17.8) |

### Explicitly out of scope — named, not merely absent

- **Generating substance, and any evaluation of its quality.** Processor's problem (#11367, #11366 §2). Nothing here blocks it.
- **Embeddings / semantic search.** `EmbeddingInputComposer`, `RegenerateContentEmbedding` and `RegenerateEmbeddingViaBranches` are **untouched**. `substance` is not composed into the embedding input and is not indexed. #11367 q2 is deferred by the go-ahead and by the task's own words.
- **Staleness *tracking*** — hash pairs, derived-from markers, drift detection. Still not designed, and now positively excluded rather than merely deferred: the §17 amendment does not track the stale value, it destroys it. §15.1 records the supersession.
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
| A9b | **On SQLite the `node` table is rebuilt on every startup, and not because of this change.** On the live path, `SchemaService` compares `existingSchema.Index` with `targetSchema.Index` — **both `IndexDescriptor[]`** — using `.Equals`, which for arrays is reference equality and is therefore **always false**, even though `IndexDescriptor` overrides `Equals` with element-level value comparison that the collection-level call never reaches. `SQLiteInfo.MustRecreateTable` ORs that comparison in, so SQLite rebuilds unconditionally. **The dead comparison is not SQLite-specific:** the same two comparisons gate the “did anything change at all” early return, so that return never fires on **any** engine — Postgres reaches the `ADD COLUMN` branch and re-runs `UpdateIndices`/`UpdateUniques` every startup too (`SchemaService.cs:163–166` — the two calls are guarded by the same two dead comparisons). That is an upstream Ocelot defect, filed by the operator against Pooshit.Ocelot; it changes nothing for this design, which depends only on which branch Postgres takes. Measured: three consecutive startups against one scratch copy of `DiVoid.db3` moved `node.rootpage` 22 → 12 → 7 → 5, and **runs 2 and 3 carried no schema delta at all**. Both rows preserved throughout; `substance` landed at its declaration position (between `content` and `embedding`), which is the rebuild signature John and QA each observed. | source at `9594f9e`: `Ocelot/Schemas/SchemaService.cs:144` (dead early return), `:149`, `:151–152`, `:163–166` (the `UpdateIndices`/`UpdateUniques` calls); `Ocelot/Info/SQLiteInfo.cs:687–693`; `Ocelot/Schemas/TableSchema.cs:16, 21`; `Ocelot/Schemas/IndexDescriptor.cs:49`; measurement 2026-09-05 against a scratch copy only — `Backend/DiVoid.db3` never opened for write |
| A10 | The `Created`/`LastUpdate` backfill in `DatabaseModelService` exists because those are **non-nullable** with a `[DefaultValue]` sentinel. A nullable column needs no analogue. | `Init/DatabaseModelService.cs:40–45` |
| A11 | `LastUpdate` is bumped by node PATCH, content POST **and** content PATCH alike — so it cannot distinguish "content changed" from "substance written". | `NodeService.cs:996, 1168, 1237` |
| A12 | No `MaxRequestBodySize` is configured; Kestrel's default (30,000,000 B) is the effective request bound, the same bound `Message.Body` documents. | `Program.cs:19–21` (no `options.Limits` line anywhere in the tree) |
| A13 | `divoid_list` passes `fields` through verbatim, unfiltered — no client-side vocabulary check (invariant 6 compliant). | `list_nodes.py:231, 264–265` |
| A14 | `divoid_search` builds a **fixed** field list and accepts no caller `fields`. | `search.py:156–163` |
| A15 | The four `create_*` MCP tools each build their own `node_body` dict and `POST /nodes`, then post content separately. **The fact is unchanged and is what forced §17.5's ordering finding**; only the line numbers moved when PR 2 landed. | at `526eab4`: `create_node.py:142`, `create_task.py:211`, `create_documentation.py:190`, `create_session_log.py:142` — a ref-stamped historical claim, still true at that ref. **For the current tree, cite the symbol, not the line:** `grep -n 'node_body' divoid-mcp/src/divoid_mcp/tools/create_*.py`. The post-PR-2 numbers this row briefly carried (`:150` / `:219` / `:198` / `:145`) had drifted again within hours; see §10's note on why a line number is the wrong instrument here |

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

**Amendment 2026-09-05 (§17) — the one arrow the diagram above does not show.** It depicts the *substance*-write and *substance*-read paths, and along those `Content` is genuinely untouched (I2) — that annotation stays true as drawn. §17 adds an arrow in the other direction, on the two paths that write content:

```
POST  /api/nodes/{id}/content  ──► NodeService.UploadContent ──► UPDATE node
PATCH /api/nodes/{id}/content  ──► NodeService.PatchContent  ──► SET ContentType?/Content,
                                                                    LastUpdate,
                                                                    Substance = NULL   ◄── §17
                                                                one statement, one transaction

                          Embedding ──────────── still regenerated by its own statement,
                                                 unchanged. The clear does not ride it.
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
| `NodeService.UploadContent` / `NodeService.PatchContent` *(amendment, §17)* | invalidating `Substance` when they write `Content` — one assignment each, inside the UPDATE they already issue | deciding *whether* the content changed; regenerating anything; reading the old substance; reporting the clear |

**No new component.** Every row above is an existing component gaining one line to three lines.

## 7. Interactions & Data Flow — the four client CRUD verbs, plus one server-initiated clear (§17)

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
>
> **Amended 2026-09-05 (§17.3):** this stays true of *client* writes and is now load-bearing in one direction. The **server** writes only NULL — it never writes `""` — so the two spellings never both arise from the server, and the client-side rule is: **treat null, absent, and empty-or-whitespace-only alike as "no substance, regenerate"**.

### Server-initiated clear *(amendment, §17)*
A fifth verb the client does not issue: a successful `POST` or `PATCH` to `/api/nodes/{id}/content` sets `Substance` NULL as part of the same UPDATE. Nothing else in the API clears it.

## 8. Data Model (Conceptual)

`Node` gains one attribute:

| | |
|---|---|
| Name | `Substance` |
| Type | nullable text, unbounded (unsized `string`, per A8) |
| Index | **none** — nothing filters, sorts or joins on it |
| Patchable | yes (`[AllowPatch]`) |
| Default | NULL, for new rows and for every pre-existing row |
| Owner | the client, for every non-null value. The server writes exactly one value — NULL, on a content write (§17) — and never any other. |
| Relationship to `Content` | sibling with a **one-way** dependency (amended 2026-09-05): writing `Content` clears `Substance`; writing `Substance` still never reads, writes or truncates `Content` (I2). Before §17 this row read "independent lifecycle"; that is no longer true in the content→substance direction. |

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
| **Content upload** *(§17)* | `POST /api/nodes/{id}/content` | content stored **and** `substance` set NULL, one statement, one transaction. No response body — the endpoint has never had one and gains none. | 404 unchanged; on any failure the clear rolls back with the content write |
| **Content patch** *(§17)* | `PATCH /api/nodes/{id}/content` | edited content stored **and** `substance` set NULL. The existing `NodeDetails` response already reports it: the key is simply absent. | 400/404 unchanged; on any failure the clear rolls back with the content write |

**Invariants the implementation must hold:**

- **I1** — `substance` is never returned in place of `content`, under any field selection or content type.
- **I2** — no read or write of `substance` reads, writes or truncates `Content`.
- **I3** — no read or write of `substance` regenerates or clears `Embedding`, and `substance` is never composed into the embedding input. *(§17 adds the converse direction: the clear rides the content UPDATE, not the embedding UPDATE — see §17.7.)*
- **I4** — `substance` is absent from `DefaultListFields`, so a caller who does not ask never pays for it.
- **I5** *(§17)* — every successful write to `Node.Content` leaves `Substance` NULL, and the clear is committed **atomically with** that content write: a content write that rolls back leaves `Substance` exactly as it was.
- **I6** *(§17)* — the only value the server ever writes to `Substance` is NULL. It never writes `""`, never trims, never normalises, and never derives a value.

**Deliberately *not* added, and why** — the `content` path needs three special cases that `substance` does not: `?fields=content` implicitly adds `contentType` (`NodeService.cs:796`), `sort=content` is rejected (`:785`), and `InlineContentEncoder` chooses UTF-8 vs base64. `substance` is a plain sortable string with no companion type field, so all three are absent by construction rather than by omission.

## 10. Contracts & Interfaces — divoid-mcp

**No new tool.** Six existing tools gain a parameter or a key, and one private helper module is added. This matters beyond ergonomics: `divoid-mcp/CLAUDE.md` requires human sign-off from the repo owner for a *new tool*, and this design does not trigger that gate — `_substance.py` is an internal module, not a registered tool.

> **Corrected 2026-09-05, after PR 2 shipped (QA review #12175).** The four composite `create_*` rows previously prescribed a `node_body["substance"]` key in the create body, costed at 2 lines per site. **That mechanism was deliberately rejected before implementation and is not what shipped** — PR 3's own §17.5 finding is why, and it was routed to the implementer mid-task. The table and the DRY math below now describe the shipped shape, **measured against the diff** (`git diff --numstat` against `d2b3288`) rather than estimated. The `get_node` and `patch_node` rows named the right mechanism and are superseded only on their figures.

| Tool | Change | Lines (measured) |
|---|---|---|
| `divoid_get_node` | add `"substance": data.get("substance")` to the returned dict; name it in the docstring and `_TOOL_DESCRIPTION` | **1** code line (+10 doc, −8) |
| `divoid_patch_node` | `substance: str \| None = None` and `clear_substance: bool = False`; compose `replace /substance` (value or `None`); include both in the `no_fields_to_patch` guard | **16** code lines (+18 doc, −9). Shape as designed; the original “+6” understated it because the parameter pair recurs across three signatures |
| `divoid_create_node` | `substance` parameter → **`write_substance(node_id, substance, config)` as the creator's LAST step — after the content POST *and* after the link loop**, never a create-body key | **13** ins / 1 del |
| `divoid_create_task` | same | **13** ins / 1 del |
| `divoid_create_documentation` | same | **13** ins / 1 del |
| `divoid_create_session_log` | same, plus a second parameter line and a pass-through for its `_execute` / `register` split | **15** ins / 1 del |
| `_substance.py` — **new private module** | `write_substance` — the shared helper the four creators call. Returns `None` on success and when there is nothing to write; returns a `partial_state` error envelope naming the surviving node id when the write fails, matching the existing content/link partial-failure shape | **43** lines (34 non-blank) |
| `divoid_list` | **no code change** — `fields` already passes through verbatim (A13). Name `substance` in `_TOOL_DESCRIPTION` as an available field. | **0** code (+3 doc, −2) |

**Why the create tools write substance by PATCH, and why it goes last.** The shipped order in all four creators is:

```
POST /nodes  →  POST /nodes/{id}/content  →  POST /nodes/{id}/links ×N  →  PATCH /nodes/{id} replace /substance
```

**Two separate constraints hold that position, from two different findings. Neither alone is sufficient, and the second is the one a reader is likely to drop:**

1. **After the content POST** — because PR 3 clears `Substance` on *any* content write (§17), so a create-body key, or any write issued before the content step, is destroyed a moment later. The tool would report success and the value would be gone. *(§17.5, found while designing PR 3.)*
2. **After the link step — i.e. last** — because a failed substance write must not leave the node **unlinked**. Placed between content and links, a substance failure returns early and yields a created, content-bearing, **orphan** node; placed last, the same failure yields a fully-linked node that is merely missing its substance. In a graph store those outcomes are not close: the second is findable and repairable by one PATCH, the first is findable by nothing. *(QA #12175 CF-1(c); the implementer's original ordering had this backwards and was changed.)*

**State the position, not just the predecessor.** *“After the content POST”* is satisfied by a write placed between content and links — which conforms to that sentence while re-opening the orphan window. The load-bearing constraint is **last**.

**How the order is cited, and why not by line number.** The claim is *relative order*, so it is cited by the three call symbols and verified by one command that returns three ascending numbers for each of the four creators:

```
grep -n 'http_client.post_bytes(\|nodes/{node_id}/links\|write_substance(node_id' \r
     divoid-mcp/src/divoid_mcp/tools/create_{node,task,documentation,session_log}.py
```

→ `post_bytes` (content) **<** `post_json(.../links)` (links) **<** `write_substance` (substance), in all four files.

An earlier revision of this paragraph cited twelve absolute line numbers instead. **All twelve were low, and systematically so** — each content cite landed on the enclosing `try:`, each links cite on the `# --- Step 4:` comment, each substance cite on the blank line above the call (QA #12175 W-6). The *claim* was right and independently re-verified; only the addresses were wrong, which is the tell that it was one off-by-a-constant in how a position was read rather than twelve slips. They are removed rather than repaired: the numbers moved again while this correction was being written, so a line number is simply the wrong instrument for a fact about order. **Cite the symbol and the relation; let the command resolve the position.**

**“Silently lossy” is measured, not argued.** QA (#12175) tested whether an unknown parameter would fail loudly, *expecting* it to. It does not: passing a misspelled `subtance` through the real tool manager **returns success and silently discards the value**. So a caller cannot detect either failure mode — a discarded misspelling, or a cleared create-body write — from the tool's own response. That is the concrete reason the ordering had to be *fixed* rather than *documented*.

**Invariant compliance (#6105), stated because this package has been bitten here:**

- **Invariant 6 (no client vocabulary).** `substance` is passed through as an opaque string. **No length check, no "is this prose?" check, no normalisation, no rejection of any value the backend accepts.** The shape of a good substance is a client convention and the backend has no opinion; the wrapper must have none either.
- **Invariant 5 (guard before HTTP).** `clear_substance` participates in the existing `_check_invariants` `no_fields_to_patch` computation — a structural invariant (a PATCH with no ops is a no-op), not a vocabulary rule. Nothing new is guarded.
- **Invariant 4 (bytes not strings).** Does **not** apply: `substance` travels inside the JSON body of `POST /nodes` / `PATCH /nodes/{id}`, which `http_client.post_json` / `patch_json` already handle. It must **not** be routed through `post_bytes` — that path exists for the content blob, and re-using it would create the second content endpoint this design exists to avoid.
- **Invariant 3 (no retries)** and **1/2 (key containment, stderr)** are untouched.

**DRY check (#1267) — recomputed 2026-09-05 against the shipped diff; the original figure priced a mechanism that was rejected.**

The superseded math read *“2-line block × 4 sites = 8, below the ~15–20 threshold, inlined, no helper”*. It was arithmetically fine and rested on a false premise: that the create-body key was the mechanism. Once the write became a PATCH issued as a separate post-create step, the recurring block stopped being one dictionary key.

**Measured, both ways — and both cross the threshold, so the shared helper is required rather than optional:**

| What is counted | Figure | Source |
|---|---|---|
| Whole per-site delta in the four creators | **13 + 13 + 13 + 15 = 54** (13 at three of them; 15 at `create_session_log`, for its `_execute` / `register` split) | `git diff --numstat` on the four files against `d2b3288`; each also shows 1 deletion, the `_TOOL_DESCRIPTION` line that was re-terminated |
| The block the helper actually *removes* from each site — the guard, the patch call, the `isError` test and the `partial_state` envelope | **~14 non-blank lines × 4 sites = 56** | `_substance.py`, line 28 to EOF: 16 lines, 14 non-blank |

The **13 × 4 = 52** figure carried in QA #12175 is the first row rounded to a uniform per-site count; the implementer's *“~25 × 4 ≈ 100”* was an estimate rather than a measurement. **Whichever is used, state how it was counted** — all three land above the ~15–20 threshold and the conclusion is identical, but only a reproducible number survives being quoted downstream.

**So the shared helper is the #1267-mandated outcome here, not a stylistic preference**, and what remains inlined at each site is the three-line call-and-propagate: invoke `write_substance`, and return its error envelope when it is not `None`.

*Unchanged from the original note:* the surrounding `node_body` construction is *already* duplicated 4× (A15). This change does not extend that duplication and does not refactor it — a `node_body` builder is a separate cleanup with its own justification, and folding it in would make a two-sentence feature restructure four files.

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

*Scoped to §§1–16. The amendment makes its own, independently: §17.4's accepted cost — a byte-identical content write destroying a good substance.*

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
| **A staleness signal (hash or timestamp pair)** | Excluded by the go-ahead and not smuggled back in. The gap it leaves is real and measured (A11) — §15.1 surfaced it as a decision for Toni rather than designing it. **Settled 2026-09-05 by #11405, and not by either branch weighed here:** the ruling tracks nothing and destroys the stale value instead (§17). The cost argument against *tracking* recorded in this row still stands — it is why the ruling's route is the cheap one. |
| **Clear only when the content actually changed** *(§17.4)* | `UploadContent` does not read the existing content, so the comparison costs a full-blob read (or a second transmission of the blob so the DB can compare) on every upload — up to 195,448 B for the worst node in §12.1. Byte-equality is also the only comparison available without interpreting the content, so it cannot distinguish a cosmetic edit from a substantive one: it protects **only** a literal no-op write. And both write paths already rewrite `LastUpdate` and regenerate `Embedding` unconditionally, so a substance-only gate would leave one row with three disagreeing answers to "did content change?". Full reasoning and the falsifier: §17.4. |
| **A combined "write content + substance" verb** *(§17.5)* | Would mean a parameter or a second body on `POST /api/nodes/{id}/content` — the endpoint complication the first row of this table already rejects. The ordering rule — substance written **after the last content write**, and for a multi-step creator **last of all** (§17.5) — costs the filler one call it is already making. *(Phrasing corrected 2026-09-05: this row previously read “content, then substance”, the two-term form §16 now calls insufficient.)* |
| **Reporting the clear in the response** *(§17.6)* | `PATCH /content` already reports it for free — it returns `NodeDetails` and the key is simply absent. `POST /content` has no response body at all, and needs none *because the rule is unconditional*: a caller that got a 200 knows the substance is gone. A conditional clear would have needed a report, which is one more reason it lost. |

## 13. Risks & Mitigations

| # | Risk | Mitigation |
|---|---|---|
| R1 | **A new MCP requesting `fields=substance` runs against a backend that predates the column** → HTTP 400 on every list call (A5), not a silent degrade. | This is the concrete reason the backend PR must merge **and deploy** before the MCP PR. §14. The MCP change to `divoid_list` is documentation-only, so the blast radius is confined to callers who explicitly pass `fields=["substance"]`. |
| R2 | **`CreateOrUpdateSchema` adds the column destructively, or differently, on the deploy engine.** | Settled by A9a/A9b rather than by precedent. PostgreSQL — the deploy target — takes the `ADD COLUMN` branch by construction (`MustRecreateTable` → `false`), so there is no table copy, no extended lock and no row-count-dependent cost on the `node` table (**10,482 rows**, counted on the live instance 2026-09-05 — the review’s “11k+” was an estimate and this replaces it). SQLite rebuilds, but rebuilds on every startup already, so this change introduces no new behaviour there — measured row-preserving across three restarts. Residual verification in §16: run the suite, then start against a **copy** of `Backend/DiVoid.db3` and read a pre-existing node back. |
| R3 | **The INSERT column list and value list drift out of alignment** — `CreateNode` builds two positional lists (`NodeService.cs:121–122`); an added column with a mis-ordered value writes substance into the wrong field. | Positional, so the compiler will not catch it. Guarded by `CreateNode_WithSubstance_SubstancePersistedToDatabase` **plus** the existing create tests for `severity`/`rootNodeId`/`status`, which fail if the ordering shifts. This is the bug #157 trap the `Severity` commit explicitly called out. |
| R4 | **A future embedding change composes `substance` into the embedding input**, silently making it search-visible — the thing #11367 q2 deferred. | I3 is stated as an invariant in §9 and pinned by a named test in §16. `EmbeddingInputComposer.Compose` takes `(name, content, contentType)` and gains no parameter here. |
| R5 | **The MCP grows a length or shape check** on substance (invariant-6 violation, the `create_task._ALLOWED_STATUSES` shape). | §10 states the prohibition explicitly and §16 pins it with a test that a 50 KB substance passes through unaltered. |
| R6 | **Clients read substance and act as though it were content** (the §3 residual). | Out of the server's hands by construction; #11367 assigns the acceptance test to the filler. The server's contribution is I1 — never substitute — and the fact that `substance` and `content` are separate names on the wire. |
| R7 *(§17)* | **A third content-write path appears and does not clear.** The clear lives at the two statements that write `Content`; a new writer — or an `[AllowPatch]` added to `Node.Content` — would bypass it silently, leaving stale substance beside new content with no signal. | The `[AllowPatch]` route is put under a guard: **C8** asserts `PATCH /api/nodes/{id}` `replace /content` returns 400. A genuinely new statement is not test-detectable; it is detectable by the two greps in §17.2, which is why that inventory is written as a method rather than a list. |
| R8 *(§17)* | **A client writes substance before a content write**, losing the substance it just computed — and files it as a bug. | It is correct behaviour, stated in §17.5 and pinned by C1. The client-side rule is **substance after the last content write**. The live instance was the MCP's composite creators, which `POST /nodes`, then `POST /content`, then `POST /links`; they now write substance **last**, for two reasons — §17.5. |

## 14. Rollout / PR decomposition

**Two PRs, backend first — the design agrees with the operator's expectation, and R1 is the concrete reason rather than a convention.**

| PR | Contents | Blocked by |
|---|---|---|
| **PR 1 — backend** | `Node.Substance`, `NodeDetails.Substance`, `NodeMapper` mapping, `CreateNode` INSERT column, backend tests, this document | — |
| **PR 2 — divoid-mcp** | six-tool parameter/key additions, tool-description updates, smoke coverage | PR 1 **merged and deployed** (A5/R1) |
| **PR 3 — backend, clear on content write** *(amendment, §17)* | two `.Set(…)` terms, the `Node.Substance` doc-comment correction, the §17.8 tests, this section | PR 1 **merged** (done — `d2b3288`). Independent of PR 2. |

**PR 2 and PR 3 are independent and may land in either order** — they modify disjoint files (`divoid-mcp/` vs `Backend/`) and neither's tests depend on the other. **But PR 3 changed what PR 2 had to do**, and this is the one place the two met: a `create_*` tool that puts `substance` in the create body and *then* posts content would have had its substance cleared by PR 3, silently. The finding was routed to the implementer mid-task and **PR 2 shipped with the fix** — substance written by `PATCH` through a shared helper, **as the creator's last step, after content and after links**. The “after links” half is QA #12175's addition, not PR 3's: it keeps a failed substance write from leaving an orphan (§10, §16 PR 2 step 3, §17.5). No code for any of it lives in PR 3.

**No fourth unit falls out.** The change touches no other package: the frontend is out of scope, no CLI verb is affected (`CliDispatcher` declares no node-service dependency and no content command — verified by grep, not assumed), and nothing is being removed or refactored. The `node_body` duplication noted in §10 is pre-existing and is deliberately *not* bundled — bundling it would make a two-line feature restructure four files.

**No deploy hand-step — and that claim now rests on the deploy engine, not on the dev engine.** The column is added by the existing startup hosted service (A9). On PostgreSQL that is an `ALTER TABLE … ADD COLUMN` (A9a), so no table copy occurs and the `node` table (10,482 rows) costs no more than an empty one. There is no migration to run, no backfill to schedule, and no flag to flip. Rollback is `ALTER TABLE … DROP COLUMN`, or simply leaving an unused nullable column in place.

**Stated limit, and the input class that would falsify it.** A9a is read from `Pooshit.Ocelot` **source** and from that repository’s own Postgres test. It is **not** measured against a running PostgreSQL instance, because the only PostgreSQL carrying this schema is production and nothing here was run against production. The shape that would make it false: an Ocelot build whose `PostgreInfo.MustRecreateTable` does not return `false`. Postgres would then take the `RecreateTable` path (`SchemaService.cs:152`) — `ALTER TABLE node RENAME TO node_original`, create, copy 10,482 rows, drop — and “no hand-step” would have to be re-decided on lock duration and disk headroom. Two checks were run against exactly that class, because the clone read is `0.23.1-preview` while `Backend.csproj` references `0.23.0-preview`. **The window is pinned by the package, not by a guessed tag boundary:** the restored `pooshit.ocelot/0.23.0-preview/pooshit.ocelot.nuspec` records `commit="9594f9e…"` as the commit the shipped assembly was built from, so the window is `9594f9e..HEAD`. Over that window, **both checks are run against the class DiVoid actually executes**: (a) the live decision path — `SchemaService.cs:140–170`, spanning the `MustRecreateTable` call, the `ADD COLUMN` branch and the `UpdateIndices`/`UpdateUniques` calls — is **byte-identical** across the window (1,396 B, sha256 `9ed66e97873a614a…`); (b) **no commit changes a `MustRecreateTable` occurrence** anywhere under `Ocelot/`. The one commit in the window that modifies `SchemaService.cs` at all is `8f46e50`, whose only hunks are at `:213–220` inside `UpdateIndices` (adding `IF EXISTS` to a `DROP INDEX`) — more than forty lines past the decision region, and affecting neither the decision nor the `ADD COLUMN` lines. A9a is unaffected. The version gap is closed by measurement rather than assumed away. What would settle it outright: one startup against a **disposable** PostgreSQL holding an existing `node` table, comparing `relfilenode` before and after. That is cheap, and it belongs **before the deploy**, not before the merge — it gates nothing in PR 1.

## 15. Open Questions

**15.1 — SUPERSEDED 2026-09-05 by #11405. Answered, and by a third route neither option below considered.**

*What this section asked, kept verbatim because the reasoning is still load-bearing:* staleness — the go-ahead pushed it to the client, and the client could not detect it. `LastUpdate` bumps on node PATCH, content POST and content PATCH alike (A11), so it moves when substance is written and gives a client no way to ask *"has content changed since I wrote this substance?"*. The client's only remaining options were (a) re-fetch and hash the content, which is the byte cost the field exists to avoid, or (b) keep its own node-id-keyed record — the sidecar shape #11327 measured drifting silently over 12 of 25 rows. The position recorded here was *ship without it*, with the note that the cheapest form would be one additional nullable timestamp written when `Content` changes.

*How it was settled:* **neither (a) nor (b), and not the timestamp.** Toni's ruling **does not track staleness — it destroys the stale value.** The server clears `Substance` on every content write, and absence becomes the signal. This is a supersession, not a reversal: every cost this section measured for *tracking* still stands, and is precisely why the ruling's route is cheaper than the one this section named as the fallback. A11 stops mattering — a client never has to interpret `LastUpdate`, because it never has to ask the question. §17 is the design; PR #182's decision **D1** is superseded on the same terms.

**15.2 — Should the generation pass be able to find unfilled nodes?** A `noSubstance=true` list filter would answer *"which nodes still need substance?"* in one query. Its only caller is the generation pass, which is out of scope, so it is not designed. If Processor's harness is built and needs it, it is a ~12-line addition mirroring `NoSeverity` — worth a follow-up task, not worth predicting the shape of now.

**15.3 — `divoid_search` cannot return substance at all (A14), by design.** The workaround is one extra `divoid_list` call. If Processor measures that round trip as a real cost, `include_substance` on `divoid_search` is a 4-line change at `search.py:156–163`. Recommend filing it as a follow-up **after** a measurement, not before.

**15.4 — Empty string vs NULL. Half-answered 2026-09-05; the remaining half is still Toni's.**

*Answered by §17.3:* the **server** side is now decided and no longer free. The server writes NULL and never `""`, so "cleared" has exactly one server-produced spelling, and the client-side test is stated as a contract: **null, absent, or empty-or-whitespace-only all mean "no substance, regenerate"**. A `""` a client wrote itself is indistinguishable from a server clear, and §17.3 argues that nothing behavioural turns on the difference — with the falsifier that would change that.

*Still open, unchanged:* whether the server should **normalise a client's** `""` / whitespace-only write to NULL. §17 does not need it — the client-side test above covers it — and normalising remains server-side interpretation of a client-owned field. The original text stands:

§7 leaves `""` unnormalised. If Toni prefers one canonical “no substance”, the rule would be “the service maps whitespace-only to NULL on write” — one line, one test. Not built, because normalisation is server-side interpretation of a client-owned field. **What is now recorded rather than implied:** no test in §16 pins the unnormalised-storage property. QA measured this directly — a trim-plus-blank-to-null mutation of the mapper setter survives the whole suite 682/682, because every substance literal in the ten tests is already trimmed and non-blank and S4 exercises `null` rather than `""`. So “the server stores what it is given” is currently a **stated intent, not a guarded invariant**. If it is meant to be load-bearing it needs one test that writes a value normalisation would change (leading/trailing whitespace, or `""`) and asserts it comes back byte-identical; that test is what the surviving mutation would kill. **Not added to §16 unasked** — it is a decision, and it is Toni’s.

**15.5 — Nothing in this design is blocked.** John could implement PR 1 in full from §16 with no answer to any of the above, and can implement **PR 3** in full from §17 on the same terms. The one item that needs a decision before it is *built* is 15.4's remaining half — and §17 does not need it built.

## 16. Implementation Guidance for the Next Agent

### PR 1 — backend, in order

1. **`Backend/Models/Nodes/Node.cs`** — add `Substance` (`string`, `[AllowPatch]`, **no `[Index]`, no `[Size]`**). The XML `<summary>` says **what the field is, in one tight line, and nothing more**, per Code Contracts **#114 §4**, which routes rationale out of doc comments and into this document. The shipped line is the reference form: *“client-supplied compressed form of `Content`; null when unset, never written by the server.”* **Do not put “not embedded, not indexed” — or any other implementation choice — in the comment.** Those are rationale and they live here: §2 (embeddings out of scope), §8 (`Index: none`), §9 (I3).
   > **Correction 2026-09-05 (§17):** the trailing clause *“never written by the server”* becomes **false** the moment PR 3 lands — the server does write it, exactly once, as NULL. PR 3 replaces the shipped comment with: *“client-supplied compressed form of `Content`; null when unset; cleared by the server on every content write.”* Same discipline — what the field is, one line, no rationale.
2. **`Backend/Models/Nodes/NodeDetails.cs`** — add `Substance` (`string`). Doc comment names the `?fields=substance` opt-in for list responses and the unconditional presence on `GET /api/nodes/{id}`.
3. **`Backend/Models/Nodes/NodeMapper.cs`** — one `FieldMapping<NodeDetails, string>("substance", DB.Property<Node>(n => n.Substance, "node"), (n, v) => n.Substance = v)` in `Mappings()`. **Do not touch `DefaultListFields` (line 40). Do not touch `PostProcess`.**
4. **`Backend/Services/Nodes/NodeService.cs`** — `CreateNode` only: add `n => n.Substance` to `.Columns(…)` and `node.Substance` to `.Values(…)` **at the same position in both lists** (lines 121–122). No other method changes. Do not touch `Patch`, `UploadContent`, `PatchContent`, `GetNodeById`, `ListPaged`, `ListPagedByPath`, or any embedding path. *(That prohibition is scoped to **PR 1**. PR 3 amends `UploadContent` and `PatchContent` by exactly one term each — §17.7. `Patch`, `GetNodeById`, the list methods and every embedding path stay untouched in PR 3 too.)*
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
| S6 | `ListPaged_WithoutFieldsOptIn_OmitsSubstance` | **I4** — the default listing does not carry it | `substance` is added to `DefaultListFields` |
| S7 | `ListPaged_FieldsSubstance_ReturnsSubstanceAndNoContent` | **I1 + I2** — the requested field arrives and `content` does not | `substance` is aliased onto the content mapping, or `content` leaks into the row |
| S8 | `Patch_ReplaceSubstance_DoesNotAlterContent` (PATCH substance → GET `/content` byte-compare) | **I2** | any substance write path touches `Content` |
| S9 | `Patch_ReplaceSubstance_LeavesEmbeddingUntouched` — branch-level; shipped in `Backend.tests/Tests/NodeSubstanceEmbeddingIsolationTests.cs`, not in the HTTP file | **I3**; R4 | `TouchesName` is widened, or `substance` is composed into the embedding input |
| S10 | `CreateNode_WithLargeSubstance_RoundTripsUnaltered` (≥50 KB) | that **nothing in this repo's own code** truncates or rewrites a 51,200-byte value on the round trip | any of our create / map / read paths truncates or alters the value |

**Falsifier for this table as a whole:** any row whose named test would still pass against an implementation lacking the claimed property. S6 and S7 are the two that carry the design’s load — S6 is the only guard on I4, and it fails loudly the moment someone “helpfully” adds `substance` to the default field list.

**That falsifier was run against this table, and it caught two rows.** QA executed the mutation each row promises will kill it (review **#11379**). Eight rows held. Two did not, and both are corrected above rather than defended: S4 previously also claimed to catch server-side normalisation — a trim-plus-blank-to-null mutation survives 682/682, and §15.4 now records that property as unguarded; S10 previously claimed the bound-type property “on either engine” — `[Size(64)]` survives 682/682 because SQLite does not enforce `VARCHAR(n)` and the suite runs only on SQLite (its 9 Postgres tests skip). **The engine-side type-mapping axis is therefore untested by this suite by construction**, and what would test it is a Postgres-backed run of S10, not another SQLite assertion.

**Resolved-reference sweep, 2026-09-05 (#1220 §9 addendum 3, check 1).** Every test name cited in the **body** of this document — not in the supersession notes, which are records rather than claims — was extracted and resolved against `Backend.tests/`. **One did not resolve:** S6 was cited as `ListPaged_WithoutFieldsOpt_In_OmitsSubstance` while the shipped test is `ListPaged_WithoutFieldsOptIn_OmitsSubstance`. Corrected above. That is the "row that pointed at nothing" shape — it read as evidence, survived a full QA cycle, and cost one command to catch. The other nine S-rows resolve, as do the three precedent tests cited elsewhere in the body — two in A7, one in §7's Create paragraph — and the `PatchContentAsync` helper §17.8 points the implementer at. The eight C-rows in §17.8 are new by construction and resolve to nothing yet; what was checked for them instead is that none of the eight names collides with an existing test.

**Second pass, same day, over the §10 / §16 corrections.** The names those sections newly cite were resolved the same way, against `divoid-mcp/`: `write_substance` and `_substance.py` exist, and all five smoke functions named in §16 PR 2 step 6 — `smoke_patch_node_substance_lifecycle`, `smoke_patch_node_substance_verbatim`, `smoke_patch_node_substance_only_is_a_valid_patch`, `smoke_create_node_empty_substance_is_written`, `smoke_create_composites_substance_after_content` — resolve in `divoid-mcp/tests/smoke/run_all.py`. **A15's four line citations did not**, having moved when PR 2 landed; both sets are now recorded against their ref rather than one being silently replaced. That is the same defect shape S6 carried, found by the same command — which is the argument for running it rather than remembering to ask.

### PR 2 — divoid-mcp *(shipped; corrected 2026-09-05 to match)*

> **This section is a record of what shipped, not a forward plan.** PR 2 has landed. Step 3 previously read *“one optional param, one `node_body` key each”* — that mechanism was rejected before implementation (§10, §17.5) and is corrected below. A future MCP change will be briefed from this section, so it must describe the code that exists.

1. `get_node.py` — one returned key + description.
2. `patch_node.py` — `substance` / `clear_substance`, composed exactly like `severity` / `clear_severity`; both added to the `no_fields_to_patch` computation; the module’s supported-paths list gains a `/substance` entry in the same one-line form as its `/severity` and `/rootNodeId` neighbours. **Same discipline as PR 1 step 1: say what the parameter is, not why the design chose it.**
3. `create_node.py`, `create_task.py`, `create_documentation.py`, `create_session_log.py` — one optional `substance` parameter each, written by `write_substance(node_id, substance, config)` **as the creator's last step: after the content POST *and* after the link loop.** Both halves are load-bearing and come from different findings — after content because PR 3 clears substance on any content write (§17.5), after links because a substance failure must not leave the node an orphan (QA #12175 CF-1(c)). **“After the content POST” alone is not the constraint**: a write placed between content and links satisfies it and re-opens the orphan window. **Not a `node_body` key** for the same reason as (1): the create body is written before the content POST, and the loss is undetectable from the tool's response. `create_session_log` carries the parameter twice — once on `_execute`, once on the registered wrapper that passes it through.
   - `_substance.py` — new private module holding `write_substance`. It delegates to `patch_node._execute` rather than composing its own patch array, so the op vocabulary lives in exactly one place. On failure it returns a `partial_state` error envelope naming the surviving node id, matching the shape the content and link steps already use. Not a registered tool, so the new-tool sign-off gate is untouched.
4. `list_nodes.py` — `_TOOL_DESCRIPTION` only; **no code change**.
5. **No length check, no shape check, no normalisation anywhere** (invariant 6; R5).
6. Smoke coverage in `tests/smoke/run_all.py`. Shipped, and named rather than described: `smoke_patch_node_substance_lifecycle` (set → verify → survive an unrelated patch → `clear_substance` → verify null), `smoke_patch_node_substance_verbatim` (≥50 KB and `""` both round-trip unaltered — R5), `smoke_patch_node_substance_only_is_a_valid_patch`, `smoke_create_node_empty_substance_is_written`, and — the one that guards this section's correction — **`smoke_create_composites_substance_after_content`**, which is what fails if a creator ever moves the substance write back into the create body. Run the smoke suite in an **isolated venv only** — never `pip install -e .` into the environment backing the operational MCP (`divoid-mcp/CLAUDE.md`).

---

## 17. Amendment 2026-09-05 — the server clears `Substance` on every content write

Source: DiVoid task **#11405**. Toni, immediately after PR #182 merged:

> *"the backend should clear out substance when content is updated since substance would run stale otherwise - an empty substance is the signal for consuming clients to regenerate the substance because it is invalid by now."*

**The ratio, stated out loud before designing (RULING 2026-09-03, #1220).** One sentence of ask. The production change is **one assignment term added to each of two existing UPDATE statements** — no new method, type, statement, endpoint, parameter, column, response body, config knob or abstraction. If this section proposed a comparison, a helper, a report or a second column, that ratio would itself be the finding. Everything below is *decisions and guards*, not mechanism; the mechanism is §17.7 and it is two lines.

### 17.1 What this settles, and why it is not a reversal

§15.1 and PR #182's decision **D1** deferred staleness tracking on measured grounds: the field is client-filled, `LastUpdate` bumps on node PATCH, content POST **and** content PATCH alike (A11) so it cannot answer *"has content changed since I wrote this substance?"*, and the two remaining options were a hash column or a client-side sidecar — the shape #11327 measured drifting over 12 of 25 rows.

**The ruling takes a third route: it does not track the staleness, it destroys the stale value.** Absence becomes the signal. No hash, no timestamp pair, no second column, no client bookkeeping. D1 is **superseded, not reversed** — everything it measured about the cost of *tracking* still holds, and is exactly why this route is the cheap one.

### 17.2 The content-write inventory — enumerated, not accepted

The brief named two write paths and asked that the list be **confirmed rather than taken**. It was confirmed by enumerating every `Insert<Node>` and `Update<Node>` in `Backend/` — **17 sites**, 13 rows below because two rows bundle a branch set — and reading each one's `.Columns(…)` / `.Set(…)`:

| Site | Method | Columns written | Writes `Content`? |
|---|---|---|---|
| `Init/DatabaseModelService.cs:41` | sentinel backfill | `Created`, `LastUpdate` | no |
| `Services/Embeddings/EmbeddingBackfillService.cs:82` | backfill | `Embedding` | no |
| `Services/Layout/LayoutNodesService.cs:186` | layout | `X`, `Y` | no |
| `NodeService.cs:78` | `TryAnchorOrphanToPositioned` | `X`, `Y` | no |
| `NodeService.cs:120` | `CreateNode` (INSERT) | `TypeId, Name, Status, Severity, RootNodeId, Substance, X, Y, OwnerId, Access, Created, LastUpdate` | **no** — see below |
| `NodeService.cs:149` | `CreateNode`, name embedding | `Embedding` | no |
| `NodeService.cs:976` | `Patch` (`[AllowPatch]` set) | whatever the patch names, gated | **no** — see below |
| `NodeService.cs:986` | `Patch`, retype | `TypeId` | no |
| `NodeService.cs:995` | `Patch`, timestamp | `LastUpdate` | no |
| `NodeService.cs:1073 / 1085 / 1093 / 1102` | `RegenerateEmbeddingViaBranches` f1–f4 | `Embedding` | no |
| **`NodeService.cs:1167`** | **`UploadContent`** | `ContentType`, **`Content`**, `LastUpdate` | **yes** |
| `NodeService.cs:1201 / 1208` | `RegenerateContentEmbedding` | `Embedding` | no |
| **`NodeService.cs:1236`** | **`PatchContent`** | **`Content`**, `LastUpdate` | **yes** |

**Two sites, and each is reachable from exactly one route.** Walked from the consumer side rather than inferred:

| Route | Controller | Service contract | Statement |
|---|---|---|---|
| `POST /api/nodes/{id}/content` | `NodeController.cs:180–187` | `INodeService.UploadContent` (`:86`) → `NodeService.cs:1155` | `:1167–1171` |
| `PATCH /api/nodes/{id}/content` | `NodeController.cs:200–207` | `INodeService.PatchContent` (`:106`) → `NodeService.cs:1216` | `:1236–1239` |

No other controller in `Backend/Controllers/` declares a `content` route, and `INodeService` declares no third content-writing member.

**`PATCH /api/nodes/{id}` cannot reach content — verified at the gate, not assumed.** `DatabasePatchExtensions.Patch` lowercases the patch path, resolves it **case-insensitively** against the entity's properties, and throws `NotSupportedException` (→ 400) unless the resolved property carries `[AllowPatch]`. That check sits **above the op switch**, so it gates the custom `embed` op exactly as it gates `replace`/`add`/`remove`/`flag`/`unflag` — there is no op that bypasses it. `Node.Content` carries no `[AllowPatch]` (`Node.cs:39–42`). So `/content`, `/Content` and `/CONTENT` all return 400 and none reaches the column. (`DatabasePatchExtensions.cs:41–48`.) **This fact was unguarded** — no test in `Backend.tests/` asserts it — and C8 in §17.8 closes that, because the design's completeness depends on it (R7).

**`POST /api/nodes` is not a content-write path either.** `CreateNode`'s INSERT column list (`:121`) does not include `Content`; a `content` value in the create body is not persisted by that statement. A create that supplies `substance` therefore needs no clear, and none is added.

**What would falsify this inventory:** a new `Insert<Node>`/`Update<Node>` site whose `.Set(…)` assigns `Content`, or an `[AllowPatch]` added to `Node.Content`. The second is under a guard (C8); the first is not test-detectable and is detectable only by re-running the two enumerations above — which is why this section is written as a method rather than as a list of two.

### 17.3 Decision A — the server writes NULL, never `""`

**The server writes NULL.** Reasons, in order of weight:

1. **NULL is already the canonical absent state** in this design (§7 Delete, §8 `Default: NULL`) and in the two precedents `severity` and `rootNodeId`. `""` would be a second, server-invented spelling of a state that already has one.
2. **NULL is omitted from the JSON**; `""` serialises as `"substance":""`. A clear that produced `""` would make a cleared node look *populated* to a client testing key presence — the exact reading the ruling depends on.
3. **Writing `""` is server-side interpretation**, in the same class §15.4 excluded — just pointed the other way.

**What a client must test, stated as a contract because clients now key on it:**

> **`substance` absent, null, or empty-or-whitespace-only ⇒ no substance; regenerate.**

Toni's words — *"clear out"*, *"an empty substance"* — do not distinguish the two spellings, and this rule makes the distinction unnecessary rather than resolving it by fiat. Concretely:

| Spelling | Who can produce it | What the client does |
|---|---|---|
| key absent / `null` | the server's clear; a node that never had one; a client's `replace /substance` with `value: null` | regenerate |
| `""` or whitespace-only | **only a client**, writing it itself (§7 stores it verbatim) | regenerate |

**A client-written `""` is not distinguishable from a server clear, and that does not matter** — because there is no behaviour that would differ. "Never had a substance", "had one, content changed, cleared", and "a client wrote an empty one" all mean *generate one*.

**Falsifier — the input class that would make that false:** a consumer that needs to *prioritise* re-generation over first-generation (or the reverse), or to report how many substances went stale. No such consumer exists, and none is named in #11405, #11367 or #11366. If one appears, the answer is still not a distinguishable empty state — a third value would be a staleness marker by another name, which is what D1 excluded — it is §15.2's `noSubstance` filter, which collapses both spellings anyway.

**Not normalised.** The server does not map a client's `""` to NULL. The client-side rule above makes normalisation buy nothing, and §15.4's reasoning against interpreting a client-owned field is unchanged.

### 17.4 Decision B — unconditional, on both paths

**The clear fires on every successful content write, including one whose bytes equal what was already stored.**

**What is already read on each path — measured, because the brief's premise turned out to be false for one of them:**

| Path | Reads existing `Content`? | Evidence |
|---|---|---|
| `UploadContent` | **No.** Its only node read is the `n.Name` SELECT inside `RegenerateContentEmbedding`, and even that is skipped when the embedding capability is off. The blob it writes comes from the request body. | `NodeService.cs:1155–1176`; the SELECT at `:1195–1197`, behind the early return at `:1191–1192` |
| `PatchContent` | **Yes** — it loads `ContentType` and `Content` inside the transaction, so old and new arrays are both in memory when the UPDATE is built. | `NodeService.cs:1225–1227`, `:1233` |

So the comparison is nearly free on one path and genuinely expensive on the other. Three arguments settle it:

1. **Cost, where it is not free.** A conditional on `UploadContent` needs either a new SELECT of the whole existing blob — up to **195,448 B** for the worst node measured in §12.1, read on every upload, for a value nobody wants — or a second WHERE-gated statement that transmits the blob a second time so the database can compare it. Both pay real bytes, forever, on the path that has none of them today.
2. **The comparison cannot discriminate what the worry is about.** Byte equality is the only comparison available without interpreting the content, and a typo fix, a reformat and a rewrite all produce different bytes. **The conditional therefore protects exactly one case: a write that changed nothing at all** — whose only other effect is a `LastUpdate` bump. The brief's concern that `PatchContent` "is a partial editor whose whole purpose is small edits" is real and the conditional does not address it: small edits *do* change bytes and *do* clear the substance, by design. A bytes-only comparison would also let a re-upload of identical bytes under a **different `ContentType`** keep its substance, which is wrong for a different reason — so the conditional would need two comparisons, not one.
3. **Internal consistency, which is decisive.** Both paths already treat *every* write as a change, for two other fields: `LastUpdate` is bumped unconditionally on both, and `RegenerateContentEmbedding` is called unconditionally on both — on the deploy engine (PostgreSQL, where the capability is enabled) that is a real embedding regeneration on a byte-identical write. Gating `Substance` alone would leave one row with **three disagreeing answers** to "did content change?": `LastUpdate` says yes, `Embedding` says yes, `Substance` says no.

**The cost accepted, named:** a byte-identical content write destroys a good substance. Its incidence is an idempotent re-upload of unchanged bytes, or a re-applied range edit whose replacement equals what is already there.

**Falsifier — what would make "that cost is negligible" false:** a client whose *normal* operating mode re-writes identical content — a sync loop that POSTs the whole body on every save, or an editor that saves on blur. **If such a client appears, the fix is not a substance-only conditional.** It is a no-op short-circuit at the top of the write path — skip the UPDATE, the `LastUpdate` bump and the embedding regeneration together — which protects all three fields, pays for its comparison with a saved embedding call, and would then carry the substance clear with it. That is a separate task with a different justification; it is named here, not designed, and premise 3 above moves with it.

**Pinned by C4 and C5**, so a later "optimisation" cannot reverse this decision silently.

### 17.5 Decision C — ordering

**Within the request: the same statement.** The clear is an additional assignment in the UPDATE the path already issues — not a second statement before or after it. Three consequences, all of them properties rather than preferences:

- The row is never observable with new content beside old substance; there is no intermediate state to order.
- `UploadContent`'s UPDATE carries the write-visibility gate (`predicate.Content`) and throws `NotFoundException` when it affects zero rows. Adding a term to the SET does not change which rows the WHERE matches, so that behaviour is unchanged — and **the clear cannot happen without the content write, by construction**, because they are one statement. *(Relevant to C4: a byte-identical re-upload still reports one affected row, because SQLite and PostgreSQL both count rows matched rather than rows whose values differed — and `LastUpdate` moves on every write regardless, so the row differs even under a "changed rows" counter. That is pre-existing behaviour of this statement, not something the added term introduces.)*
- Each of the two statements sits inside its own method's existing `Transaction`, so a later failure — the embedding regeneration throwing, or the cancellation token firing — rolls the clear back with the content write. That is **I5**, pinned by C7.

*No coverage row pins "same statement".* Two statements inside the same transaction would be equally correct; one statement is a KISS choice, and a row claiming to guard it would be a wish.

**Across requests: substance after the last content write.** A client that writes substance and *then* writes content loses the substance. **That is correct**, and it is stated here so nobody files it as a bug later: the clear is keyed on the content write, not on any comparison of what the substance describes, and the server cannot know that a substance written a moment ago already accounts for the content arriving next. **Phrase it as a position rather than a predecessor** — *“second”* is true only of a two-step client, and a client with more steps needs to know which one it must follow. For the MCP's creators that position is *last*, and for a second reason as well; see below.

**This hazard was live in code that already existed, and the finding changed PR 2 mid-flight.** The MCP's composite creators (`create_node`, `create_task`, `create_documentation`, `create_session_log`) `POST /nodes`, *then* `POST /nodes/{id}/content`, *then* `POST /nodes/{id}/links` ×N (A15). §10 as originally written put `substance` into the create body — which PR 3 would clear on the very next call, with the caller told only that the create succeeded. **That is now fixed in the shipped code**: each creator issues a `PATCH /nodes/{id}` `replace /substance` through a shared `write_substance` helper, **as its last step** — after content *and* after links.

**The “after links” half is not this design's finding, and it is the half more easily lost.** PR 3's constraint is only *after content*, because that is all the clearing behaviour implies. QA #12175 CF-1(c) supplied the second: a substance write placed between content and links returns early on failure and leaves a created, content-bearing, **unlinked** node — an orphan, which in a graph store is worse than the alternative it was guarding against. Placed last, the same failure leaves a fully-linked node missing only its substance: findable, and repairable by one PATCH. **So a document that says only “after the content POST” is satisfied by the wrong placement.** Name the position — *last* — not just the predecessor. §10 and §16's PR 2 steps were corrected to match (2026-09-05); the DRY math moved with the mechanism. The MCP remains out of this document's scope (§2) — what belongs here is the constraint this design imposes, the constraint QA added beside it, and the fact that both were applied.

**And the loss really is undetectable from the tool surface.** QA tested whether an unknown parameter would fail loudly, *expecting* it to: a misspelled `subtance` passed through the real tool manager **returns success and silently discards the value**. So neither failure mode — a discarded misspelling, or a create-body write cleared a moment later — surfaces in the response. That is measured, and it is why the ordering had to be fixed rather than documented.

**Rejected: a combined "write content and substance" verb.** It would mean a parameter or a second body on the content endpoint — the endpoint complication §12.4's first row already rejects — to save the filler one call it is already making.

### 17.6 Decision D — the response reports it where it can, and needs no new mechanism

| Endpoint | Returns today | Does the caller learn about the clear? |
|---|---|---|
| `PATCH /api/nodes/{id}/content` | `NodeDetails`, re-read via `GetNodeById` after commit (`NodeService.cs:1244`) | **Yes, already.** `GetNodeById` projects the whole mapper vocabulary (A1) and nulls are omitted, so the `substance` key is simply absent from the response. Zero code. |
| `POST /api/nodes/{id}/content` | nothing — the action returns `Task` (`NodeController.cs:182`) | **No, and it needs no report.** |

#11405's decision 4 invokes PR #182's own lesson — *defaulting alone makes the node correct while only reporting it lets the caller tell*. **It does not bind here, and the reason is the same reason B went the way it did.** That lesson applies where the server made a choice the caller could not predict. Here the rule is **unconditional**: a caller that received a 200 from the content POST knows the substance is gone, without being told. Adding a response body to an endpoint that has never had one — changing the wire for every existing caller — to restate a rule that has no branches is a mechanism with no information in it.

**Note the interlock:** a *conditional* clear **would** have needed a report, because the caller could not tell whether it fired, and that report would have to be a new response body on `UploadContent`. The two decisions are not independent; unconditional is what makes "no new reporting" honest.

### 17.7 Decision E — the clear rides the content UPDATE, not the embedding path

**The change, precisely.** Each of the two content-write statements gains one assignment naming `Substance` and a null string constant, in the cast-to-null form the same file already uses for `Embedding` at `:1103` and `:1209`:

- `NodeService.cs:1168` (`UploadContent`) — a fourth assignment beside `ContentType`, `Content`, `LastUpdate`.
- `NodeService.cs:1237` (`PatchContent`) — a third assignment beside `Content`, `LastUpdate`.

Nothing else in either method changes. `RegenerateContentEmbedding` is **not** modified: its parameters remain name, content and content type, it gains no substance parameter, and `EmbeddingInputComposer.Compose` is untouched. **I3 holds by construction** — the clear writes a column, it does not feed the embedding input, and the substance is not read on either path.

**The DRY-shaped trap, named so it is not walked into.** `RegenerateContentEmbedding` is the helper *both* content-write paths already call, so it looks like the one-site home for a clear that belongs on both. **It is the wrong home, and the failure is silent and engine-dependent:** it returns early when `embeddingCapability.IsEnabled` is false (`:1191–1192`), which is the case on SQLite and on any deployment with embeddings off — so the clear would simply never happen there. The test engine is SQLite, so **C1 and C2 fail against that mutation**, loudly, on the default suite run. It is caught, but it should not be reached for in the first place.

**DRY math (#1267):** the clear is a **1-line block × 2 sites = 2**, far below the ~15–20 threshold. Inlined at both sites; no helper extracted, and none would be justified by two lines even if the shared helper above were a safe home.

**Everything else is inherited and unchanged:** authorization (a caller that may write content may clear substance — same gate, no new branch); observability (no new log line, and the substance value is never logged, §11.4); error handling (no new exception type); idempotency (a repeated content POST still ends at the same state — content stored, substance NULL).

### 17.8 PR 3 — implementation and coverage

**In order:**

1. **`Backend/Services/Nodes/NodeService.cs`** — add the `Substance`-to-null assignment to the `UploadContent` UPDATE (`:1168`) and to the `PatchContent` UPDATE (`:1237`). **Nothing else.** Do not touch `Patch`, `CreateNode`, `GetNodeById`, the list methods, `RegenerateContentEmbedding`, `RegenerateEmbeddingViaBranches`, or `EmbeddingInputComposer`.
2. **`Backend/Models/Nodes/Node.cs`** — replace the `Substance` doc comment's now-false trailing clause per §16 step 1's correction note. One line, no rationale.
3. **`Backend/Models/Nodes/NodeDetails.cs`** — the DTO comment (`:56–58`) says only where the field appears on the wire and is still true; leave it, unless the implementer judges a "cleared on content write" clause belongs there, in which case it is one clause and no rationale.
4. **`Backend/Controllers/V1/NodeController.cs`** — one clause in the XML `<summary>` of each content action (`:172–177` upload, `:189–195` patch) stating that the node's substance is cleared. **This is not optional documentation:** those summaries are the endpoints' published contract, and a side effect a caller must react to belongs where the caller reads the endpoint. **Both summaries already carry side-effect clauses of exactly this shape** — the upload one describes the per-engine embedding behaviour across three lines (`:174–176`), the patch one says *"on Postgres the embedding is regenerated in the same transaction"* (`:194`) — so this adds a clause to an existing list rather than opening a new register. Unlike the embedding clauses, the substance clause carries **no engine qualifier**: the clear is part of the content UPDATE and fires on SQLite and PostgreSQL alike. No signature, attribute or body change in this file.
5. **Tests** — table below. The HTTP rows go in the existing `Backend.tests/Tests/NodeSubstanceHttpTests.cs`, reusing its `CreateNodeAsync`, `UploadTextAsync`, `GetWithRawAsync` and `PatchAsync` helpers; C7 goes in the existing `Backend.tests/Tests/NodeSubstanceEmbeddingIsolationTests.cs`, which already carries the enabled/disabled capability fixture. One new local helper is needed — a `PATCH /content` sender mirroring `NodeContentPatchHttpTests.PatchContentAsync`. **DRY math: ~8 lines × 2 sites = 16**, at the low end of the threshold and spread across two independent `WebApplicationFactory` fixtures — inlined; a shared test-helper class would be a new type for a two-line feature.
6. **Verify** — `dotnet test Backend.tests/Backend.tests.csproj`. No schema change, so none of §16 step 6's startup verification applies: this PR adds no column and touches no migration path.

Files **not** in PR 3: `NodeMapper.cs`, `NodeFilter.cs`, `INodeService.cs`, `DatabasePatchExtensions.cs`, `DatabaseModelService.cs`, `ContentEditor.cs`, anything under `Services/Embeddings/`, anything under `divoid-mcp/` or `frontend/`.

**Coverage. Each row names the test, not a mechanism.**

| # | Test | Pins | The premise that makes it discriminate | Fails when |
|---|---|---|---|---|
| C1 | `UploadContent_ClearsSubstance` | **I5** on the upload path | asserts the substance is **present** before the upload, so a missing mapper mapping cannot make it pass vacuously; asserts the raw JSON carries no `"substance"` **key**, which is the S4 idiom and is what separates NULL from `""` | the clear term is dropped from `:1168`; **or** the clear writes `""` instead of NULL; **or** the clear is folded into `RegenerateContentEmbedding`, whose capability early-return fires on the SQLite test engine |
| C2 | `PatchContent_ClearsSubstance` | **I5** on the patch path | same before/after shape; the edit is a real change, so this row is about the clear and not about no-op handling | the clear term is dropped from `:1237`, or written as `""`, or folded into the embedding helper |
| C3 | `PatchContent_Response_OmitsClearedSubstance` | §17.6 — the PATCH response reports the clear with no new mechanism | asserts on the **PATCH response body**, not on a follow-up GET | `PatchContent` stops re-reading the row for its return value (e.g. returns a `NodeDetails` assembled before the write). C2 would still pass; this row would not |
| C4 | `UploadContent_WithByteIdenticalContent_StillClearsSubstance` | §17.4 — the clear is **unconditional** | the order is load-bearing: upload content, *then* write the substance, *then* upload the **same bytes again**. Only an unconditional clear can fire on that second upload. Writing the substance before the first upload would have it cleared by that upload and the row would prove nothing | anyone adds a change-comparison to the upload path |
| C5 | `PatchContent_WithNoOpEdit_StillClearsSubstance` | §17.4 on the path where the comparison is cheap and therefore tempting | the edit replaces one line with its own current text. Verified against `ContentEditor.Apply`: it decodes, splices `[offsets[start], offsets[end])` with the supplied value, and re-encodes the whole string — so replacing a line with itself yields byte-identical output, and `Apply` never returns the original array by reference | anyone adds a change-comparison to the patch path |
| C6 | `Patch_ReplaceName_LeavesSubstanceIntact` | the clear is keyed on **content** writes, not on any write | a name PATCH runs through `NodeService.Patch`, which executes fully on SQLite | the clear is placed on the shared node-PATCH path (`:995`) or inside `DatabasePatchExtensions`. **Bounded claim:** this row does *not* cover a clear placed inside `RegenerateEmbeddingViaBranches` — `nameTouched` requires `embeddingCapability.IsEnabled`, so that path is unreachable on the SQLite test engine and **no SQLite assertion can redden it**. That axis is untested by this suite by construction, exactly as §16's Postgres note records for S10 |
| C7 | `UploadContent_WhenEmbeddingRegenerationFails_LeavesSubstanceIntact` | **I5's atomicity half** | built like the shipped S9: seed with a `DisabledCapability` service, then call `UploadContent` on one constructed with an **enabled** capability against the same SQLite fixture — it reaches the Postgres-only `embedding` function and throws, so `transaction.Commit()` is never reached. **The seeded node must carry a name and text content type**, or `EmbeddingInputComposer.Compose` returns null, the `Embedding = null` branch runs cleanly on SQLite, nothing throws and the row proves nothing. The test asserts the throw **first**, which is the scenario check S9's own comment documents | the clear is issued outside the transaction (no transaction handle) ahead of the content UPDATE: the content write rolls back and the clear does not. The row reddens either by the substance assertion or by the transaction error the mutation itself raises |
| C8 | `Patch_ReplaceContent_IsRejected` | **R7** — `PATCH /api/nodes/{id}` is not a content-write path, so §17.2's inventory is closed at two | asserts the **status code** (400). The `[AllowPatch]` check at `DatabasePatchExtensions.cs:47–48` runs *before* the value conversion at `:50–54`, so the patch value can be any string and no `byte[]` conversion question arises. The accompanying "content and substance unchanged" assertion is documentation, not the guard | `[AllowPatch]` is added to `Node.Content`: the PATCH returns 200 and a third content-write path exists with no clear |

**Falsifier for this table as a whole:** any row whose named test would still pass against an implementation lacking the claimed property.

**Run over the column, before shipping (#1220 §9 addenda 1–3):**

- **No row is a grep.** All eight assert behaviour through the HTTP or service surface, so none can fire on a compliant implementation merely because it *spells* something.
- **No row fires on compliant code.** C8's compliant outcome is 400 and it asserts 400; C7's compliant outcome is a rollback and it asserts the rollback; C1/C2 assert key-absence, which compliant code produces.
- **The vacuous-pass hole is closed explicitly.** *Without* their before-assert, C1 and C2 would pass against an implementation carrying no `substance` mapping at all — the key would be absent for the wrong reason, which is a green row proving nothing. *With* it they cannot, and that premise is written into the row rather than left to be inferred by whoever writes the test.
- **Check 1 (every named guard exists) cannot be run yet**, because all eight are new. What *was* run: none of the eight names collides with an existing test in `Backend.tests/`. Check 1 becomes runnable the moment PR 3 exists, and is QA's.
- **Two claims are bounded rather than universal, and say so:** C6 (does not reach the name-embedding branch on SQLite) and the note that no row pins "same statement" (§17.5).

### 17.9 Pre-Design Checklist (#1136 §5) — delta for this amendment

§12.2 answers the checklist for §§1–16. Only the rows this amendment moves:

- **KISS** — two production lines; no new element to delete, merge or inline. The one element that *was* deleted is the conditional (§17.4), and the second is the response body (§17.6).
- **DRY** — 1 line × 2 sites = 2, inlined (§17.7). The tempting shared helper is named and rejected on correctness, not on style.
- **YAGNI** — no seam is built for the staleness question; it is **answered**, not deferred. No normalisation, no marker, no filter, no report.
- **Existing systems first** — the change lands entirely inside two statements that already exist; no new layer is proposed, so #1136 §2's bar is not reached.
- **Configurability** — no knob. In particular the clear is **not** made switchable: a toggle would mean clients could not rely on absence, which is the whole signal.
- **Data deliverables** — none. No column, no migration, no backfill; existing rows are untouched and stay as they are until their next content write (backfill is out of scope per §2 and #11405).

### 17.10 What §17 supersedes

| Location | Status |
|---|---|
| §15.1 (staleness deferral) | **superseded** — rewritten in place; the reasoning is preserved because it still holds for *tracking* |
| PR #182 decision **D1** | **superseded** on the same terms |
| §8 "independent lifecycle: writing one never reads or writes the other" | **corrected** — the content→substance direction is no longer independent |
| §16 step 1's reference doc-comment (*"never written by the server"*) | **corrected** — PR 3 replaces the clause |
| §12.4's staleness row | **annotated** — the alternative it rejected is still rejected; the ruling is a third route |
| §16 S6's cited test name | **corrected** — it pointed at nothing (see the resolved-reference sweep above) |
| §10's four `create_*` rows and its DRY math | **corrected 2026-09-05 (QA #12175)** — they prescribed a `node_body["substance"]` create-body key at 2 lines × 4 sites = 8. That mechanism was rejected because of §17.5, and the shipped shape is a `write_substance` PATCH issued as the creator's last step — after content and after links; the recomputed math is 54 (per-site delta) or 56 (the block the helper removes), either of which makes the helper required |
| §10's `get_node` / `patch_node` line estimates | **corrected on the figures only** — both named the shape that shipped; "+6" for `patch_node` measured 16, because the parameter pair recurs across three signatures |
| §16's PR 2 step 3 and step 6 | **corrected** — step 3 described the rejected create-body key; step 6 described smoke coverage in prose and now names the five shipped smoke functions, including the one that guards the ordering |
| A15's line citations | **re-resolved** — the fact holds, the four line numbers moved when PR 2 landed; both sets are recorded against their ref |
| Every two-term ordering sentence — the TL;DR, §10 (table row and the why-PATCH paragraph), §12.4's combined-verb row, §13 R8, §14, §16 PR 2 step 3, and §17.5 | **corrected 2026-09-05 (QA #12175 CF-1(c), then W-7)** — each stated a *sufficient* condition where the shipped code depends on a *stronger* one. The write moved to the creators' **last** step, after the link loop, so a substance failure cannot leave an orphan. Every citation in those sentences still resolved, which is why a resolution check could not find this: **the defect was in the strength of the claim, not in its references.** Ordering constraints in this document now name the position, not the predecessor |
| The twelve content/links/substance line citations in §10, and A15's post-PR-2 set | **removed, not repaired, 2026-09-05 (QA #12175 W-6)** — all twelve were low and *systematically* so: each content cite on the enclosing `try:`, each links cite on the `# --- Step 4:` comment, each substance cite on the blank line above the call. The order they asserted was correct and independently re-verified; only the addresses were wrong. Replaced by a symbol-and-relation citation with the command that resolves it — which was the right call rather than a tidier one, because the numbers moved twice more while this correction was being written |
| … and this row's own account of which sections were affected | **wrong twice before it was right, and left visible because it is the argument for the rule.** Draft 1 listed “§12's PR table” from memory — wrong. Draft 2 replaced it with “§12 carries no ordering sentence at all” — **also wrong**, and worse, because it was written *as* the lesson about grepping rather than reading, and was itself asserted from reading. The truth is one row: §12.4's combined-verb line, which said *“content, then substance”*. QA found it (W-7); a grep for the ordering vocabulary finds it in one command, and that grep is what produced the list above | **corrected 2026-09-05 (QA #12175 CF-1(c))** — each stated a *sufficient* condition where the shipped code depends on a *stronger* one. The write moved to the creators' **last** step, after the link loop, so that a substance failure cannot leave an orphan. Every citation in those sentences still resolved, which is exactly why a resolution check could not find this: **the defect was in the strength of the claim, not in its references.** Ordering constraints in this document now name the position, not the predecessor |
| §2, §6, §7, §9, §13, §14 | **extended** — scope row; a `UploadContent`/`PatchContent` responsibility row; the server-initiated clear verb; I5/I6 plus the two content-endpoint contract rows; R7/R8; PR 3 |
| §5's diagram (*"Content ── untouched"*) | **still true as drawn, and extended** — it depicts the substance-write paths, where no write touches `Content` (I2). §5 now carries a second fragment for the content-write direction, which is where the only new arrow goes |
| Everything else in §§1–16 | **unchanged and still current** |

---

*Design: sarah-software-architect. §§1–16: 2026-09-05, task #11367 (shipped as PR #182). §17 and the corrections it names: 2026-09-05, task #11405. Repo path: `docs/architecture/node-substance.md`.*
