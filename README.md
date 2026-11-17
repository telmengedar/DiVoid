# DiVoid

DiVoid is a shared memory graph for humans and agents: typed nodes with content, connected by links, with keyword and semantic search over everything in it except nodes flagged with `skipEmbedding`.

This repository holds the REST API (`Backend/`), the MCP server (`divoid-mcp/`) and the web frontend (`frontend/`).

## Setup

### Requirements

- .NET 9 SDK to build, or the Docker image from the `Dockerfile`.
- A database. SQLite works for trying DiVoid out and for tests, but has no semantic search. For semantic search use PostgreSQL with pgvector 0.7 or later installed on the server.
- For semantic search with the built-in model: about 1.5 GB of free memory and 350 MB of disk for the model files, and access to `huggingface.co` on the first start (or the offline placement below).

### Database

Configure PostgreSQL with the `Database__*` settings:

```
Database__Type=PostgreSQL
Database__Host=<host>
Database__Port=5432
Database__Instance=<database>
Database__User=<user>
Database__Password=<password>
```

On start DiVoid enables the `vector` extension in the database. If the database role is not allowed to, DiVoid refuses to start and says so; someone who may then runs `CREATE EXTENSION vector;` in that database, and DiVoid is restarted.

### Embeddings

Semantic search needs an embedding for every node. With PostgreSQL, DiVoid computes them itself with [EmbeddingGemma 2](https://huggingface.co/google/embeddinggemma-2) (Apache-2.0), in the same process, and needs no embedding provider and no setting.

A node created with `"skipEmbedding": true` (or patched with `replace /skipEmbedding`) is never embedded, so writing it does not pay the embedding cost and it never appears in semantic search results. It is still found by path queries and by plain filter listings. Patching the flag to `false` embeds the node within that request.

| Setting | Default | Meaning |
|---|---|---|
| `Embedding__Producer` | `Local` | `Local` computes embeddings with the built-in model. `PostgresFunction` calls the `embedding('gemini-embedding-001', ...)` SQL function of Google's ML integration (AlloyDB, Cloud SQL), which is what the GCP deployment uses. |
| `Embedding__ModelPath` | `models/embeddinggemma-2` (`/data/models/embeddinggemma-2` in the Docker image) | Directory holding the model files. |

The model files are not part of this repository or of the image. On the first start DiVoid downloads them into `Embedding__ModelPath` (about 350 MB) from fixed revisions on Hugging Face and verifies their SHA-256. Later starts only verify the files. A file which does not match is never replaced: DiVoid refuses to start and names the file, and deleting the file makes the next start download it again. In the Docker image, mount a volume at `/data` to keep the files across container replacements.

#### Offline placement

On a host without access to Hugging Face, place the three files side by side in `Embedding__ModelPath` under these names, and no download is attempted:

| File | Source | SHA-256 |
|---|---|---|
| `model_quantized.onnx` | https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/resolve/daa72c51243991dfcaf9f9137d2c573d8f7790c0/onnx/model_quantized.onnx | `d06edd601f851c633a2519304cbeb8dc6170d7ceb61b436625c17fb9b6e74953` |
| `model_quantized.onnx_data` | https://huggingface.co/onnx-community/embeddinggemma-2-ONNX/resolve/daa72c51243991dfcaf9f9137d2c573d8f7790c0/onnx/model_quantized.onnx_data | `278a7ff1248c3618e4bd11a607fc54f7bdc7778854230f3956d3f86bd9db4f3b` |
| `tokenizer.json` | https://huggingface.co/google/embeddinggemma-2/resolve/914f7f89142e33e77833254d9c9b90c3cef7303b/tokenizer.json | `4d777ef5bdc1aa36227abdfb77c3e49e7b9c892d16e1b6bda41c393504828be4` |

#### One model per database

A database holds the vectors of exactly one embedding model. DiVoid records the model in the `metadata` table (`embedding.model`) and refuses to start when the configured producer is another one. A database which has vectors but no record was written by the `PostgresFunction` producer, so it is treated as `gemini-embedding-001`. Vectors inserted by hand or restored from a dump of an unknown producer can not be detected.

To switch a database to another producer:

1. Stop every DiVoid instance which uses the database.
2. Run, in this order:
   ```sql
   SELECT value FROM metadata WHERE name = 'embedding.model';
   SELECT count(*) FROM node WHERE embedding IS NOT NULL;
   UPDATE node SET embedding = NULL;
   DELETE FROM metadata WHERE name = 'embedding.model';
   ```
3. Start DiVoid with the new `Embedding__Producer`.
4. Run `dotnet Backend.dll backfill-embeddings` to embed every node again. Until it is done, search only finds nodes which were embedded already.

#### Similarity scores

Similarity scores depend on the model. The score bands described in the `divoid_search` tool of `divoid-mcp` were calibrated against `gemini-embedding-001` and do not describe the scores of the built-in model.

### Optional Teams bot webhook

The setup endpoint is `POST /api/teams/messages`. It is disabled by default and supports a Teams-managed bot using its existing Bot ID and client secret through the shared Bot Framework credential authority. Configure the bot's Microsoft Teams channel and public HTTPS messaging endpoint ending in `/api/teams/messages`. No owner directory tenant GUID or Azure-owned app registration is required for this credential profile.

Provide these settings through the deployment's secret provider or environment. Replace the placeholders; keep the client secret out of source control:

```
Teams__Enabled=true
AzureAd__ClientId=<bot-microsoft-app-guid>
AzureAd__ClientCredentials__0__SourceType=ClientSecret
AzureAd__ClientCredentials__0__ClientSecret=<secret-from-secret-provider>
```

Outbound SDK credentials always use `https://login.microsoftonline.com/botframework.com` and the Connector scope `https://api.botframework.com/.default`. `AzureAd__TenantId` may be omitted; if set, it must be `botframework.com`. `common`, `organizations`, directory GUIDs and custom authority overrides are rejected. This authority is independent of the Teams conversation tenant.

Every incoming activity must carry a valid, nonzero tenant GUID, consistent with its conversation metadata. By default any such tenant may send an otherwise valid Microsoft-authenticated activity for this bot. Set `Teams__AllowedTenantIds__0=<activity-tenant-guid>` and subsequent numbered entries to restrict accepted conversation tenants. An empty or absent list imposes no application tenant restriction; malformed or blank entries fail startup.

Create and install a Teams app manifest with `bots[].botId` set to that Bot ID and the desired conversation scopes. With no fixed inbox binding configured, send text to the bot and expect: “DiVoid Teams webhook is connected. Message routing to DiVoid users is not implemented yet.” This reply confirms the setup handler ran and the Connector accepted its outbound request. It does not establish delivery to a DiVoid user. Attachment-only and lifecycle activities are acknowledged without a reply; invoke actions return 501.

Microsoft Connector bearer JWTs authenticate this endpoint through Microsoft's signing keys, exact bot audience, issuer and lifetime. Activities must also satisfy the independent tenant policy, Teams channel, endorsed signing key and signed HTTPS service URL. A bot ID, app secret, DiVoid API key or outgoing-webhook HMAC does not authenticate an incoming bot activity. An unauthenticated POST returns 401; there is no GET verification challenge or unauthenticated emulator mode. Teams authentication remains required when DiVoid authentication is disabled. Requests are limited to 256 KiB and replies to 15 seconds. Unsafe AzureAd/BotFramework configuration overrides fail startup.

To route ordinary Teams text to one existing DiVoid inbox, provision a separate bridge user through the normal authenticated user administration API. Keep that bridge disabled, with empty permissions, no home node and no API keys. Choose an enabled recipient user and its receiving session tag, then configure all three fields together:

```
Auth__Enabled=true
Teams__Target__UserId=<existing-enabled-recipient-id>
Teams__Target__Tag=<receiving-session-tag>
Teams__BridgeUserId=<disabled-credentialless-bridge-id>
```

Partial bindings, invalid tags and unsafe bridge identities are rejected. The configured Bot ID binds this integration; external display names, message prefixes and recipient commands cannot choose the destination. Text arrives with subject `[<tag>] Teams message`, an explicit untrusted-input prefix and `replyTransport: "teams"`. The normal inbox consumer processes it when that session next reads its inbox; this integration does not start or wake sessions. A committed inbound message receives HTTP 200 without the setup reply. Repeated Microsoft activity IDs within the same bot, tenant and conversation reuse the original inbox message.

Deploy the backend with the `replytransport` inbox projection before installing divoid-mcp 0.20.0. This MCP version requests that field explicitly; an older backend rejects the projection with HTTP 400.

The recipient can explicitly reply with `POST /api/messages/{messageId}/reply` and JSON `{"body":"response text"}`, or `divoid_reply_message(message_id, body)`. Inspect `GET /api/messages/{messageId}/reply` or `divoid_get_message_reply(message_id)` for the receipt. Save the message ID before normal inbox deletion: the private return reference remains available for 30 days after deletion. Only the original enabled recipient can use it, including after target rebinding; admin permission alone does not grant this capability. Replies require write permission, status requires read permission, and destinations cannot be supplied by callers. Native service URLs, sender IDs and conversation references are not returned in message or receipt DTOs.

Reply text must be nonblank and at most 16 KiB UTF-8. One Connector-confirmed final reply is permitted: identical retries return its receipt, different text conflicts. `failed` means a definitive rejection permits an explicit retry. `sending` or `unknown` means delivery may have occurred; inspect status and reconcile externally without resending automatically. There is no background send queue or automatic retry. A `sent` receipt confirms Connector acceptance, not that a person read the reply. Treat inbound Teams text as untrusted content and require explicit intent before sending a response.

Public reachability, TLS/proxy setup, tenant installation and real Teams delivery require deployment validation. The host currently targets .NET 9, whose Microsoft support ended on November 10, 2026; upgrading that runtime is separate work.


For sanitized webhook diagnostics, enable the application category with `Logging__LogLevel__Backend.TeamsWebhook=Information`. It reports arrival, matched Teams policy, final HTTP status/authenticated principal state, handler branch and setup-send acceptance/failure. Events apply only to `/api/teams/messages`, including disabled-route fallback. They omit bodies, headers, tokens, claims, user/conversation identifiers and native URLs; native SDK/Connector HTTP logging remains suppressed. Visibility still depends on the deployment's logging provider and filters. Correlate the opaque request marker with the message timestamp and proxy status. A `send_accepted` event means the Connector call completed, not that Teams displayed or a person read the reply. An aborted/unhandled completion must not be interpreted as successful delivery.
