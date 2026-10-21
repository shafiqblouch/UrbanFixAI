import json

from fastapi.testclient import TestClient

from app.config import Settings
from app.main import create_app
from tests.conftest import CATEGORIES, TOKEN

BODY = {"text": "No water since morning in block B", "categories": CATEGORIES}


def reply(**over):
    base = {"category": "water", "summary": "No water supply in block B.", "location_text": "block B", "language": "en", "confidence": 0.9}
    return json.dumps({**base, **over})


def test_health_is_public(client):
    assert client.get("/health").json() == {"status": "ok", "service": "ai"}


def test_v1_requires_the_service_token(client, auth):
    assert client.post("/v1/complaints/understand", json=BODY).status_code == 401
    assert client.post("/v1/complaints/understand", json=BODY, headers={"Authorization": "Bearer nope"}).status_code == 401
    assert client.post("/v1/complaints/understand", json=BODY, headers={"Authorization": f"Basic {TOKEN}"}).status_code == 401
    assert client.get("/v1/providers", headers=auth).status_code == 200


def test_unconfigured_service_rejects_everything():
    c = TestClient(create_app(Settings(service_token="")))
    assert c.get("/v1/providers", headers={"Authorization": "Bearer "}).status_code == 401
    assert c.get("/v1/providers", headers={"Authorization": "Bearer anything"}).status_code == 401


def test_previous_token_is_accepted_during_rotation():
    c = TestClient(create_app(Settings(service_token="new", service_token_previous="old")))
    assert c.get("/v1/providers", headers={"Authorization": "Bearer old"}).status_code == 200
    assert c.get("/v1/providers", headers={"Authorization": "Bearer new"}).status_code == 200


def test_understand_returns_typed_response(client, auth, fake):
    fake.llm_replies.append(reply())
    r = client.post("/v1/complaints/understand", json=BODY, headers=auth)
    assert r.status_code == 200
    body = r.json()
    assert body["category"] == "water"
    assert body["needs_more_info"] is False
    assert body["prompt_version"] == "v1"
    assert body["model"] == "fake-llm-1"


def test_request_validation_rejects_bad_input(client, auth):
    assert client.post("/v1/complaints/understand", json={"text": "", "categories": CATEGORIES}, headers=auth).status_code == 422
    assert client.post("/v1/complaints/understand", json={"text": "x", "categories": []}, headers=auth).status_code == 422
    assert client.post("/v1/complaints/understand", json={"text": "x" * 4001, "categories": CATEGORIES}, headers=auth).status_code == 422
    bad_key = [{"key": "Bad Key!", "label": "x"}]
    assert client.post("/v1/complaints/understand", json={"text": "x", "categories": bad_key}, headers=auth).status_code == 422


def test_pii_never_reaches_the_provider(client, auth, fake):
    fake.llm_replies.append(reply())
    text = "Mr. Tariq Mehmood (0300-1234567, tariq@x.com, CNIC 42101-1234567-1) says no water"
    r = client.post("/v1/complaints/understand", json={**BODY, "text": text, "known_names": ["Tariq Mehmood"]}, headers=auth)
    assert r.status_code == 200
    sent = " ".join(fake.sent_text)
    for secret in ["Tariq", "Mehmood", "0300", "1234567", "tariq@x.com"]:
        assert secret not in sent
    assert r.json()["pii_redactions"] >= 4


def test_invalid_output_is_rejected_and_retried(client, auth, fake):
    fake.llm_replies += ["not json at all", reply(category="plumbing"), reply(confidence=7), reply()]
    r = client.post("/v1/complaints/understand", json=BODY, headers=auth)
    # default max_output_retries = 2 → 3 attempts, all invalid
    assert r.status_code == 502
    assert r.json()["error"] == "invalid_model_output"
    assert len(fake.llm_replies) == 1


def test_second_attempt_can_succeed_and_feedback_is_sent(client, auth, fake):
    fake.llm_replies += ["oops", reply()]
    r = client.post("/v1/complaints/understand", json=BODY, headers=auth)
    assert r.status_code == 200
    assert any("previous reply was rejected" in t for t in fake.sent_text)


def test_json_in_a_code_fence_is_accepted(client, auth, fake):
    fake.llm_replies.append("```json\n" + reply() + "\n```")
    assert client.post("/v1/complaints/understand", json=BODY, headers=auth).status_code == 200


def test_other_category_and_low_confidence_ask_for_more_info(client, auth, fake):
    fake.llm_replies.append(reply(category="other"))
    assert client.post("/v1/complaints/understand", json=BODY, headers=auth).json()["needs_more_info"] is True
    fake.llm_replies.append(reply(confidence=0.2))
    assert client.post("/v1/complaints/understand", json=BODY, headers=auth).json()["needs_more_info"] is True


def test_summary_is_scrubbed_on_the_way_out(client, auth, fake):
    fake.llm_replies.append(reply(summary="Mr. Tariq Mehmood called from 0300-1234567."))
    body = client.post("/v1/complaints/understand", json=BODY, headers=auth).json()
    assert "Tariq" not in body["summary"] and "1234567" not in body["summary"]


def test_provider_failure_maps_to_502(auth):
    from app.providers import Providers
    from app.providers.base import ProviderError

    class Down:
        async def complete(self, **_):
            raise ProviderError("quota")

    c = TestClient(create_app(Settings(service_token=TOKEN), Providers(llm=Down(), vision=Down(), speech=Down(), embedding=Down())))
    r = c.post("/v1/complaints/understand", json=BODY, headers=auth)
    assert r.status_code == 502 and r.json()["error"] == "provider_unavailable"


def test_provider_status_shows_pinned_models(client, auth):
    assert client.get("/v1/providers", headers=auth).json()["llm"] == "fake:fake-llm-1"
