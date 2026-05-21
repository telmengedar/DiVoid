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

# SHA-256 of node #8 content as of 2026-05-21 bootstrap.
PINNED_API_REF_HASH = "82d975f20d497b61dc1f545e9307d7bc8897b70b838a73c1773808be2215fe52"
