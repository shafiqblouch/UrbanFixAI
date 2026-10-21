import json
from pathlib import Path

import pytest

from app.language import detect_language

DATASET = json.loads((Path(__file__).parent.parent / "spike" / "dataset.json").read_text(encoding="utf-8"))


def test_accuracy_on_the_labelled_set_meets_the_target():
    wrong = [(d["id"], d["language"], detect_language(d["text"]).language) for d in DATASET
             if detect_language(d["text"]).language != d["language"]]
    accuracy = 1 - len(wrong) / len(DATASET)
    assert accuracy >= 0.95, f"{accuracy:.2%} wrong={wrong}"


@pytest.mark.parametrize("lang", ["en", "roman_ur", "ur"])
def test_each_language_is_recognised_at_least_90_percent(lang):
    items = [d for d in DATASET if d["language"] == lang]
    ok = sum(detect_language(d["text"]).language == lang for d in items)
    assert ok / len(items) >= 0.9


@pytest.mark.parametrize("text,expected", [
    ("The street light is broken", "en"),
    ("No water since yesterday in block C, please send a tanker", "en"),
    ("pani nahi aa raha", "roman_ur"),
    ("bijli chali gayi hai", "roman_ur"),
    ("گلی میں پانی نہیں آ رہا", "ur"),
    ("بجلی کا ٹرانسفارمر خراب ہے", "ur"),
])
def test_short_messages(text, expected):
    assert detect_language(text).language == expected


def test_urdu_script_with_an_english_word_is_still_urdu_and_a_real_mix_is_mixed():
    assert detect_language("ہمارے بلاک کا transformer خراب ہے").language == "ur"
    assert detect_language("ہمارے گھر کے سامنے sewage overflow ہو رہا ہے and the smell is terrible near the school").language == "mixed"


def test_roman_urdu_with_english_technical_words():
    assert detect_language("Transformer se chingariyan nikal rahi hain, bohat khatarnak hai").language == "roman_ur"


def test_empty_and_symbol_only_input_has_zero_confidence():
    for text in ["", "   ", "12345 !!!", "😀"]:
        assert detect_language(text).confidence == 0.0


def test_urdu_digits_do_not_count_as_letters():
    assert detect_language("۰۳۰۰۱۲۳۴۵۶۷").confidence == 0.0
