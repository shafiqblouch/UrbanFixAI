"""AI evaluation harness (QA-04).

Runs the production complaint-understanding pipeline over a labelled set and reports accuracy per language and category.

Model answers are recorded in a cache keyed by (model, exact system prompt, exact user text). Normal runs and CI *replay* the
cache, so they are free and deterministic. Changing the model, the prompt (any wording or version) or a test case misses the
cache, and the run then fails with ``CacheMiss`` until someone re-runs with ``--live`` and commits the refreshed cache. That is
how "reruns on any model or prompt change" is enforced without calling a paid API on every build.
"""

from __future__ import annotations

import hashlib
import json
from collections import Counter, defaultdict
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any

from app.config import Settings
from app.pii import ScrubbedText
from app.providers import Providers
from app.providers.base import LlmResult, ProviderError, Usage
from app.schemas import CategoryDef, UnderstandRequest
from app.understanding import InvalidModelOutput, understand

HERE = Path(__file__).resolve().parent

# The organisation categories the labelled set was written against.
CATEGORIES = [
    CategoryDef(key="water", label="Water supply", description="no water, low pressure, dirty water"),
    CategoryDef(key="electricity", label="Electricity", description="power, wires, transformers, street lights"),
    CategoryDef(key="sewage_drainage", label="Sewage and drainage", description="sewers, manholes, rain water"),
    CategoryDef(key="roads", label="Roads", description="potholes, speed breakers"),
    CategoryDef(key="garbage", label="Garbage", description="waste collection, dumping"),
    CategoryDef(key="security", label="Security", description="guards, gates, CCTV, theft"),
    CategoryDef(key="noise", label="Noise", description="generators, music, loudspeakers"),
]

# Tasks the harness knows about. A task with no labelled cases is reported as "not covered", never as passing.
TASKS = ("understanding", "priority", "duplicate", "photo")


class CacheMiss(Exception):
    """The recorded answers do not cover this run: the model, a prompt or a case changed. Re-run with --live."""


@dataclass(frozen=True)
class Case:
    id: str
    language: str
    text: str
    category: str
    tags: tuple[str, ...] = ()


def load_cases(path: Path) -> list[Case]:
    raw = json.loads(path.read_text(encoding="utf-8"))
    return [Case(r["id"], r["language"], r["text"], r["category"], tuple(r.get("tags", []))) for r in raw]


def cache_key(model: str, system: str, user: str, json_mode: bool) -> str:
    return hashlib.sha256(json.dumps([model, system, user, json_mode], ensure_ascii=False).encode("utf-8")).hexdigest()


class AnswerCache:
    """Recorded model answers for one model, stored as a sorted JSON file so changes show up cleanly in review."""

    def __init__(self, path: Path) -> None:
        self.path = path
        self.entries: dict[str, dict[str, Any]] = json.loads(path.read_text(encoding="utf-8")) if path.exists() else {}
        self.dirty = False

    def get(self, key: str) -> dict[str, Any] | None:
        return self.entries.get(key)

    def put(self, key: str, value: dict[str, Any]) -> None:
        self.entries[key] = value
        self.dirty = True

    def save(self) -> None:
        if not self.dirty:
            return
        self.path.parent.mkdir(parents=True, exist_ok=True)
        self.path.write_text(json.dumps(self.entries, indent=1, ensure_ascii=False, sort_keys=True) + "\n", encoding="utf-8")
        self.dirty = False


class RecordingLlm:
    """Sits where the LLM provider does. Replays recorded answers; with an inner provider (--live) it calls it and records."""

    def __init__(self, cache: AnswerCache, inner: Any | None = None) -> None:
        self.cache = cache
        self.inner = inner
        self.live_calls = 0
        self.input_tokens = 0
        self.output_tokens = 0

    async def complete(self, *, model: str, system: ScrubbedText, user: ScrubbedText, json_mode: bool) -> LlmResult:
        key = cache_key(model, system, user, json_mode)
        hit = self.cache.get(key)
        if hit is None:
            if self.inner is None:
                raise CacheMiss(f"no recorded answer for model '{model}' (prompt or case changed)")
            result = await self.inner.complete(model=model, system=system, user=user, json_mode=json_mode)
            hit = {"text": result.text, "model": result.model, "input_tokens": result.usage.input_tokens, "output_tokens": result.usage.output_tokens}
            self.cache.put(key, hit)
            self.live_calls += 1
        self.input_tokens += hit["input_tokens"]
        self.output_tokens += hit["output_tokens"]
        return LlmResult(hit["text"], hit["model"], Usage(hit["input_tokens"], hit["output_tokens"]))


