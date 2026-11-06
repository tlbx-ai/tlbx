"""Validate and stage reviewed Git-tracked native bundles without a build toolchain."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import tempfile
import backports

RIDS = ("win-x64", "win-x86", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64")
FILES = ("bundle.zip", "manifest.json", "native-dependencies.spdx.json", "native-backports.vex.json")
ROOT = Path(__file__).resolve().parent


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def recipe_digest(native, rid):
    if rid not in RIDS:
        raise ValueError("Unsupported native runtime identifier")
    pin = json.loads((native / "native-stack.json").read_text(encoding="utf-8-sig"))
    pin = {key: value for key, value in pin.items() if key not in ("grype", "cpeSources")}
    paths = [native / name for name in ("build.ps1", "package.py", "recipe.py", "vcpkg.json", f"triplets/{rid}.cmake")]
    paths += sorted(path for path in (native / "ports").rglob("*") if path.is_file())
    paths.append(native / "source-metadata" / pin["sourceMetadata"]["file"])
    inputs = {path.relative_to(native).as_posix(): hashlib.sha256(path.read_bytes().replace(b"\r\n", b"\n")).hexdigest() for path in paths}
    value = {"version": 1, "runtimeIdentifier": rid, "pin": pin, "files": inputs}
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":")).encode()).hexdigest()


def indexed_files(native, rid, directory):
    if rid not in RIDS:
        raise ValueError("Unsupported native runtime identifier")
    index = json.loads((native / "prebuilt/index.json").read_text(encoding="utf-8-sig"))
    if index.get("version") != 1:
        raise ValueError("Unsupported native prebuilt index version")
    record = index.get("artifacts", {}).get(rid)
    if record is None or record.get("recipeSha256") != recipe_digest(native, rid):
        raise ValueError(f"Pinned native recipe mismatch for {rid}; deliberately rebuild/review and seal its prebuilt artifact")
    if set(record.get("files", {})) != set(FILES):
        raise ValueError("Pinned native index must bind the exact four artifact files")
    for name in FILES:
        path = directory / name
        if path.is_symlink() or not path.is_file() or digest(path) != record["files"][name]:
            raise ValueError(f"Pinned native file integrity mismatch: {rid}/{name}")
    return record


def validate(native, rid, directory=None):
    directory = directory or native / "prebuilt" / rid
    indexed_files(native, rid, directory)
    return backports.validate_frozen_bundle(native, rid, directory)


def seal(native, rid, source=None):
    if rid not in RIDS:
        raise ValueError("Unsupported native runtime identifier")
    directory = native / "prebuilt" / rid
    # Deliberate maintainer operation: validate all proof and ZIP contents before
    # authorizing these bytes. This command never downloads or compiles anything.
    for name in FILES:
        if (directory / name).is_symlink() or not (directory / name).is_file():
            raise ValueError(f"Missing regular prebuilt artifact: {rid}/{name}")
    backports.validate_frozen_bundle(native, rid, directory)
    path = native / "prebuilt/index.json"
    index = json.loads(path.read_text(encoding="utf-8-sig")) if path.is_file() else {"version": 1, "artifacts": {}}
    if index.get("version") != 1:
        raise ValueError("Unsupported native prebuilt index version")
    files = {name: digest(directory / name) for name in FILES}
    previous = index["artifacts"].get(rid, {})
    record = {"recipeSha256": recipe_digest(native, rid), "files": files}
    if source is not None:
        record["source"] = source
    elif previous.get("files") == files and "source" in previous:
        record["source"] = previous["source"]
    index["artifacts"][rid] = record
    path.write_text(json.dumps(index, indent=2, sort_keys=True) + "\n", encoding="utf-8", newline="\n")
    return record


def prepare(native, rid):
    native = native.resolve()
    source = native / "prebuilt" / rid
    validate(native, rid, source)
    destination = native / "bin" / rid

    def check_destination():
        if destination.resolve() != destination:
            raise ValueError("Pinned native staging directory must not redirect through a symlink or junction")

    check_destination()
    destination.mkdir(parents=True, exist_ok=True)
    for name in FILES:
        check_destination()
        target = destination / name
        if not target.is_file() or target.is_symlink() or digest(target) != digest(source / name):
            temporary = None
            try:
                with tempfile.NamedTemporaryFile(dir=destination, prefix=f".{name}.", suffix=".tmp", delete=False) as output:
                    temporary = Path(output.name)
                    with (source / name).open("rb") as input_file:
                        shutil.copyfileobj(input_file, output)
                check_destination()
                temporary.replace(target)
            finally:
                if temporary is not None:
                    temporary.unlink(missing_ok=True)
    indexed_files(native, rid, destination)
    print(f"Verified pinned native {rid}; staged immutable bundle without source build or network.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    action = parser.add_mutually_exclusive_group(required=True)
    action.add_argument("--seal", choices=RIDS)
    action.add_argument("--prepare", choices=RIDS)
    action.add_argument("--validate", choices=RIDS)
    action.add_argument("--fingerprint", choices=RIDS)
    action.add_argument("--validate-all", action="store_true")
    parser.add_argument("--source", type=Path, help="Optional acquisition provenance JSON for --seal")
    args = parser.parse_args()
    if args.seal:
        provenance = json.loads(args.source.read_text(encoding="utf-8-sig")) if args.source else None
        seal(ROOT, args.seal, provenance)
        print(f"Sealed reviewed native {args.seal} in prebuilt/index.json")
    elif args.prepare:
        prepare(ROOT, args.prepare)
    elif args.fingerprint:
        print(recipe_digest(ROOT, args.fingerprint))
    else:
        for rid in RIDS if args.validate_all else (args.validate,):
            validate(ROOT, rid)
            print(f"Verified pinned native {rid}")