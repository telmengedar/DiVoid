"""Unit tests for divoid-mcp's pyproject.toml -- version.py is the single source of the package version."""

from __future__ import annotations

import tomllib
from pathlib import Path

PYPROJECT_PATH = Path(__file__).parents[2] / "pyproject.toml"


def _load_pyproject() -> dict:
    return tomllib.loads(PYPROJECT_PATH.read_text(encoding="utf-8"))


def test_project_table_carries_no_static_version():
    data = _load_pyproject()

    assert "version" not in data["project"]


def test_project_dynamic_lists_version():
    data = _load_pyproject()

    assert "version" in data["project"].get("dynamic", [])


def test_hatch_version_source_points_at_version_py():
    data = _load_pyproject()

    assert data["tool"]["hatch"]["version"]["path"] == "src/divoid_mcp/version.py"
