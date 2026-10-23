"""Run the AI evaluation.

    python -m eval                    replay recorded answers, write the report, exit 1 if a threshold fails (what CI runs)
    python -m eval --live             call the model (needs OPENROUTER_API_KEY), refresh the recorded answers
    python -m eval --update-baseline  accept this run's results as the new baseline
    python -m eval --model M          evaluate another model (its answers live in its own cache file)

Exit codes: 0 pass, 1 threshold failed, 2 recorded answers do not cover this run (model, prompt or cases changed: run --live).
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import sys
from dataclasses import replace
from pathlib import Path

from app.config import RECOMMENDED_LLM_MODEL, Settings
from app.providers import build_providers
from app.providers.openrouter import OpenRouterProvider
from eval.harness import (
    HERE,
    AnswerCache,
    CacheMiss,
    RecordingLlm,
    build_report,
    check,
    load_cases,
    render_markdown,
    run_understanding,
)


def load_key() -> str:
    if key := os.environ.get("OPENROUTER_API_KEY"):
        return key
    env = HERE.parent / ".env"
    if env.exists():
        for line in env.read_text(encoding="utf-8").splitlines():
            if line.startswith("OPENROUTER_API_KEY=") and (value := line.split("=", 1)[1].strip()):
                return value
    sys.exit("--live needs OPENROUTER_API_KEY (environment or ai/.env)")


async def run(args: argparse.Namespace) -> int:
    cases = load_cases(Path(args.dataset))
    settings = replace(Settings(), llm_model=args.model, llm_fallback_model="", llm_provider="fake")
    cache = AnswerCache(HERE / "cache" / (args.model.replace("/", "__") + ".json"))
    inner = OpenRouterProvider(load_key(), settings.openrouter_base_url, 90) if args.live else None
    llm = RecordingLlm(cache, inner)
    providers = build_providers(settings)
    providers.llm = llm  # type: ignore[assignment]

    try:
        rows = await run_understanding(cases, providers, settings)
    except CacheMiss as miss:
        print(f"Recorded answers are out of date: {miss}\nRe-run with --live and commit eval/cache/ and eval/baseline.json.", file=sys.stderr)
        return 2
    finally:
        cache.save()

    report = build_report(rows, args.model, settings.prompt_versions["complaint_understanding"], llm.live_calls)
    report["tokens"] = {"input": llm.input_tokens, "output": llm.output_tokens}

    thresholds = json.loads((HERE / "thresholds.json").read_text(encoding="utf-8"))
    baseline_path = HERE / "baseline.json"
    baseline = json.loads(baseline_path.read_text(encoding="utf-8")) if baseline_path.exists() else None
    verdict = check(report, thresholds, baseline)

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    (out / "latest.json").write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    markdown = render_markdown(report, verdict)
    (out / "latest.md").write_text(markdown, encoding="utf-8")
    print(markdown)

    if args.update_baseline:
        baseline_path.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"baseline updated: {baseline_path}", file=sys.stderr)
    return 0 if verdict.passed else 1


def main() -> None:
    ap = argparse.ArgumentParser(prog="python -m eval", description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--live", action="store_true", help="call the model and refresh the recorded answers")
    ap.add_argument("--model", default=os.environ.get("AI_LLM_MODEL", RECOMMENDED_LLM_MODEL))
    ap.add_argument("--dataset", default=str(HERE / "golden.json"))
    ap.add_argument("--out", default=str(HERE / "reports"))
    ap.add_argument("--update-baseline", action="store_true")
    sys.exit(asyncio.run(run(ap.parse_args())))


if __name__ == "__main__":
    main()