@dataclass
class Row:
    case: Case
    status: str = "ok"  # ok | invalid_output | provider_error
    got: str | None = None
    language_ok: bool | None = None
    needs_more_info: bool | None = None
    confidence: float | None = None
    prompt_version: str | None = None
    model: str | None = None


async def run_understanding(cases: list[Case], providers: Providers, settings: Settings, concurrency: int = 6) -> list[Row]:
    import asyncio

    sem = asyncio.Semaphore(concurrency)
    rows = [Row(c) for c in cases]

    async def one(row: Row) -> None:
        async with sem:
            req = UnderstandRequest(text=row.case.text, categories=CATEGORIES)
            try:
                out = await understand(req, providers, settings)
            except InvalidModelOutput:
                row.status = "invalid_output"
                return
            except ProviderError:
                row.status = "provider_error"
                return
            row.got = out.category
            exp = row.case.language
            # Script and word counts decide the language; "mixed" is acceptable for either Urdu form.
            row.language_ok = out.language == exp or (exp in ("ur", "roman_ur") and out.language == "mixed")
            row.needs_more_info = out.needs_more_info
            row.confidence = out.confidence
            row.prompt_version = out.prompt_version
            row.model = out.model

    await asyncio.gather(*(one(r) for r in rows))
    return rows


def _ratio(hits: int, n: int) -> float | None:
    return round(hits / n, 4) if n else None


def build_report(rows: list[Row], model: str, prompt_version: str, live_calls: int = 0, cost_usd: float | None = None) -> dict[str, Any]:
    ok = [r for r in rows if r.status == "ok"]
    correct = [r for r in ok if r.got == r.case.category]

    def group(key) -> dict[str, dict[str, Any]]:
        buckets: dict[str, list[Row]] = defaultdict(list)
        for r in rows:
            buckets[key(r)].append(r)
        return {
            k: {"n": len(v), "accuracy": _ratio(sum(1 for r in v if r.status == "ok" and r.got == r.case.category), len(v))}
            for k, v in sorted(buckets.items())
        }

    confusion = Counter((r.case.category, r.got) for r in ok if r.got != r.case.category)
    others = [r for r in ok if r.case.category == "other"]
    covered = {"understanding": len(rows)}
    return {
        "model": model,
        "prompt_version": prompt_version,
        "cases": len(rows),
        "understanding": {
            "accuracy": _ratio(len(correct), len(rows)),
            "valid_rate": _ratio(len(ok), len(rows)),
            "language_detection": _ratio(sum(1 for r in ok if r.language_ok), len(ok)),
            "by_language": group(lambda r: r.case.language),
            "by_category": group(lambda r: r.case.category),
            "by_tag": group_by_tag(rows),
            "flagged_for_review_when_other": _ratio(sum(1 for r in others if r.needs_more_info), len(others)),
            "mean_confidence": round(sum(r.confidence or 0 for r in ok) / len(ok), 3) if ok else None,
        },
        "mistakes": [
            {"id": r.case.id, "language": r.case.language, "expected": r.case.category, "got": r.got, "status": r.status}
            for r in sorted(rows, key=lambda r: r.case.id)
            if r.status != "ok" or r.got != r.case.category
        ],
        "confusion": [{"expected": e, "got": g, "count": c} for (e, g), c in confusion.most_common(10)],
        "coverage": {t: covered.get(t, 0) for t in TASKS},
        "live_calls": live_calls,
        "cost_usd": cost_usd,
    }


