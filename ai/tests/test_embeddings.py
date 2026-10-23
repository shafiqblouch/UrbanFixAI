import httpx
import pytest

from app.config import Settings
from app.main import create_app
from app.providers import Providers, build_providers
from app.providers.base import ProviderError
from app.providers.openrouter import OpenRouterProvider
from tests.conftest import TOKEN


def test_embeddings_come_back_in_order_with_their_dimension(client, auth):
    r = client.post("/v1/embeddings", json={"texts": ["no water", "street light out"]}, headers=auth)
    assert r.status_code == 200
    body = r.json()
    assert (len(body["vectors"]), body["dimensions"], body["model"]) == (2, 8, "fake-embed-1")
    assert body["vectors"][0] != body["vectors"][1]


def test_the_same_text_always_gives_the_same_vector(client, auth):
    a = client.post("/v1/embeddings", json={"texts": ["pothole on road"]}, headers=auth).json()["vectors"][0]
    b = client.post("/v1/embeddings", json={"texts": ["pothole on road"]}, headers=auth).json()["vectors"][0]
    assert a == b


def test_personal_details_never_reach_the_embedding_provider(client, auth, fake):
    body = {"texts": ["Call Ahmed Khan on 0300 1234567, tap broken"], "known_names": ["Ahmed Khan"]}
    r = client.post("/v1/embeddings", json=body, headers=auth)
    assert r.status_code == 200 and r.json()["pii_redactions"] >= 2
    sent = " ".join(fake.sent_text)
    assert "Ahmed" not in sent and "1234567" not in sent


def test_input_limits(client, auth):
    assert client.post("/v1/embeddings", json={"texts": []}, headers=auth).status_code == 422
    assert client.post("/v1/embeddings", json={"texts": ["x"] * 33}, headers=auth).status_code == 422
    assert client.post("/v1/embeddings", json={"texts": ["x"]}).status_code == 401


def test_without_a_chosen_provider_it_fails_loudly_not_with_fake_vectors(fake):
    providers = build_providers(Settings(service_token=TOKEN))
    assert providers.embedding.__class__.__name__ == "Unconfigured"
    from fastapi.testclient import TestClient

    app = create_app(Settings(service_token=TOKEN), providers)
    r = TestClient(app).post("/v1/embeddings", json={"texts": ["x"]}, headers={"Authorization": f"Bearer {TOKEN}"})
    assert r.status_code == 502 and "No embedding provider" in r.json()["detail"]


def test_mismatched_vector_shapes_are_refused(fake, auth):
    from fastapi.testclient import TestClient

    from app.providers.base import EmbeddingResult

    async def bad(**_):
        return EmbeddingResult([[0.1, 0.2], [0.3]], "m")

    fake.embed = bad  # type: ignore[method-assign]
    app = create_app(Settings(service_token=TOKEN), Providers(llm=fake, vision=fake, speech=fake, embedding=fake))
    r = TestClient(app).post("/v1/embeddings", json={"texts": ["a", "b"]}, headers=auth)
    assert r.status_code == 502


async def test_openrouter_embeddings_keep_input_order():
    def handler(request: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"model": "m", "data": [{"index": 1, "embedding": [2.0]}, {"index": 0, "embedding": [1.0]}]})

    client = httpx.AsyncClient(base_url="https://or.test/api/v1", transport=httpx.MockTransport(handler))
    result = await OpenRouterProvider("k", "https://or.test/api/v1", 5, client).embed(model="m", texts=["a", "b"])
    assert result.vectors == [[1.0], [2.0]]
    with pytest.raises(ProviderError):
        await OpenRouterProvider("k", "https://or.test/api/v1", 5, httpx.AsyncClient(
            base_url="https://or.test/api/v1", transport=httpx.MockTransport(lambda r: httpx.Response(200, json={})))).embed(model="m", texts=["a"])
