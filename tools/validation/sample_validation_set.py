"""Draws the GD7 validation set (PLAN GD7 step 1) from the technical-eligible preflight manifests.

The strict-eligible manifests are empty (no audited revision map yet), so Chinh chose the technical-eligible ones on 14/11/2026.
Repositories are grouped by (repo_url, checkout_sha), sorted, then sampled with a fixed seed; up to CLASSES_PER_REPO classes are
sampled per repository. Only the fields needed to rerun are kept: no machine paths or logs.

Usage: python tools/validation/sample_validation_set.py <preflight_manifest dir> docs/validation-set.json
"""
import hashlib
import json
import random
import sys
from pathlib import Path

SEED = 20261114
REPOS = 20
CLASSES_PER_REPO = 3
MANIFEST = "technical_eligible_manifest.jsonl"


def main(manifest_dir: str, out: str) -> None:
    root = Path(manifest_dir)
    shards = sorted(p for p in root.iterdir() if (p / MANIFEST).is_file())
    groups: dict[tuple[str, str], list[dict]] = {}
    sources = []
    for shard in shards:
        data = (shard / MANIFEST).read_bytes()
        sources.append({"shard": shard.name, "file": MANIFEST, "sha256": hashlib.sha256(data).hexdigest()})
        for line in data.decode("utf-8").splitlines():
            if not line.strip():
                continue
            r = json.loads(line)
            if not r.get("technical_eligible"):
                continue
            groups.setdefault((r["repo_url"], r["checkout_sha"]), []).append({
                "shard": shard.name,
                "task_id": r["task_id"],
                "class_fqn": r["class_fqn"],
                "class_path": r["class_path"],
                "module_path": r.get("module_path") or ".",
                "build_tool": r["build_tool"],
                "testing_framework": r.get("testing_framework"),
                "java_version": r.get("java_version"),
            })

    rng = random.Random(SEED)
    keys = rng.sample(sorted(groups), min(REPOS, len(groups)))
    repos = []
    for repo_url, sha in keys:
        classes = sorted({c["class_fqn"]: c for c in groups[(repo_url, sha)]}.values(), key=lambda c: c["class_fqn"])
        picked = sorted(rng.sample(classes, min(CLASSES_PER_REPO, len(classes))), key=lambda c: c["class_fqn"])
        repos.append({"repo_url": repo_url, "checkout_sha": sha, "build_tool": picked[0]["build_tool"], "classes": picked})

    result = {
        "seed": SEED,
        "source": "technical_eligible_manifest.jsonl (strict_eligible is empty in every shard)",
        "sources": sources,
        "repositories_available": len(groups),
        "repositories": repos,
    }
    Path(out).write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"{len(repos)} repositories, {sum(len(r['classes']) for r in repos)} classes, from {len(groups)} available")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
