"""Sign the verified native inputs before embedding them in the release executable."""

import argparse
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import zipfile


def sign(path, identity):
    subprocess.run(
        ["codesign", "--force", "--sign", identity, "--timestamp", str(path)],
        check=True,
    )
    subprocess.run(
        ["codesign", "--verify", "--strict", "--all-architectures", str(path)],
        check=True,
    )


def sign_connect_bundle(source, staging, identity):
    with zipfile.ZipFile(source) as original:
        manifest = json.loads(original.read("manifest.json"))
        with zipfile.ZipFile(staging / "bundle.zip", "x", zipfile.ZIP_DEFLATED) as signed:
            for entry in manifest["files"]:
                name = entry["name"]
                data = original.read(name)
                if hashlib.sha256(data).hexdigest() != entry["sha256"]:
                    raise ValueError(f"Unexpected input hash for {name}")
                if name.endswith(".dylib"):
                    if not re.fullmatch(r"[A-Za-z0-9_-][A-Za-z0-9_.-]*", name):
                        raise ValueError(f"Unexpected Connect library name: {name}")
                    library = staging / name
                    with library.open("xb") as output:
                        output.write(data)
                    sign(library, identity)
                    data = library.read_bytes()
                    entry["sha256"] = hashlib.sha256(data).hexdigest()
                signed.writestr(name, data)
            signed.writestr("manifest.json", json.dumps(manifest, indent=2))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("identity")
    parser.add_argument("staging", type=Path)
    parser.add_argument("bundle", type=Path)
    parser.add_argument("libraries", type=Path, nargs=2)
    args = parser.parse_args()

    for source in args.libraries:
        destination = args.staging / source.name
        with source.open("rb") as library, destination.open("xb") as output:
            shutil.copyfileobj(library, output)
        sign(destination, args.identity)
    sign_connect_bundle(args.bundle, args.staging, args.identity)


if __name__ == "__main__":
    main()
