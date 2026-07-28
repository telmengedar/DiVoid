"""
_link_details -- shared linkDetails row normalization for inline-edge tools.

divoid_list and divoid_search both offer an include_link_details opt-in that
appends the backend's `linkDetails` field to the fields projection and surfaces
it per result row. Both need the exact same camelCase -> snake_case
normalization, so it lives here once rather than duplicated per tool module
(same rationale as _groups.py's resolve_group).

Mirrors divoid_get_links's row normalization exactly (see get_links.py):
sourceId/targetId are always present -> source_id/target_id; linkType/context
are pass-through (invariant 6 — no vocabulary policing) and are only surfaced
when the backend row actually carries them, which distinguishes "field
unknown to this backend" from "backend explicitly returned null".
"""

from __future__ import annotations

from typing import Any


def normalize_link_details(raw: list[dict[str, Any]]) -> list[dict[str, Any]]:
    """Normalize a raw linkDetails array into the snake_case link_details shape."""
    normalized: list[dict[str, Any]] = []
    for link in raw:
        row: dict[str, Any] = {
            "source_id": link.get("sourceId"),
            "target_id": link.get("targetId"),
        }
        if "linkType" in link:
            row["link_type"] = link["linkType"]
        if "context" in link:
            row["context"] = link["context"]
        normalized.append(row)
    return normalized
