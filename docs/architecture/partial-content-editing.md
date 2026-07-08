# Architectural Document: Partial Node-Content Editing (JSON-Patch-explored, `PATCH /content` chosen)

> Source task: DiVoid #6283. Design Contracts (#1136) and Backend Code Contracts (#114) are load-bearing.
> Follow-up (out of scope here): MCP tools to drive this surface — Alex.

## Problem Statement

Agents avoid editing large content nodes because the only mutation path today is a **wholesale**
replace (`POST /nodes/{id}/content` → `NodeService.UploadContent`, or the deprecated `embed` patch op).
Rewriting a whole 500-line document to change five lines risks clobbering the other 495. We want
**partial edits** — "replace lines 7–11 with this", "append this", "replace characters 300–660 with
this", "delete this range" — so that a small change is expressed as a small, bounded operation that is
*safer* than a full rewrite, not more dangerous.

User's verbatim ask (DiVoid #6283):

> "add something like an 'edit node content' — like replace line 7-11 with this content, append the
> following content — replace character 300-660 with the following content and so on. If possible with
> the existing vocabulary it would be really nice if this is representable by json patches - this way we
> wouldn't need to invent funky new endpoints. Send sarah to find a clean kiss way in json patch - if not
> possible new endpoints are okay, but explore the option first. And then add some clear methods to the
> mcp to edit content."

Success criteria: (1) partial edits by line range and character range; (2) bounded, fail-loud behavior
that cannot silently corrupt; (3) prefer the existing JSON-Patch vocabulary if it fits cleanly; (4) a
clear surface an MCP layer can wrap with named verbs.

## The KISS Exploration (done first, per the ask) — can existing JSON Patch express this?

DiVoid's patch surface is `PatchOperation[]` (`{op, path, value}`) routed through
`DatabasePatchExtensions.Patch`. Three facts about how it actually works (verified in code, not assumed):

1. **It is a property setter, gated by `[AllowPatch]`.** `patch.Path[1..]` is resolved to an entity
   *property*; a property without `[AllowPatch]` throws. `Node.Content` (`byte[]`) is deliberately **not**
   `[AllowPatch]`, and making it so would only expose *wholesale* replace via generic patch — it does
   nothing for sub-range editing.
2. **It executes entirely SQL-side, with no read-modify-write in .NET.** Each op becomes a `SET`
   fragment built from Ocelot expression trees (`DB.Property`, `DB.Constant`, `DB.CustomFunction`). There
   is no point at which the current value is materialized, decoded, spliced, and written back.
3. **JSON-Pointer paths address JSON-document nodes.** RFC-6902 addresses locations within a JSON
   document. `Node.Content` is an opaque UTF-8 text/byte blob, not a JSON document; a pointer like
   `/content/lines/7` is not a natural JSON-Pointer — it would be a range grammar smuggled into a path
   string and re-parsed.

Sub-range text editing is fundamentally a **read → decode (UTF-8) → splice in line/character space →
re-encode → write** cycle. That is a different execution model from the SQL-side property setter. Three
options were weighed:

| Option | Shape | Verdict |
|---|---|---|
| **(a) Custom op on generic `PATCH /nodes/{id}`** with a `/content` path carrying range semantics | `{op:"splice", path:"/content", ...}` mixed into the property-patch array | **Rejected.** Forces two execution models into one handler; `[AllowPatch]` is a property gate, not a sub-range gate; content edits need read-modify-write + embedding regen that the SQL-set path has no hook for. This is *more* complex, not less. |
| **(b) Splice op inside `DatabasePatchExtensions`** doing UTF-8-aware splicing in SQL (`overlay`/`convert_from`) | new op in the patch engine | **Rejected.** SQL string/byte splice is dialect-divergent (SQLite has no `overlay`), and byte-space `overlay` cannot honor code-point/line boundaries — the exact corruption we must prevent. |
| **(c) Dedicated `PATCH /nodes/{id}/content`** taking an ordered edit-list, executed as a service-layer read-modify-write | new HTTP verb on the **existing** content sub-resource | **Chosen.** |

