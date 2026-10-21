import json

import httpx
import pytest

from app.config import Settings
from app.pii import ScrubbedText
from app.prompts import load_prompt
from app.providers import build_providers
from app.providers.base import ProviderError
from app.providers.fake import FakeProvider
from app.providers.openrouter import OpenRouterProvider

S = ScrubbedText


def openrouter(handler) -> OpenRouterProvider:
    client = httpx.AsyncClient(base_url="https://or.test/api/v1", transport=httpx.MockTransport(handler))
    return OpenRouterProvider("key", "https://or.test/api/v1", 5, client)


def test_switching_provider_is_configuration_only():
    fake = build_providers(Settings())
    assert isinstance(fake.llm, FakeProvider)
    switched = build_providers(Settings(llm_provider="openrouter", openrouter_api_key="k"))
    assert isinstance(switched.llm, OpenRouterProvider)
    assert isinstance(switched.speech, FakeProvider)


def test_unknown_provider_is_a_clear_error():
    with pytest.raises(ProviderError, match="nonexistent"):
        build_providers(Settings(llm_provider="nonexistent"))
    with pytest.raises(ProviderError):
        build_providers(Settings(speech_provider="openrouter"))  # no speech support yet


def test_openrouter_requires_a_key():
    with pytest.raises(ProviderError):
        build_providers(Settings(llm_provider="openrouter", openrouter_api_key=""))


def test_settings_from_env_reads_overrides_and_pins_prompt_versions():
    s = Settings.from_env({
        "AI_SERVICE_TOKEN": "t", "AI_LLM_PROVIDER": "openrouter", "AI_LLM_MODEL": "vendor/model-x",
        "AI_PROMPT_COMPLAINT_UNDERSTANDING": "v2", "AI_MAX_OUTPUT_RETRIES": "5",
    })
    assert (s.service_token, s.llm_provider, s.llm_model, s.max_output_retries) == ("t", "openrouter", "vendor/model-x", 5)
    assert s.prompt_versions["complaint_understanding"] == "v2"
    assert Settings.from_env({}).llm_provider == "fake"


async def test_openrouter_llm_request_shape_and_parsing():
    seen = {}

    def handler(req: httpx.Request) -> httpx.Response:
        seen["body"] = json.loads(req.content)
        return httpx.Response(200, json={"model": "vendor/m", "choices": [{"message": {"content": "{\"a\":1}"}}],
                                         "usage": {"prompt_tokens": 11, "completion_tokens": 3}})

    r = await openrouter(handler).complete(model="vendor/m", system=S("sys"), user=S("hi"), json_mode=True)
    assert r.text == '{"a":1}' and r.usage.input_tokens == 11 and r.usage.output_tokens == 3
    assert seen["body"]["response_format"] == {"type": "json_object"}
    assert seen["body"]["temperature"] == 0
    assert seen["body"]["model"] == "vendor/m"


async def test_openrouter_vision_embeds_the_image_as_data_url():
    seen = {}

    def handler(req: httpx.Request) -> httpx.Response:
        seen["body"] = json.loads(req.content)
        return httpx.Response(200, json={"choices": [{"message": {"content": "a pothole"}}]})

    r = await openrouter(handler).describe(model="v", prompt=S("describe"), image=b"\x89PNG", mime_type="image/png")
    assert r.text == "a pothole"
    url = seen["body"]["messages"][0]["content"][1]["image_url"]["url"]
    assert url.startswith("data:image/png;base64,")


async def test_openrouter_embeddings_are_returned_in_input_order():
    def handler(req: httpx.Request) -> httpx.Response:
        return httpx.Response(200, json={"data": [{"index": 1, "embedding": [2.0]}, {"index": 0, "embedding": [1.0]}]})

    r = await openrouter(handler).embed(model="e", texts=[S("a"), S("b")])
    assert r.vectors == [[1.0], [2.0]]


@pytest.mark.parametrize("response", [
    httpx.Response(500, json={}),
    httpx.Response(200, text="not json"),
    httpx.Response(200, json={"choices": []}),
])
async def test_openrouter_failures_become_provider_errors(response):
    p = openrouter(lambda req: response)
    with pytest.raises(ProviderError):
        await p.complete(model="m", system=S("s"), user=S("u"), json_mode=False)


async def test_openrouter_bad_embedding_payload():
    p = openrouter(lambda req: httpx.Response(200, json={"data": [{"nope": 1}]}))
    with pytest.raises(ProviderError):
        await p.embed(model="e", texts=[S("a")])


async def test_openrouter_network_error():
    def boom(req):
        raise httpx.ConnectError("down")

    with pytest.raises(ProviderError):
        await openrouter(boom).complete(model="m", system=S("s"), user=S("u"), json_mode=False)


async def test_fake_provider_covers_all_modalities():
    f = FakeProvider()
    assert (await f.describe(model="m", prompt=S("p"), image=b"x", mime_type="image/png")).text
    assert (await f.transcribe(model="m", audio=b"x", mime_type="audio/ogg", language_hint="ur")).language == "ur"
    a, b = (await f.embed(model="m", texts=[S("same"), S("same")])).vectors
    assert a == b


def test_prompts_are_versioned_files_and_path_traversal_is_refused():
    assert "category" in load_prompt("complaint_understanding", "v1")
    with pytest.raises(ValueError):
        load_prompt("complaint_understanding", "../../config")
    with pytest.raises(FileNotFoundError):
        load_prompt("complaint_understanding", "v99")

def test_recommended_model_is_default_when_a_key_is_present():
    s = Settings.from_env({"OPENROUTER_API_KEY": "k"})
    assert (s.llm_provider, s.llm_model) == ("openrouter", "anthropic/claude-haiku-5.5")
    assert s.llm_fallback_model == "deepseek/deepseek-v4.1-flash"
    # environment still overrides the recommendation
    assert Settings.from_env({"OPENROUTER_API_KEY": "k", "AI_LLM_MODEL": "x/y"}).llm_model == "x/y"
    # no key: stays on the hermetic fake provider
    assert Settings.from_env({}).llm_provider == "fake"
