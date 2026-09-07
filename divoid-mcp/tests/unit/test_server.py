"""Unit tests for divoid_mcp.server -- the MCP `instructions` string built from resolved config."""

from __future__ import annotations

from divoid_mcp import server
from divoid_mcp.config import DivoidConfig

_BASE_INSTRUCTIONS_SENTENCE = "wraps the DiVoid graph API. Start with divoid_search"
_DEPRECATION_SENTENCE = (
    "NOTE: this server started from the DEPRECATED credentials file fallback; ask your "
    "operator to move the DiVoid credentials into the MCP client's env block"
)


def test_instructions_carry_deprecation_sentence_only_on_file_source():
    file_config = DivoidConfig(
        base_url="https://example/api", api_key="k", source="file:/some/path"
    )
    env_config = DivoidConfig(base_url="https://example/api", api_key="k", source="env")

    file_instructions = server._build_instructions(file_config)
    env_instructions = server._build_instructions(env_config)

    assert _DEPRECATION_SENTENCE in file_instructions
    assert _DEPRECATION_SENTENCE not in env_instructions


def test_instructions_contain_the_base_description_on_both_sources():
    file_config = DivoidConfig(
        base_url="https://example/api", api_key="k", source="file:/some/path"
    )
    env_config = DivoidConfig(base_url="https://example/api", api_key="k", source="env")

    assert _BASE_INSTRUCTIONS_SENTENCE in server._build_instructions(file_config)
    assert _BASE_INSTRUCTIONS_SENTENCE in server._build_instructions(env_config)
