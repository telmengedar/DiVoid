"""
Version constants for divoid-mcp.

PINNED_API_REF_HASH is the SHA-256 (hex, 64 chars) of node #8's content body
at the time this version was pinned. The drift canary compares the live hash
against this constant at startup and logs a WARNING on mismatch.

To update after verifying an API change is intentional:
1. Fetch the new hash:
   curl -s -H "Authorization: Bearer $DIVOID_KEY" "$DIVOID_URL/nodes/8/content" | sha256sum
2. Replace PINNED_API_REF_HASH below with the new 64-char hex string.
3. Commit with a message noting what changed in node #8.
See docs/drift-policy.md for the full procedure.
"""

__version__ = "0.1.0"

# SHA-256 of node #8 content as of 2026-05-26; fields=content documented (PR #119 / DiVoid #1181).
PINNED_API_REF_HASH = "0c0ea79ad07c226f69d0efca0f5c364177155f5c0c1cc00e56754dd3d4fdc602"
