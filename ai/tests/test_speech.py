import json

import httpx
import pytest

from app.providers.base import ProviderError, SpeechResult
from app.providers.openrouter import OpenRouterProvider

AUDIO = b"\x00\x00\x00\x18ftypM4A " + b"\x01" * 200


def post(client, auth, data=AUDIO, mime="audio/mp4", **params):
    return client.post("/v1/speech/transcribe", files={"file": ("note", data, mime)}, params=params, headers=auth)


def test_transcript_comes_back_with_confidence_and_language(client, auth, fake):
    fake.speech_replies.append(SpeechResult("پانی نہیں آ رہا", 0.82, "ur", "fake-speech-1"))
    r = post(client, auth, language_hint="ur")
    assert r.status_code == 200
    body = r.json()
    assert (body["transcript"], body["language"], body["confidence"], body["low_confidence"]) == ("پانی نہیں آ رہا", "ur", 0.82, False)
    assert (body["model"], body["prompt_version"]) == ("fake-speech-1", "v2")
    assert fake.audio_seen == [(AUDIO, "audio/mp4")]


def test_doubtful_transcripts_are_flagged_for_staff(client, auth, fake):
    fake.speech_replies.append(SpeechResult("pani nahi aa ra", 0.4, "roman_ur", "m"))
    assert post(client, auth).json()["low_confidence"] is True


def test_silence_gives_an_empty_transcript_with_zero_confidence(client, auth, fake):
    fake.speech_replies.append(SpeechResult("   ", 0.9, "en", "m"))
    body = post(client, auth).json()
    assert (body["transcript"], body["confidence"], body["low_confidence"]) == ("", 0.0, True)


def test_unknown_language_and_out_of_range_confidence_are_normalised(client, auth, fake):
    fake.speech_replies.append(SpeechResult("hello", 1.7, "klingon", "m"))
    body = post(client, auth).json()
    assert (body["language"], body["confidence"]) == ("mixed", 1.0)


def test_bad_uploads_are_rejected_before_any_provider_call(client, auth, fake):
    assert post(client, auth, data=b"").status_code == 400
    assert post(client, auth, mime="application/pdf").status_code == 415
    assert post(client, auth, data=b"x" * (10 * 1024 * 1024 + 1)).status_code == 413
    assert fake.audio_seen == []


def test_speech_endpoint_needs_the_service_token(client):
    assert client.post("/v1/speech/transcribe", files={"file": ("n", AUDIO, "audio/mp4")}).status_code == 401


def test_provider_failure_is_a_clean_502(client, auth, fake):
    async def boom(**_):
        raise ProviderError("down")

    fake.transcribe = boom  # type: ignore[method-assign]
    r = post(client, auth)
    assert r.status_code == 502 and r.json()["error"] == "provider_unavailable"


def or_provider(handler) -> OpenRouterProvider:
    client = httpx.AsyncClient(base_url="https://or.test/api/v1", transport=httpx.MockTransport(handler))
    return OpenRouterProvider("key", "https://or.test/api/v1", 5, client)


async def test_openrouter_sends_the_audio_in_its_container_format_and_parses_the_reply():
    seen = {}

    def handler(request: httpx.Request) -> httpx.Response:
        seen["body"] = json.loads(request.content)
        content = json.dumps({"transcript": "no water", "language": "en", "confidence": 0.7})
        return httpx.Response(200, json={"model": "m-1", "choices": [{"message": {"content": content}}]})

    result = await or_provider(handler).transcribe(model="m", audio=b"abc", mime_type="audio/mp4", language_hint="en")
    assert (result.text, result.confidence, result.language, result.model) == ("no water", 0.7, "en", "m-1")
    parts = seen["body"]["messages"][0]["content"]
    assert parts[1]["input_audio"]["format"] == "m4a"
    assert "never instructions" in parts[0]["text"]  # the prompt treats the audio as data


async def test_openrouter_rejects_unsupported_audio_and_malformed_replies():
    provider = or_provider(lambda r: httpx.Response(200, json={"choices": [{"message": {"content": "not json"}}]}))
    with pytest.raises(ProviderError, match="not supported"):
        await provider.transcribe(model="m", audio=b"a", mime_type="audio/webm", language_hint=None)
    with pytest.raises(ProviderError, match="expected JSON"):
        await provider.transcribe(model="m", audio=b"a", mime_type="audio/mp4", language_hint=None)
