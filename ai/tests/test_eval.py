"""QA-04: the evaluation harness itself. Uses the fake provider, so nothing here calls a model."""

from __future__ import annotations

import json
from dataclasses import replace

import pytest

from app.config import Settings
from app.providers import build_providers
from app.providers.fake import FakeProvider
from eval.harness import (
    AnswerCache,
    CacheMiss,
    Case,
    RecordingLlm,
    build_report,
    cache_key,
    check,
    load_cases,
    render_markdown,
    run_understanding,
)

THRESHOLDS = {
    "min_accuracy": 0.9, "min_accuracy_per_language": 0.8, "min_accuracy_per_category": 0.5, "category_min_cases": 2,
    "min_valid_rate": 0.95, "min_other_flagged": 0.9, "max_regression": 0.05,
}

CASES = [
    Case("a1", "en", "No water since morning", "water", ("clean",)),
    Case("a2", "en", "Tap is dry again", "water", ("clean",)),
    Case("b1", "ur", "بجلی بند ہے", "electricity", ("short",)),
    Case("b2", "roman_ur", "bijli nahi hai", "electricity", ("short",)),
    Case("c1", "en", "Please renew my membership card", "other", ("unlisted",)),
]
ANSWERS = {"No water since morning": "water", "Tap is dry again": "water", "بجلی بند ہے": "electricity", "bijli nahi hai": "water",
           "Please renew my membership card": "other"}


def reply(category: str, confidence: float = 0.9) -> str:
    return json.dumps({"category": category, "summary": "s", "location_text": None, "language": "en", "confidence": confidence})


class Scripted(FakeProvider):
    """Answers each complaint with the category in ANSWERS, found in the fenced user text."""

    async def complete(self, *, model, system, user, json_mode):
        category = next((c for text, c in ANSWERS.items() if text in user), "other")
        self.llm_replies = [reply(category, 0.3 if category == "other" else 0.9)]
        return await super().complete(model=model, system=system, user=user, json_mode=json_mode)


def setup(tmp_path, live: bool):
    settings = replace(Settings(), llm_model="test-model", llm_fallback_model="")
    cache = AnswerCache(tmp_path / "cache.json")
    llm = RecordingLlm(cache, Scripted() if live else None)
    providers = build_providers(settings)
    providers.llm = llm  # type: ignore[assignment]
    return settings, providers, cache, llm


async def test_live_run_records_answers_and_replay_reproduces_the_same_report_without_a_provider(tmp_path):
    settings, providers, cache, llm = setup(tmp_path, live=True)
    rows = await run_understanding(CASES, providers, settings)
    cache.save()
    live = build_report(rows, "test-model", "v3", llm.live_calls)
    assert llm.live_calls == len(CASES)

    settings2, providers2, _, llm2 = setup(tmp_path, live=False)
    replayed = build_report(await run_understanding(CASES, providers2, settings2), "test-model", "v3", llm2.live_calls)
    assert llm2.live_calls == 0
    assert replayed["understanding"] == live["understanding"]
    assert replayed["mistakes"] == live["mistakes"]


async def test_report_breaks_accuracy_down_by_language_category_and_tag(tmp_path):
    settings, providers, _cache, _llm = setup(tmp_path, live=True)
    report = build_report(await run_understanding(CASES, providers, settings), "test-model", "v3")
    u = report["understanding"]

    assert u["accuracy"] == 0.8  # one roman-Urdu electricity complaint was called water
    assert u["by_language"]["roman_ur"] == {"n": 1, "accuracy": 0.0}
    assert u["by_language"]["en"]["accuracy"] == 1.0
    assert u["by_category"]["electricity"] == {"n": 2, "accuracy": 0.5}
    assert u["by_tag"]["short"] == {"n": 2, "accuracy": 0.5}
    assert u["flagged_for_review_when_other"] == 1.0
    assert report["mistakes"] == [{"id": "b2", "language": "roman_ur", "expected": "electricity", "got": "water", "status": "ok"}]
    assert report["confusion"] == [{"expected": "electricity", "got": "water", "count": 1}]
    assert report["coverage"] == {"understanding": 5, "priority": 0, "duplicate": 0, "photo": 0}


