import argparse
import hashlib
import json
import shutil
import sys
import tempfile
import urllib.request
from pathlib import Path
from typing import Any, Dict, List


def _load_manifest(path: Path) -> Dict[str, Any]:
    if not path.exists():
        raise RuntimeError(f"Manifest not found: {path}")
    return json.loads(path.read_text(encoding="utf-8"))


def _sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest().lower()


def _normalize_status(model: Dict[str, Any], backend_root: Path) -> Dict[str, Any]:
    missing: List[str] = []
    corrupt: List[str] = []

    for entry in model.get("required_files", []):
        rel_path = entry.get("path", "")
        expected_sha = (entry.get("sha256", "") or "").strip().lower()
        abs_path = backend_root / rel_path
        if not abs_path.exists():
            missing.append(rel_path)
            continue
        if expected_sha:
            actual = _sha256(abs_path)
            if actual != expected_sha:
                corrupt.append(rel_path)

    status = "installed"
    if corrupt:
        status = "corrupt"
    elif missing:
        status = "missing"

    return {
        "id": model.get("id", ""),
        "display_name": model.get("display_name", model.get("id", "")),
        "status": status,
        "install_root": model.get("install_root", ""),
        "missing_files": missing,
        "corrupt_files": corrupt,
    }


def _resolve_url(artifact: Dict[str, Any], base_url: str) -> str:
    url = (artifact.get("url", "") or "").strip()
    if url:
        return url

    rel = (artifact.get("output_path", "") or "").replace("\\", "/").lstrip("/")
    if base_url and rel:
        return f"{base_url.rstrip('/')}/{rel}"

    return ""


def _download_to(url: str, destination: Path) -> None:
    with urllib.request.urlopen(url) as response:
        total = int(response.headers.get("Content-Length") or 0)
        downloaded = 0
        with destination.open("wb") as handle:
            while True:
                chunk = response.read(1024 * 1024)
                if not chunk:
                    break
                handle.write(chunk)
                downloaded += len(chunk)
                if total > 0:
                    pct = (downloaded / total) * 100.0
                    print(f"Downloading... {pct:0.1f}% ({downloaded}/{total} bytes)")
                else:
                    print(f"Downloading... {downloaded} bytes")


def _install_model(manifest: Dict[str, Any], backend_root: Path, model_id: str, base_url: str) -> Dict[str, Any]:
    model = next((item for item in manifest.get("models", []) if item.get("id") == model_id), None)
    if model is None:
        raise RuntimeError(f"Unknown model id: {model_id}")

    artifacts = model.get("artifacts", [])
    if not artifacts:
        raise RuntimeError(f"Model '{model_id}' has no artifacts in manifest.")

    for artifact in artifacts:
        output_rel = artifact.get("output_path", "")
        if not output_rel:
            raise RuntimeError(f"Artifact missing output_path for model '{model_id}'.")

        expected_sha = (artifact.get("sha256", "") or "").strip().lower()
        output_abs = backend_root / output_rel
        output_abs.parent.mkdir(parents=True, exist_ok=True)

        if output_abs.exists():
            if not expected_sha:
                print(f"Skipping {output_rel} (already present)")
                continue

            current_sha = _sha256(output_abs)
            if current_sha == expected_sha:
                print(f"Skipping {output_rel} (already present, checksum OK)")
                continue

        url = _resolve_url(artifact, base_url)
        if not url:
            raise RuntimeError(
                f"Artifact '{artifact.get('name', output_rel)}' has no URL. "
                "Set artifact.url in manifest or provide --base-url."
            )

        print(f"Installing {artifact.get('name', output_rel)} -> {output_rel}")

        with tempfile.TemporaryDirectory(prefix="motiongen_model_dl_") as tmp_dir:
            tmp_path = Path(tmp_dir) / "artifact.bin"
            _download_to(url, tmp_path)

            if expected_sha:
                actual_sha = _sha256(tmp_path)
                if actual_sha != expected_sha:
                    raise RuntimeError(
                        f"Checksum mismatch for {output_rel}. expected={expected_sha} actual={actual_sha}"
                    )

            shutil.move(str(tmp_path), str(output_abs))

    return _normalize_status(model, backend_root)


def main() -> int:
    parser = argparse.ArgumentParser(description="MotionGen model install/status manager")
    subparsers = parser.add_subparsers(dest="command", required=True)

    status_parser = subparsers.add_parser("status", help="Get install status for all models")
    status_parser.add_argument("--manifest", required=True, help="Path to backend manifest")

    install_parser = subparsers.add_parser("install", help="Install a model from manifest artifacts")
    install_parser.add_argument("--manifest", required=True, help="Path to backend manifest")
    install_parser.add_argument("--model", required=True, help="Model id to install")
    install_parser.add_argument(
        "--base-url",
        default="",
        help="Optional base URL used when artifact.url is empty (appended with artifact output_path).",
    )

    args = parser.parse_args()
    manifest_path = Path(args.manifest).resolve()
    backend_root = manifest_path.parent.parent

    try:
        manifest = _load_manifest(manifest_path)
        if args.command == "status":
            payload = {
                "backend_version": manifest.get("version", "unknown"),
                "models": [_normalize_status(model, backend_root) for model in manifest.get("models", [])],
            }
            print(json.dumps(payload))
            return 0

        if args.command == "install":
            status = _install_model(manifest, backend_root, args.model, args.base_url)
            payload = {
                "ok": True,
                "model": args.model,
                "status": status,
            }
            print(json.dumps(payload))
            return 0

        raise RuntimeError(f"Unknown command: {args.command}")
    except Exception as ex:
        print(json.dumps({"ok": False, "error": str(ex)}))
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
