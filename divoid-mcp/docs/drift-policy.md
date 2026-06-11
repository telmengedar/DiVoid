# Drift Policy

divoid-mcp pins the SHA-256 hash of node #8 (DiVoid API reference) in
`src/divoid_mcp/version.py::PINNED_API_REF_HASH`. The startup drift canary
compares this constant against the live content of node #8.

## What drift means

The hash changes when the text of node #8 changes. This could be:
- **Benign**: a typo fix, a wording improvement, an added example.
- **Significant**: a new endpoint, a changed response shape, a removed parameter.

The canary cannot distinguish these cases — it only detects change. You
(the operator or the reviewing agent) must inspect what changed to decide
whether any tool behaviour needs updating.

## On startup: what you'll see

A drift warning looks like this in the log (stderr):

```
WARNING divoid_mcp.drift: API reference hash MISMATCH — node #8 content
has changed since this version of divoid-mcp was pinned.
pinned=82d975f20d497b61... observed=a1b2c3d4e5f6g7h8...
The server will continue, but tool behaviour may be incorrect.
See docs/drift-policy.md to update the pin.
```

The server **does not refuse to start**. Tools remain operational.

## Update procedure

1. Read node #8 to understand what changed:
   ```bash
   DIVOID_KEY=$(awk -F= '/^ApiKey=/{print $2}' ~/.claude/secrets/.divoid-online)
   DIVOID_URL=$(awk -F= '/^Url=/{print $2}' ~/.claude/secrets/.divoid-online)
   curl -s -H "Authorization: Bearer $DIVOID_KEY" "$DIVOID_URL/nodes/8/content"
   ```

2. Verify the change is understood and benign (or update any affected tool
   behaviour if the API surface changed).

3. Compute the new hash:
   ```bash
   curl -s -H "Authorization: Bearer $DIVOID_KEY" "$DIVOID_URL/nodes/8/content" | sha256sum
   ```

4. Update `PINNED_API_REF_HASH` in `src/divoid_mcp/version.py`:
   ```python
   PINNED_API_REF_HASH = "<new 64-char hex>"
   ```

5. Commit with a message like:
   ```
   chore: bump node #8 hash pin — <brief description of what changed>
   ```
   Include the old and new hash prefixes in the commit body so the change
   is auditable in git log.

6. Re-run the smoke tests to confirm:
   ```bash
   python tests/smoke/run_all.py
   ```

## False positives

Minor formatting changes to node #8 (whitespace, punctuation) will trigger
the canary even when no tool-relevant change occurred. These are safe to
bump without further investigation, but always inspect before bumping — even
a "trivial" edit can be accompanied by a substantive one.

## Future enhancements

A future iteration may replace the raw-bytes hash with a structural hash (parsed
section headings + parameter names) to reduce false positives from cosmetic edits.
Until then, the manual loop above is the correct procedure.
