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

__version__ = "0.21.0"

# SHA-256 of the exact API reference staged at DiVoid #34170 for this release.
# Publish those checked bytes to #8 after merge; drift remains warning-only until then.
PINNED_API_REF_HASH = "2748f17d7737b64f72df47be1b791186162e26ee59c9abb570bf754054757331"
