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

__version__ = "0.18.0"

# SHA-256 of node #8 content as of 2026-10-26; fields=content documented (PR #119 / DiVoid #1181).
# Hash verified after PR #122 (backend fields=links) merged — node #8 not yet updated for links.
PINNED_API_REF_HASH = "ae701b92ef44da0cf3a0e6e4a9ca583adfd11a887078700f3706cf83813d9dd6"
