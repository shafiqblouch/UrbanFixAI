import json
from pathlib import Path

import pytest

from app.pii import scrub

CORPUS = json.loads((Path(__file__).parent / "pii_corpus.json").read_text(encoding="utf-8"))


@pytest.mark.parametrize("case", CORPUS, ids=[c["text"][:40] for c in CORPUS])
def test_corpus(case):
    result = scrub(case["text"], case["names"])
    for leak in case.get("leaks", []):
        assert leak.lower() not in result.text.lower(), f"{leak!r} leaked: {result.text!r}"
    for keep in case.get("keeps", []):
        assert keep in result.text, f"{keep!r} was wrongly removed: {result.text!r}"


def test_redactions_are_counted_by_kind():
    r = scrub("call 0300-1234567, mail a@b.com, Mr. Tariq Mehmood")
    assert r.counts == {"EMAIL": 1, "PHONE": 1, "NAME": 1}
    assert r.redacted_anything


def test_text_without_pii_is_unchanged():
    r = scrub("the drain outside house 7 is blocked")
    assert r.text == "the drain outside house 7 is blocked"
    assert not r.redacted_anything


def test_known_name_tokens_are_scrubbed_individually_and_case_insensitively():
    r = scrub("RAZA said Ahmed will come", ["Ahmed Raza"])
    assert "raza" not in r.text.lower() and "ahmed" not in r.text.lower()


def test_blank_known_names_are_ignored():
    assert scrub("hello", ["", "  ", "x"]).text == "hello"
