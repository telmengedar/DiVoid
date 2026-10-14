import json
import sys

import torch
from sentence_transformers import SentenceTransformer

MODEL = "google/embeddinggemma-2"
REVISION = "914f7f89142e33e77833254d9c9b90c3cef7303b"

WINDOW_SENTENCES = [
    "The scheduler keeps a queue of pending jobs and wakes a worker whenever the queue is not empty.",
    "Each node stores its name, a content type and optionally a body which is embedded for semantic search.",
    "Bananas, quinces and medlars ripen at different speeds, which is why the market stalls are sorted by week.",
    "A reverse proxy terminates TLS and forwards the request to the upstream service on a private network.",
    "Ludwig walked along the canal every morning and counted the barges that were already moored.",
    "Rollback is only possible while the previous schema version is still compatible with the running code.",
]

TEXTS = [
    ("english-prose", "Notes on the release process\n\nWe cut a release branch every second Thursday. The branch is frozen for everything except fixes, and the staging environment is rebuilt from it overnight. If the smoke tests pass, the release is promoted on Friday morning; otherwise the on-call engineer decides whether to fix forward or to revert."),
    ("german-prose", "Übersicht der Wartungsfenster\n\nDie Datenbank wird jeden Sonntag zwischen zwei und vier Uhr gewartet. Während dieser Zeit sind Schreibzugriffe nicht möglich; Lesezugriffe über die Schnittstelle bleiben erhalten. Größere Änderungen am Schema werden grundsätzlich vorher angekündigt, damit die Straßenkarten der Teams angepasst werden können."),
    ("markdown", "# Deployment\n\n## Steps\n\n1. Build the image\n2. Run the tests\n3. Push to the registry\n\n- **Note:** the `latest` tag is never used\n- See [the runbook](https://example.com/runbook)\n\n| env | replicas | memory |\n|-----|----------|--------|\n| dev | 1 | 512Mi |\n| prod | 3 | 2Gi |\n\n---\n\n```bash\ndocker build -t app:1 .\n```\n"),
    ("source-code", "public class Counter {\n\tint value;\n\n\tpublic void Increment(int by) {\n\t\tif (by < 0)\n\t\t\tthrow new ArgumentException(\"by\");\n\t\tvalue += by;\n\t}\n\n    public int Value => value;\n}\n"),
    ("whitespace", "   leading spaces\n\n\n\nafter blank lines    and    inner    runs\t\ttabs \n  trailing spaces   \n\n"),
    ("digits", "Invoice 2026-10-14 over 1234567890123 units at 3.14159 each, shipped to 10.0.0.255 on port 8080, build 9.0.100-preview.7, phone +49 170 1234567."),
    ("emoji-cjk", "Release \U0001F389 done \U0001F680 — 漢字かな交じり文 and 한국어 and ñandú, café, naïve, Ελληνικά, العربية, \U0001F468\u200d\U0001F469\u200d\U0001F467"),
    ("special-token-literals", "Literal markers inside user text: <bos> then <eos> then <|turn> and <turn|> and <mask> and <pad> and <unk> and <image|> end"),
    ("name-only", "Release checklist"),
    ("repeated-runs", "Separators\n\n==========\n----------\n**********\nxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx\naaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n.......................\n"),
    ("urls-and-paths", "See https://example.com/a/b?c=d&e=f#frag and C:\\Users\\max\\file.txt and /var/log/app/error.log and user@example.com"),
    ("mixed-newlines", "line one\r\nline two\rline three\nline four\n\n\nline five"),
    ("name-and-content", "Quarterly planning\n\nGoals for the next quarter: reduce page load time, migrate the legacy billing job, and document the on-call handover."),
]


def full_window():
    text = "Full window\n\n"
    index = 0
    while len(text) < 8000:
        text += WINDOW_SENTENCES[index % len(WINDOW_SENTENCES)] + " "
        if index % 7 == 6:
            text += "\n\n"
        index += 1
    return text[:8000]


def main():
    model = SentenceTransformer(MODEL, revision=REVISION, device="cpu", model_kwargs={"dtype": torch.float32})
    if model.default_prompt_name is not None:
        raise SystemExit("default prompt is set; the prefix would be applied twice")
    dtype = next(model.parameters()).dtype
    print("max_seq_length", model.max_seq_length, "dtype", dtype, file=sys.stderr)
    if dtype != torch.float32:
        raise SystemExit("the reference must run in float32, not %s" % dtype)
    print("sentence-transformers prompts", model.prompts, file=sys.stderr)

    texts = TEXTS + [("full-window", full_window())]
    entries = []
    for name, text in texts:
        for purpose in ("document", "query"):
            if name == "full-window" and purpose == "query":
                continue
            prompt = model.prompts[purpose] + text
            ids = model.tokenize([prompt])["input_ids"][0].tolist()
            if len(ids) > model.max_seq_length:
                raise SystemExit("%s/%s has %d tokens, more than max_seq_length %d" % (name, purpose, len(ids), model.max_seq_length))
            vector = model.encode([prompt], normalize_embeddings=True, convert_to_numpy=True)[0]
            entries.append({
                "name": name,
                "purpose": purpose,
                "prompt": prompt,
                "inputIds": ids,
                "vector": [round(float(component), 7) for component in vector],
            })
            print("done", name, purpose, len(ids), file=sys.stderr)

    json.dump({
        "model": MODEL,
        "revision": REVISION,
        "torch": torch.__version__,
        "prompts": dict(model.prompts),
        "entries": entries,
    }, sys.stdout, ensure_ascii=False, separators=(",", ":"))


main()