**Verdict: a dedicated `PATCH /nodes/{id}/content` endpoint — but the request body keeps the
JSON-Patch spirit** (an ordered array of operations). This honors the user's real goal (a familiar,
list-of-edits vocabulary; no *funky* endpoint) while refusing the mechanical mis-fit of routing UTF-8
range splicing through a SQL property-setter. It is not a funky new endpoint: `/nodes/{id}/content`
already answers `GET` (read) and `POST` (wholesale set). `PATCH` is the *missing, semantically-correct*
verb on that same sub-resource — "partially modify the content" is exactly what HTTP `PATCH` means. The
"no new endpoint" half of the wish cannot be satisfied cleanly; the "representable as a list of
operations" half is satisfied fully.

## Scope & Non-Scope

**In scope (this PR — design + first backend increment):**
- `PATCH /api/nodes/{id}/content` taking an ordered `ContentEdit[]`.
- The splice engine (`ContentEditor`), the service method, controller wiring, interface, full tests.
- Line-unit and character-unit addressing (both are in the verbatim ask).

**Explicitly out of scope (named, not merely absent):**
- **MCP tools** to drive this — the Alex follow-up PR. It maps human-friendly verbs
  (`append`, `replace_lines`, `replace_chars`, `insert_at`, `delete_range`) and 1-based/inclusive human
  ranges onto the backend primitive.
- **Byte-range editing of binary content.** Not asked for; line/char ranges are meaningless for binary.
  Non-text content is rejected, never spliced.
- **Optimistic concurrency / a `test` precondition op.** The user did not ask for it; the status-quo
  wholesale `POST /content` has no concurrency control either, and sub-range editing already *reduces*
  the motivating corruption risk. Adding an etag/version/`test` op now is YAGNI (Design Contracts §6).
  A single edit request is transactional; the residual concurrent-writer lost-update window is identical
  to today's upload path and out of scope to close here.
- **A second backend increment.** The text splice feature is one cohesive unit; there is nothing smaller
  worth splitting and nothing larger in this PR.

## Assumptions & Constraints

