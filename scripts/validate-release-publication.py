"""Validate complete release packages and prepare the GitHub publication patch."""
import json
import os
import sys


def validate_release(release, expected_tag):
    platforms = {
        rid: {f"mt-{rid}.{extension}", f"mt-{rid}.spdx.json"}
        for rid, extension in [
            ("linux-arm64", "tar.gz"), ("linux-x64", "tar.gz"),
            ("osx-arm64", "tar.gz"), ("osx-x64", "tar.gz"),
            ("win-x64", "zip"), ("win-x86", "zip"),
        ]
    }
    expected_assets = set().union(*platforms.values())

    if release.get("tagName") != expected_tag:
        raise ValueError(f"Release tag mismatch: expected {expected_tag!r}, got {release.get('tagName')!r}")
    if not isinstance(release.get("isDraft"), bool):
        raise ValueError("Release draft state is missing or invalid.")
    if release.get("isPrerelease") is not ("-dev" in expected_tag):
        raise ValueError("Release prerelease flag does not match its tag.")

    assets = release.get("assets") or []
    actual_names = {asset.get("name") for asset in assets}
    unexpected = actual_names - expected_assets
    if len(assets) != len(actual_names) or unexpected:
        raise ValueError(f"Duplicate or unexpected release assets: {unexpected}")

    empty = sorted(
        asset["name"]
        for asset in assets
        if not isinstance(asset.get("size"), int) or asset["size"] <= 0
    )
    if empty:
        raise ValueError(f"Release assets must be nonzero: {empty}")

    available = []
    unavailable = []
    on_demand = []
    for rid, pair in platforms.items():
        present = pair & actual_names
        if present and present != pair:
            raise ValueError(f"Incomplete platform {rid}: missing {sorted(pair - present)}")
        if rid == "osx-x64" and not present:
            on_demand.append(rid)
        else:
            (available if present else unavailable).append(rid)
    if not available:
        raise ValueError("At least one complete platform archive and matching SBOM is required.")

    release_id = release.get("databaseId")
    if not isinstance(release_id, int) or release_id <= 0:
        raise ValueError(f"Invalid GitHub release id: {release_id!r}")

    on_demand_note = (
        f"On demand: {', '.join(on_demand)} (manual Release workflow, platform=osx-x64).\n\n"
        if on_demand else ""
    )
    availability = (
        "## Platform availability\n\n"
        f"Available: {', '.join(available)}.\n\n"
        f"{on_demand_note}"
        f"Unavailable in this release: {', '.join(unavailable) if unavailable else 'none'}."
    )
    patch = {
        "draft": False,
        "prerelease": release["isPrerelease"],
        "make_latest": "false" if release["isPrerelease"] else "true",
        "body": (release.get("body") or "").split("## Platform availability", 1)[0].rstrip() + "\n\n" + availability,
    }
    return patch


def main():
    release_path, expected_tag, patch_path = sys.argv[1:]
    with open(release_path, encoding="utf-8") as handle:
        release = json.load(handle)
    patch = validate_release(release, expected_tag)
    with open(patch_path, "w", encoding="utf-8") as handle:
        json.dump(patch, handle)
    availability = patch["body"].split("## Platform availability", 1)[1]
    if os.environ.get("GITHUB_STEP_SUMMARY"):
        with open(os.environ["GITHUB_STEP_SUMMARY"], "a", encoding="utf-8") as handle:
            handle.write("## Platform availability" + availability + "\n")
    print(release["databaseId"])


if __name__ == "__main__":
    try:
        main()
    except ValueError as error:
        raise SystemExit(str(error)) from error
