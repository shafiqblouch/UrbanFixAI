from __future__ import annotations

from pathlib import Path

_ROOT = Path(__file__).parent


def load_prompt(name: str, version: str) -> str:
    """Prompts are versioned files; the version in use is pinned in Settings and returned with every result."""
    path = (_ROOT / name / f"{version}.txt").resolve()
    if _ROOT.resolve() not in path.parents:
        raise ValueError("invalid prompt path")
    return path.read_text(encoding="utf-8")
