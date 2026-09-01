"""Shared pytest fixtures for divoid-mcp unit tests."""

from __future__ import annotations

import pytest

from divoid_mcp import paths


@pytest.fixture(autouse=True)
def _isolate_path_roots():
    paths._roots = ()
    yield
    paths._roots = ()