def group_by_tag(rows: list[Row]) -> dict[str, dict[str, Any]]:
    buckets: dict[str, list[Row]] = defaultdict(list)
    for r in rows:
        for tag in r.case.tags or ("untagged",):
            buckets[tag].append(r)
    return {
        k: {"n": len(v), "accuracy": _ratio(sum(1 for r in v if r.status == "ok" and r.got == r.case.category), len(v))}
        for k, v in sorted(buckets.items())
    }


@dataclass
class Verdict:
    failures: list[str] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    @property
    def passed(self) -> bool:
        return not self.failures


def check(report: dict[str, Any], thresholds: dict[str, Any], baseline: dict[str, Any] | None) -> Verdict:
    """Compares a report with the release thresholds and, if given, the committed baseline."""
    v = Verdict()
    u = report["understanding"]

    def need(name: str, value: float | None, minimum: float) -> None:
        if value is None or value < minimum:
            v.failures.append(f"{name} {value} is below the minimum {minimum}")

    need("accuracy", u["accuracy"], thresholds["min_accuracy"])
    need("valid_rate", u["valid_rate"], thresholds["min_valid_rate"])
    for lang, stats in u["by_language"].items():
        need(f"accuracy for {lang}", stats["accuracy"], thresholds["min_accuracy_per_language"])
    for cat, stats in u["by_category"].items():
        if stats["n"] >= thresholds.get("category_min_cases", 3):
            need(f"accuracy for category {cat}", stats["accuracy"], thresholds["min_accuracy_per_category"])
    if (flag := u["flagged_for_review_when_other"]) is not None:
        need("share of 'other' complaints flagged for review", flag, thresholds.get("min_other_flagged", 0.0))

    if baseline is not None:
        if baseline.get("model") != report["model"] or baseline.get("prompt_version") != report["prompt_version"]:
            v.notes.append(f"model/prompt changed from {baseline.get('model')}/{baseline.get('prompt_version')}: update the baseline if the new results are accepted")
        drop = (baseline["understanding"]["accuracy"] or 0) - (u["accuracy"] or 0)
        if drop > thresholds["max_regression"]:
            v.failures.append(f"accuracy fell by {drop:.3f} against the baseline (allowed {thresholds['max_regression']})")
    for task, n in report["coverage"].items():
        if n == 0:
            v.notes.append(f"task '{task}' has no labelled cases, so it is not evaluated")
    return v


def render_markdown(report: dict[str, Any], verdict: Verdict) -> str:
    u = report["understanding"]
    pct = lambda x: "n/a" if x is None else f"{x * 100:.1f}%"
    lines = [
        f"# AI evaluation: {'PASS' if verdict.passed else 'FAIL'}",
        "",
        f"Model `{report['model']}`, prompt `{report['prompt_version']}`, {report['cases']} cases.",
        "",
        f"- Category accuracy: **{pct(u['accuracy'])}** (valid output {pct(u['valid_rate'])}, language detection {pct(u['language_detection'])})",
        f"- 'Other' complaints flagged for a person to review: {pct(u['flagged_for_review_when_other'])}",
        "",
        "| Language | Cases | Accuracy |", "|---|---|---|",
        *[f"| {k} | {s['n']} | {pct(s['accuracy'])} |" for k, s in u["by_language"].items()],
        "",
        "| Category | Cases | Accuracy |", "|---|---|---|",
        *[f"| {k} | {s['n']} | {pct(s['accuracy'])} |" for k, s in u["by_category"].items()],
    ]
    if report["mistakes"]:
        lines += ["", "## Mistakes", "", "| Case | Language | Expected | Got |", "|---|---|---|---|",
                  *[f"| {m['id']} | {m['language']} | {m['expected']} | {m['got'] or m['status']} |" for m in report["mistakes"]]]
    if verdict.failures:
        lines += ["", "## Failures", "", *[f"- {f}" for f in verdict.failures]]
    if verdict.notes:
        lines += ["", "## Notes", "", *[f"- {n}" for n in verdict.notes]]
    return "\n".join(lines) + "\n"