async def test_changing_the_model_or_the_case_text_misses_the_cache_and_demands_a_live_rerun(tmp_path):
    settings, providers, cache, _ = setup(tmp_path, live=True)
    await run_understanding(CASES, providers, settings)
    cache.save()

    other_model = replace(settings, llm_model="another-model")
    _, providers2, _, _ = setup(tmp_path, live=False)
    with pytest.raises(CacheMiss):
        await run_understanding(CASES, providers2, other_model)

    edited = [replace(CASES[0], text="No water since yesterday")] + CASES[1:]
    with pytest.raises(CacheMiss):
        await run_understanding(edited, providers2, settings)


def test_cache_key_covers_model_prompt_text_and_mode():
    base = cache_key("m", "system", "user", True)
    assert base == cache_key("m", "system", "user", True)
    assert len({base, cache_key("m2", "system", "user", True), cache_key("m", "system!", "user", True),
                cache_key("m", "system", "user!", True), cache_key("m", "system", "user", False)}) == 5


def report(accuracy=1.0, valid=1.0, by_language=None, by_category=None, flagged=1.0, model="m", prompt="v3"):
    return {
        "model": model, "prompt_version": prompt, "cases": 10,
        "understanding": {
            "accuracy": accuracy, "valid_rate": valid, "language_detection": 1.0,
            "by_language": by_language or {"en": {"n": 5, "accuracy": 1.0}},
            "by_category": by_category or {"water": {"n": 5, "accuracy": 1.0}}, "by_tag": {},
            "flagged_for_review_when_other": flagged, "mean_confidence": 0.9,
        },
        "mistakes": [], "confusion": [], "coverage": {"understanding": 10, "priority": 0, "duplicate": 0, "photo": 0},
    }


def test_thresholds_block_a_release_for_each_kind_of_shortfall():
    assert check(report(), THRESHOLDS, None).passed
    assert not check(report(accuracy=0.85), THRESHOLDS, None).passed
    assert not check(report(valid=0.9), THRESHOLDS, None).passed
    assert not check(report(by_language={"ur": {"n": 5, "accuracy": 0.6}}), THRESHOLDS, None).passed
    assert not check(report(by_category={"noise": {"n": 4, "accuracy": 0.25}}), THRESHOLDS, None).passed
    assert not check(report(flagged=0.5), THRESHOLDS, None).passed
    # a category with too few cases is not held to the per-category bar
    assert check(report(by_category={"noise": {"n": 1, "accuracy": 0.0}}), THRESHOLDS, None).passed


def test_a_drop_against_the_baseline_fails_and_a_model_change_is_noted():
    baseline = report(accuracy=1.0)
    assert not check(report(accuracy=0.92), THRESHOLDS, baseline).passed  # above the floor, but fell 8 points
    swapped = check(report(model="other"), THRESHOLDS, baseline)
    assert swapped.passed and any("changed" in n for n in swapped.notes)
    uncovered = check(report(), THRESHOLDS, None)
    assert any("'photo' has no labelled cases" in n for n in uncovered.notes)  # never silently passes an unmeasured task


def test_markdown_report_states_the_verdict_and_lists_failures():
    bad = check(report(accuracy=0.5), THRESHOLDS, None)
    text = render_markdown(report(accuracy=0.5), bad)
    assert text.startswith("# AI evaluation: FAIL")
    assert "## Failures" in text and "accuracy 0.5" in text
    assert render_markdown(report(), check(report(), THRESHOLDS, None)).startswith("# AI evaluation: PASS")


def test_the_committed_golden_set_is_well_formed_and_covers_every_language_and_hard_cases():
    from eval.harness import HERE

    cases = load_cases(HERE / "golden.json")
    assert len({c.id for c in cases}) == len(cases)
    assert {c.language for c in cases} == {"en", "ur", "roman_ur"}
    assert {"clean", "short", "vague", "typo", "unlisted"} <= {t for c in cases for t in c.tags}
    assert sum(c.category == "other" for c in cases) >= 5
