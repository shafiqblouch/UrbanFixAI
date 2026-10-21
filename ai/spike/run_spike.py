"""AI-01 model-selection spike. Runs the production understanding pipeline (scrub -> prompt -> provider -> strict
validation) over a labelled set for each candidate model and reports accuracy, validity, latency and cost.

    python spike/run_spike.py anthropic/claude-haiku-5.5 qwen/qwen3.7-flash ...

Reads OPENROUTER_API_KEY from the environment or ai/.env (never committed).
"""

from __future__ import annotations

import asyncio
import json
import os
import statistics
import sys
import time
from collections import defaultdict
from dataclasses import replace
from pathlib import Path

import httpx

ROOT = Path(__file__).resolve().parent
sys.path.insert(0, str(ROOT.parent))

from app.config import Settings  # noqa: E402
from app.providers import build_providers  # noqa: E402
from app.providers.base import LlmResult, ProviderError  # noqa: E402
from app.schemas import CategoryDef, UnderstandRequest  # noqa: E402
from app.understanding import InvalidModelOutput, understand  # noqa: E402

CATEGORIES = [
    CategoryDef(key="water", label="Water supply", description="no water, low pressure, dirty water"),
    CategoryDef(key="electricity", label="Electricity", description="power, wires, transformers, street lights"),
    CategoryDef(key="sewage_drainage", label="Sewage and drainage", description="sewers, manholes, rain water"),
    CategoryDef(key="roads", label="Roads", description="potholes, speed breakers"),
    CategoryDef(key="garbage", label="Garbage", description="waste collection, dumping"),
    CategoryDef(key="security", label="Security", description="guards, gates, CCTV, theft"),
    CategoryDef(key="noise", label="Noise", description="generators, music, loudspeakers"),
]


def load_key() -> str:
    if key := os.environ.get("OPENROUTER_API_KEY"):
        return key
    env = ROOT.parent / ".env"
    if env.exists():
        for line in env.read_text(encoding="utf-8").splitlines():
            if line.startswith("OPENROUTER_API_KEY="):
                return line.split("=", 1)[1].strip()
    sys.exit("OPENROUTER_API_KEY not set")


async def prices(key: str) -> dict[str, tuple[float, float]]:
    async with httpx.AsyncClient(headers={"Authorization": f"Bearer {key}"}, timeout=30) as c:
        data = (await c.get("https://openrouter.ai/api/v1/models")).json()["data"]
    return {m["id"]: (float(m["pricing"]["prompt"]), float(m["pricing"]["completion"])) for m in data}


async def run_model(model: str, items: list[dict], key: str, price: tuple[float, float], concurrency: int) -> dict:
    settings = replace(Settings(), llm_provider="openrouter", llm_model=model, openrouter_api_key=key, request_timeout_seconds=90)
    providers = build_providers(settings)

    # Wrap the provider to capture usage and wall time per call without changing the pipeline.
    raw = providers.llm
    calls: list[tuple[float, int, int]] = []

    class Metered:
        async def complete(self, **kw) -> LlmResult:
            t0 = time.perf_counter()
            r = await raw.complete(**kw)
            calls.append((time.perf_counter() - t0, r.usage.input_tokens, r.usage.output_tokens))
            return r

    providers.llm = Metered()  # type: ignore[assignment]
    sem = asyncio.Semaphore(concurrency)
    results: list[dict] = []

    async def one(item: dict) -> None:
        async with sem:
            req = UnderstandRequest(text=item["text"], categories=CATEGORIES)
            row = {"id": item["id"], "language": item["language"], "expected": item["category"], "got": None, "status": "ok"}
            try:
                out = await understand(req, providers, settings)
                row["got"] = out.category
                row["lang_ok"] = out.language == item["language"] or (item["language"] in ("ur", "roman_ur") and out.language == "mixed")
            except InvalidModelOutput:
                row["status"] = "invalid_output"
            except ProviderError as exc:
                row["status"] = f"provider_error: {exc}"
            results.append(row)

    await asyncio.gather(*(one(i) for i in items))

    by_lang: dict[str, list[bool]] = defaultdict(list)
    for r in results:
        # "other" is the correct label for unlisted topics; both sides use the same convention.
        by_lang[r["language"]].append(r["got"] == r["expected"])
    latencies = sorted(c[0] for c in calls)
    cost = sum(c[1] * price[0] + c[2] * price[1] for c in calls)
    n = len(results)
    return {
        "model": model,
        "n": n,
        "accuracy": sum(r["got"] == r["expected"] for r in results) / n,
        "by_language": {k: sum(v) / len(v) for k, v in sorted(by_lang.items())},
        "valid_rate": sum(r["status"] == "ok" for r in results) / n,
        "p50_s": statistics.median(latencies) if latencies else None,
        "p95_s": latencies[int(len(latencies) * 0.95) - 1] if latencies else None,
        "cost_usd": cost,
        "cost_per_1000": cost / n * 1000,
        "errors": [r for r in results if r["status"] != "ok"][:3],
        "wrong": [(r["id"], r["expected"], r["got"]) for r in sorted(results, key=lambda r: r["id"]) if r["status"] == "ok" and r["got"] != r["expected"]],
    }


async def main(models: list[str]) -> None:
    key = load_key()
    items = json.loads((ROOT / "dataset.json").read_text(encoding="utf-8"))
    price_table = await prices(key)
    report = []
    for model in models:
        if model not in price_table:
            print(f"skip {model}: not available", file=sys.stderr)
            continue
        print(f"running {model} ...", file=sys.stderr, flush=True)
        report.append(await run_model(model, items, key, price_table[model], concurrency=6))
        r = report[-1]
        print(f"  acc={r['accuracy']:.2f} valid={r['valid_rate']:.2f} p50={r['p50_s']:.1f}s cost=${r['cost_usd']:.4f}", file=sys.stderr, flush=True)
    out = ROOT / "results.json"
    out.write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"wrote {out}", file=sys.stderr)


if __name__ == "__main__":
    asyncio.run(main(sys.argv[1:]))
