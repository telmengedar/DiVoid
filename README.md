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

The setup endpoint is `POST /api/teams/messages`. It is disabled by default and supports a single tenant in the public Azure cloud. Register a single-tenant Entra application and Azure Bot with the same Microsoft App ID and tenant, enable the Microsoft Teams channel, and set the bot's messaging endpoint to your public HTTPS URL followed by `/api/teams/messages`.

Provide these settings through the deployment's secret provider or environment. Replace the placeholders; keep the client secret out of source control:

```
Teams__Enabled=true
AzureAd__TenantId=<tenant-guid>
AzureAd__ClientId=<bot-microsoft-app-guid>
AzureAd__ClientCredentials__0__SourceType=ClientSecret
AzureAd__ClientCredentials__0__ClientSecret=<secret-from-secret-provider>
```

Create and install a Teams app manifest with `bots[].botId` set to that App ID and the desired conversation scopes. Send text to the bot and expect: “DiVoid Teams webhook is connected. Message routing to DiVoid users is not implemented yet.” This reply confirms the setup handler ran and the Connector accepted its outbound request. It does not establish delivery to a DiVoid user. Attachment-only and lifecycle activities are acknowledged without a reply; invoke actions return 501.

Microsoft Connector bearer JWTs authenticate this endpoint through Microsoft's signing keys, exact bot audience, issuer and lifetime. Activities must also match the configured tenant, Teams channel, endorsed signing key and signed HTTPS service URL. A bot ID, app secret, DiVoid API key or outgoing-webhook HMAC does not authenticate an incoming bot activity. An unauthenticated POST returns 401; there is no GET verification challenge or unauthenticated emulator mode. Teams authentication remains required when DiVoid authentication is disabled. Requests are limited to 256 KiB and replies to 15 seconds. Unsafe AzureAd/BotFramework configuration overrides fail startup.

Public reachability, TLS/proxy setup, tenant installation and real Teams delivery require deployment validation. Routing to DiVoid users, a virtual Teams user and tag/channel defaults are deferred. The host currently targets .NET 9, whose Microsoft support ended on November 10, 2026; upgrading that runtime is separate work.