- Content is `byte[]` + `ContentType` string; embedding regenerates on content change (Postgres only).
- KISS/DRY/YAGNI (Design Contracts #1136) and Backend Code Contracts (#114) bind the implementation.
- UTF-8 is the text encoding; DiVoid has a real UTF-8 corruption history (#187), so offset semantics and
  multi-byte handling must be explicit.
- SQLite (dev/test) and Postgres (prod) must behave identically for the edit itself — hence the splice
  runs in .NET, not in dialect-specific SQL.

## Architectural Overview

```
   PATCH /api/nodes/{id}/content            body: ContentEdit[]  (ordered splice list)
            │
            ▼
   NodeController.PatchContent  ──► NodeService.PatchContent
                                        │  write-gate load (ContentType, Content) in a transaction
                                        │  404 if missing / not writable / no content
                                        ▼
                                   ContentEditor.Apply(content, contentType, edits)   [pure, no I/O]
                                        │  text-only · strict UTF-8 · code-point boundaries
                                        │  bounds check · overlap reject · splice
                                        ▼  new byte[]
                                   UPDATE Content, LastUpdate  ──► RegenerateContentEmbedding (shared)
                                        ▼
                                   commit · return NodeDetails
```

## Components & Responsibilities

- **`ContentEdit` (DTO) + `ContentEditUnit` (enum `Line`/`Char`)** — the wire vocabulary. A single edit
  is a **splice**: replace the half-open range `[Start, Start+Length)` in `Unit` space with `Value`. It
  owns nothing but the shape. Every requested verb reduces to it: replace (`Length>0`), insert
  (`Length=0`), delete (`Value` empty/null), append (`Start` at the end, `Length=0`). One primitive, not
  five ops — the ergonomic verbs live in the MCP layer.
- **`ContentEditor` (pure static engine)** — owns *all* anti-corruption logic: text classification,
  strict UTF-8 decode, code-point/line offset resolution, bounds checking, overlap rejection, splicing,
  re-encode. No I/O, no SQL, no DI — mirrors the established `EmbeddingInputComposer` / `InlineContentEncoder`
  pure-helper pattern so the tricky logic is unit-tested without a database. Does **not** own persistence,
  auth, or embeddings.
- **`NodeService.PatchContent`** — owns orchestration: write-gate load, 404 semantics, calling the engine,
  the single `UPDATE`, embedding regen, transaction, response. Does **not** own the splice arithmetic.
- **`NodeController.PatchContent`** — owns HTTP binding and caller resolution only.
- **`RegenerateContentEmbedding` (extracted private helper)** — owns "recompute the embedding from name +
  current content" shared by `UploadContent` and `PatchContent`.

## Interactions & Data Flow (key flow)

1. Client sends `PATCH /api/nodes/{id}/content` with an ordered `ContentEdit[]` (JSON).
2. Controller resolves caller, delegates to the service.
3. Service opens a transaction, loads `ContentType`+`Content` behind the **write** visibility gate.
   Missing / not-writable / null-content → `NotFoundException<Node>` → **404**.
4. Service calls `ContentEditor.Apply`. Any caller-input fault → `ArgumentException` → **400**, and
   because the engine is all-or-nothing and runs before the `UPDATE`, **a rejected edit never mutates
   stored content**.
5. Service writes `Content` + `LastUpdate` in one `UPDATE`, regenerates the embedding (Postgres), commits.
6. Returns `NodeDetails` (mirrors `Patch`).

## Data Model (Conceptual)

No schema change. `Node.Content` / `Node.ContentType` are reused; `Content` stays **not** `[AllowPatch]`
(the new endpoint does not go through the property-patch engine). No new entity, no `DatabaseModelService`
registration.

## Contracts & Interfaces (Abstract)

**`ContentEdit`** — `Unit: Line|Char`, `Start: int (0-based, inclusive)`, `Length: int (≥0)`,
`Value: string (null/empty = delete)`.

**Addressing semantics (the anti-corruption core):**

| Concern | Rule |
|---|---|
| **Range convention** | Half-open `[Start, Start+Length)`, 0-based. `Length=0` = pure insertion at `Start`. `Start = count` = append. |
| **Char offset space** | **Unicode code points**, never bytes and never UTF-16 units. A boundary therefore can never split a multi-byte sequence or a surrogate pair. Resolved code-point offsets are mapped to UTF-16 indices that always land on a rune boundary before splicing. |
| **Line offset space** | Lines split on `\n`. A line's range **includes its trailing `\n`**: line range `[s, s+L)` covers from the start of line `s` up to the start of line `s+L`. Content with no `\n` is one line; a trailing `\n` yields a final empty line. A bare `\r` is ordinary content, so **CRLF files are edited without disturbing the `\r`**. |
| **Original-frame addressing** | Every edit in one request is addressed against the content **as read**, not against the evolving result of earlier edits. A caller computes all offsets from one snapshot. |
| **Overlap** | Overlapping resolved ranges → **400**. Adjacent ranges (`end == next.start`) and same-position inserts are allowed; same-position inserts apply in request order. |
| **Bounds** | `Start ≥ 0`, `Length ≥ 0`, `Start+Length ≤ unit count`. Anything else → **400** with a message naming the requested range and the valid maximum. No silent clamping. |
| **Text-only** | `ContentType` must classify as text (`TextContentTypePredicate.IsText`). Non-text/unknown/null → **400**. Binary is never decoded-and-spliced. |
| **Strict UTF-8** | Text content is decoded with a throw-on-invalid UTF-8 decoder; invalid bytes → **400**, never replacement-character corruption. |
| **Empty batch** | Empty/null `edits` → **400**. |

**HTTP contract:** `PATCH /api/nodes/{id}/content`, `[Authorize(Policy="write")]`, body `ContentEdit[]`,
returns `NodeDetails`. `404` = missing / not-writable / content-less node; `400` = any addressing or
content fault (`ArgumentException` → `badparameter`).

## Cross-Cutting Concerns

- **Security/authz:** reuses the existing **write** visibility gate exactly as `UploadContent` does — no
  new auth surface. `Content` is not made patchable, so the generic patch attack surface is unchanged.
- **Error handling:** reuses existing typed-exception → HTTP handlers (`ArgumentException`→400,
  `NotFoundException<Node>`→404). No new exception types (Code Contracts §12).
- **Consistency/atomicity:** read-modify-write + `UPDATE` + embedding regen in one transaction; the edit
  is all-or-nothing.
- **Observability:** one structured log line at the controller (node id + edit count), matching the
  existing content endpoints.
- **Embeddings:** regenerated via the shared helper so the search index tracks edited content.

## Quality Attributes & Trade-offs

- **Safety (primary):** fail-loud bounds/overlap/UTF-8/text checks *before* any write make partial edits
  strictly safer than the wholesale rewrite they replace — the stated goal.
- **Simplicity:** one primitive (splice) covers all five requested verbs; one pure engine holds all the
  arithmetic; line addressing is a thin adapter over the same offset resolution as char addressing (DRY).
- **Portability:** splicing in .NET keeps SQLite and Postgres identical; SQL-side splicing (rejected
  option b) would have diverged.
- **Trade-off — .NET read-modify-write vs SQL-side splice:** RMW pulls the blob into memory. For DiVoid's
  document-sized text nodes this is negligible, and it buys UTF-8 correctness + dialect parity + fast unit
  testing. The SQL-side alternative saves one round-trip but cannot honor code-point/line boundaries and
  is dialect-divergent. RMW wins decisively.
- **Trade-off — code-point vs UTF-16-unit char addressing:** code points cost a small offset-mapping walk
  but make astral characters (emoji, CJK-ext) atomic and give callers a "character" count they can
  actually compute. UTF-16-unit addressing is marginally simpler but can split a surrogate pair — the
  exact corruption this feature exists to prevent. Code points win.

## Risks & Mitigations

| Risk | Mitigation |
|---|---|
| Silent corruption from bad addressing | Fail-loud 400 on every out-of-range/overlap/invalid-UTF-8 case; all-or-nothing before write. Regression test asserts a rejected edit leaves content byte-identical. |
| Splitting a multi-byte character | Code-point addressing + rune-boundary-aligned splice; astral-character and multi-byte unit tests. |
| CRLF mangling | Split on `\n` only; `\r` treated as content; CRLF unit test. |
| Concurrent-writer lost update | Out of scope (matches status quo); single request is transactional. Noted, not closed. |
| Embedding drifting from edited content | Shared `RegenerateContentEmbedding` runs in the same transaction. |

## Migration / Rollout Strategy

Additive; no schema change, no migration, no flag. `POST /content` (wholesale) and `embed` are untouched.

## Open Questions

- None blocking. For Alex's MCP layer: confirm the human-facing range convention to expose (recommendation:
  accept 1-based inclusive line ranges and translate to the backend's 0-based half-open `Start`/`Length`,
  keeping the backend primitive unambiguous).

## Implementation Guidance for the Next Agent (build order) — DELIVERED in this PR

1. `ContentEditUnit` enum + `ContentEdit` DTO (one type per file).
2. `ContentEditor` pure engine (decode → resolve → validate → splice → encode); throws `ArgumentException`.
3. Extract `RegenerateContentEmbedding` from `UploadContent`; add `NodeService.PatchContent`.
4. `INodeService.PatchContent` + `NodeController` `PATCH {id}/content`.
5. Tests: `ContentEditorTests` (pure — all verbs, multi-edit, bounds, overlap, UTF-8, astral, CRLF) +
   `NodeContentPatchHttpTests` (200 line/char/multi, 404 missing/no-content, 400 out-of-range/non-text/overlap,
   rejected-edit-does-not-mutate).

## Pre-Design Checklist (Design Contracts #1136 §5)

- **KISS/DRY/YAGNI:** No mirror type (`ContentEdit` is a new, non-overlapping vocabulary). No one-impl
  abstraction. No "might need later" element. No deprecation/flag/shim. DRY math: the embedding-regen block
  is ~20 lines × 2 call sites (`UploadContent` + `PatchContent`) = ~40 > 20 → **extracted** to
  `RegenerateContentEmbedding` (named in 2 words). One splice op instead of five parallel ops.
- **Existing systems first:** Reuses `Node.Content`, the write gate, `TextContentTypePredicate`,
  `EmbeddingInputComposer`, existing error handlers. The one new HTTP verb is justified concretely: the
  SQL-set patch engine cannot do UTF-8 read-modify-write (named delta, not "cleanliness"). No new entity,
  table, or persisted field.
- **Configurability:** none added; no knobs, no audit columns.
- **Less is better:** each element passes delete/merge/inline — the DTO, the enum, the pure engine, the
  service method, the controller action, the extracted helper are each irreducible. Out-of-scope items
  listed explicitly.
- **Document discipline:** cites #114 and #1136 as load-bearing; scope and non-scope explicit; no
  superseded predecessor.
