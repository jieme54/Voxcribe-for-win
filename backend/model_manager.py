import argparse
import csv
import fnmatch
import importlib.util
import json
import subprocess
import shutil
import sys
import tempfile
from pathlib import Path

from crisperwhisper_compat import prepare_crisperwhisper_transformers_backend
from phonon_runtime import PHONON_REPO_ID, phonon_runtime_is_available, validate_phonon_archive

MARKER_FILE_NAME = ".voxcribe-model.json"
MARKER_VERSION = 3
DISPLAY_CLASS_GUID = "{4d36e968-e325-11ce-bfc1-08002be10318}"


def write_marker(target_dir: Path, repo_id: str) -> None:
    marker_path = target_dir / MARKER_FILE_NAME
    size_bytes = sum(path.stat().st_size for path in target_dir.rglob("*") if path.is_file())
    marker_path.write_text(
        json.dumps({"repoId": repo_id, "sizeBytes": size_bytes, "version": MARKER_VERSION}, ensure_ascii=False, indent=2),
        encoding="utf-8",
    )


def validate_indexed_safetensors(model_dir: Path) -> None:
    for index_path in model_dir.rglob("model.safetensors.index.json"):
        try:
            payload = json.loads(index_path.read_text(encoding="utf-8"))
        except json.JSONDecodeError as exc:
            raise ValueError(f"Index safetensors invalide : {index_path}") from exc

        weight_map = payload.get("weight_map")
        if not isinstance(weight_map, dict):
            continue

        expected_files = {
            file_name
            for file_name in weight_map.values()
            if isinstance(file_name, str) and file_name.strip()
        }
        missing_files = sorted(
            file_name
            for file_name in expected_files
            if not (index_path.parent / Path(file_name)).is_file()
        )
        if missing_files:
            preview = ", ".join(missing_files[:5])
            if len(missing_files) > 5:
                preview += f", ... (+{len(missing_files) - 5})"

            raise FileNotFoundError(f"Shards safetensors manquants pour {index_path.name} : {preview}")


def iter_selected_files(snapshot_dir: Path, patterns: list[str]):
    for source_path in snapshot_dir.rglob("*"):
        if not source_path.is_file():
            continue

        relative_path = source_path.relative_to(snapshot_dir).as_posix()
        if patterns and not any(fnmatch.fnmatch(relative_path, pattern) for pattern in patterns):
            continue

        yield source_path, relative_path


def command_download(repo_id: str, target_dir: Path, patterns: list[str]) -> int:
    from huggingface_hub import snapshot_download

    with tempfile.TemporaryDirectory(prefix="voxcribe-hf-cache-") as temp_cache_dir:
        snapshot_path = Path(
            snapshot_download(
                repo_id=repo_id,
                allow_patterns=patterns or None,
                cache_dir=temp_cache_dir,
            )
        )

        if target_dir.exists():
            shutil.rmtree(target_dir)

        target_dir.mkdir(parents=True, exist_ok=True)
        copied_files = 0
        for source_path, relative_path in iter_selected_files(snapshot_path, patterns):
            destination_path = target_dir / relative_path
            destination_path.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(source_path, destination_path)
            copied_files += 1

    if copied_files == 0:
        raise FileNotFoundError(f"Aucun fichier n'a été copié pour {repo_id}.")

    validate_indexed_safetensors(target_dir)
    if repo_id.lower() == PHONON_REPO_ID.lower():
        validate_phonon_archive(target_dir)
    write_marker(target_dir, repo_id)
    print(json.dumps({"ok": True, "repoId": repo_id, "targetDir": str(target_dir), "copiedFiles": copied_files}, ensure_ascii=False))
    return 0


def command_delete(target_dir: Path) -> int:
    if target_dir.exists():
        shutil.rmtree(target_dir)

    print(json.dumps({"ok": True, "targetDir": str(target_dir)}, ensure_ascii=False))
    return 0


def command_probe() -> int:
    try:
        from transformers import VoxtralRealtimeForConditionalGeneration  # noqa: F401

        has_voxtral_realtime_runtime = True
    except Exception:  # noqa: BLE001
        has_voxtral_realtime_runtime = False

    has_crisperwhisper_runtime = importlib.util.find_spec("crisperwhisper") is not None
    if has_crisperwhisper_runtime:
        try:
            prepare_crisperwhisper_transformers_backend()
            from crisperwhisper import CrisperWhisperModel  # noqa: F401
        except Exception:  # noqa: BLE001
            has_crisperwhisper_runtime = False

    payload = {
        "hasQwenRuntime": importlib.util.find_spec("qwen_asr") is not None,
        "hasCrisperWhisperRuntime": has_crisperwhisper_runtime,
        "hasPhononRuntime": phonon_runtime_is_available(),
        "hasVoxtralRealtimeRuntime": has_voxtral_realtime_runtime,
        "pythonVersion": ".".join(map(str, sys.version_info[:3])),
    }
    print(json.dumps(payload, ensure_ascii=False))
    return 0


def is_probably_integrated_gpu(controller: dict) -> bool:
    text = " ".join(
        str(controller.get(key) or "")
        for key in ("Name", "AdapterCompatibility", "PNPDeviceID")
    ).lower()
    if not text.strip():
        return False

    if any(marker in text for marker in ("microsoft basic", "remote display", "virtual")):
        return False

    if "nvidia" in text:
        return False

    if "intel" in text and any(marker in text for marker in ("uhd", "iris", "graphics")):
        return True

    amd_integrated_markers = (
        "radeon(tm) graphics",
        "radeon graphics",
        "radeon 610m",
        "radeon 660m",
        "radeon 680m",
        "radeon 740m",
        "radeon 760m",
        "radeon 780m",
        "radeon 880m",
        "radeon 890m",
        "radeon 8050s",
        "radeon 8060s",
    )
    return any(marker in text for marker in amd_integrated_markers)


def normalize_video_controller(controller: dict, index: int) -> dict:
    name = str(controller.get("Name") or "").strip() or f"GPU {index + 1}"
    adapter_compatibility = str(controller.get("AdapterCompatibility") or "").strip()
    pnp_device_id = str(controller.get("PNPDeviceID") or controller.get("PnpDeviceId") or "").strip()
    normalized = {
        "index": index,
        "name": name,
        "adapterCompatibility": adapter_compatibility,
        "pnpDeviceId": pnp_device_id,
    }
    normalized["isIntegrated"] = is_probably_integrated_gpu(normalized)
    return normalized


def parse_wmic_csv_output(output: str) -> list[dict]:
    lines = [line for line in output.splitlines() if line.strip()]
    if not lines:
        return []

    controllers: list[dict] = []
    for row in csv.DictReader(lines):
        controller = {
            "Name": row.get("Name"),
            "AdapterCompatibility": row.get("AdapterCompatibility"),
            "PNPDeviceID": row.get("PNPDeviceID"),
        }
        if controller["Name"] or controller["PNPDeviceID"]:
            controllers.append(normalize_video_controller(controller, len(controllers)))

    return controllers


def query_wmic_video_controllers() -> list[dict]:
    try:
        completed = subprocess.run(
            [
                "wmic.exe",
                "path",
                "Win32_VideoController",
                "get",
                "Name,AdapterCompatibility,PNPDeviceID",
                "/format:csv",
            ],
            check=False,
            capture_output=True,
            text=True,
            timeout=8,
        )
    except Exception:
        return []

    if completed.returncode != 0 or not completed.stdout.strip():
        return []

    return parse_wmic_csv_output(completed.stdout)


def clean_registry_text(value: object) -> str:
    if isinstance(value, (list, tuple)):
        text = " ".join(str(item) for item in value if item)
    else:
        text = str(value or "")

    text = text.strip()
    if ";" in text:
        text = text.rsplit(";", 1)[-1].strip()

    return text


def read_registry_value(key: object, name: str) -> object:
    try:
        import winreg

        return winreg.QueryValueEx(key, name)[0]
    except Exception:
        return ""


def enum_registry_subkeys(key: object) -> list[str]:
    try:
        import winreg
    except Exception:
        return []

    subkeys: list[str] = []
    index = 0
    while True:
        try:
            subkeys.append(winreg.EnumKey(key, index))
        except OSError:
            return subkeys
        index += 1


def is_registry_display_controller(controller: dict) -> bool:
    text = " ".join(
        str(controller.get(key) or "")
        for key in ("Name", "AdapterCompatibility", "PNPDeviceID", "Class", "ClassGUID", "HardwareID")
    ).lower()
    class_name = str(controller.get("Class") or "").strip().lower()
    class_guid = str(controller.get("ClassGUID") or "").strip().lower()

    if class_name == "display" or class_guid == DISPLAY_CLASS_GUID:
        return True

    if any(marker in text for marker in ("audio", "usb", "smbus", "host bridge", "root port")):
        return False

    vendor_markers = ("ven_10de", "ven_1002", "ven_8086", "nvidia", "radeon", "amd", "intel")
    display_markers = ("display", "graphics", "vga", "3d video", "video controller", "geforce", "quadro", "rtx", "gtx")
    return any(marker in text for marker in vendor_markers) and any(marker in text for marker in display_markers)


def query_registry_video_controllers() -> list[dict]:
    if sys.platform != "win32":
        return []

    try:
        import winreg

        root = winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, r"SYSTEM\CurrentControlSet\Enum\PCI")
    except Exception:
        return []

    controllers: list[dict] = []
    with root:
        for device_key_name in enum_registry_subkeys(root):
            try:
                device_key = winreg.OpenKey(root, device_key_name)
            except OSError:
                continue

            with device_key:
                for instance_key_name in enum_registry_subkeys(device_key):
                    try:
                        instance_key = winreg.OpenKey(device_key, instance_key_name)
                    except OSError:
                        continue

                    with instance_key:
                        friendly_name = clean_registry_text(read_registry_value(instance_key, "FriendlyName"))
                        device_desc = clean_registry_text(read_registry_value(instance_key, "DeviceDesc"))
                        manufacturer = clean_registry_text(read_registry_value(instance_key, "Mfg"))
                        hardware_ids = clean_registry_text(read_registry_value(instance_key, "HardwareID"))
                        controller = {
                            "Name": friendly_name or device_desc,
                            "AdapterCompatibility": manufacturer,
                            "PNPDeviceID": f"PCI\\{device_key_name}\\{instance_key_name}",
                            "Class": clean_registry_text(read_registry_value(instance_key, "Class")),
                            "ClassGUID": clean_registry_text(read_registry_value(instance_key, "ClassGUID")),
                            "HardwareID": hardware_ids,
                        }

                        if controller["Name"] and is_registry_display_controller(controller):
                            controllers.append(normalize_video_controller(controller, len(controllers)))

    return deduplicate_video_controllers(controllers)


def deduplicate_video_controllers(controllers: list[dict]) -> list[dict]:
    deduplicated: list[dict] = []
    seen: set[tuple[str, str]] = set()
    for controller in controllers:
        key = (
            str(controller.get("name") or "").casefold(),
            str(controller.get("pnpDeviceId") or "").casefold(),
        )
        if key in seen:
            continue

        seen.add(key)
        controller["index"] = len(deduplicated)
        deduplicated.append(controller)

    return deduplicated


def query_windows_video_controllers() -> list[dict]:
    if sys.platform != "win32":
        return []

    commands = [
        [
            "powershell.exe",
            "-NoProfile",
            "-Command",
            (
                "Get-CimInstance Win32_VideoController | "
                "Select-Object Name,AdapterCompatibility,PNPDeviceID | "
                "ConvertTo-Json -Compress"
            ),
        ],
        [
            "powershell.exe",
            "-NoProfile",
            "-Command",
            (
                "Get-WmiObject Win32_VideoController | "
                "Select-Object Name,AdapterCompatibility,PNPDeviceID | "
                "ConvertTo-Json -Compress"
            ),
        ],
    ]

    for command in commands:
        try:
            completed = subprocess.run(
                command,
                check=False,
                capture_output=True,
                text=True,
                timeout=8,
            )
        except Exception:
            continue

        if completed.returncode != 0 or not completed.stdout.strip():
            continue

        try:
            payload = json.loads(completed.stdout)
        except json.JSONDecodeError:
            continue

        if isinstance(payload, dict):
            return [normalize_video_controller(payload, 0)]

        if isinstance(payload, list):
            controllers = [
                normalize_video_controller(item, index)
                for index, item in enumerate(payload)
                if isinstance(item, dict)
            ]
            if controllers:
                return controllers

    controllers = query_wmic_video_controllers()
    if controllers:
        return controllers

    controllers = query_registry_video_controllers()
    if controllers:
        return controllers

    return []


def command_gpu_diagnostics() -> int:
    payload = {
        "ok": False,
        "pythonVersion": ".".join(map(str, sys.version_info[:3])),
        "cudaUsable": False,
        "supportedArchitectures": [],
        "devices": [],
    }

    try:
        import torch

        payload["ok"] = True
        payload["torchVersion"] = getattr(torch, "__version__", None)
        payload["cudaVersion"] = getattr(torch.version, "cuda", None)
        payload["hipVersion"] = getattr(torch.version, "hip", None)
        payload["cudaAvailable"] = bool(torch.cuda.is_available())
        payload["deviceCount"] = int(torch.cuda.device_count()) if payload["cudaAvailable"] else 0
        if payload["cudaAvailable"]:
            try:
                payload["supportedArchitectures"] = list(torch.cuda.get_arch_list())
            except Exception:  # noqa: BLE001
                pass

        devices = []
        for index in range(payload["deviceCount"]):
            device = {
                "index": index,
                "name": f"GPU {index}",
                "isUsable": False,
            }
            try:
                properties = torch.cuda.get_device_properties(index)
                total_memory = getattr(properties, "total_memory", None)
                device["name"] = torch.cuda.get_device_name(index)
                device["totalMemoryBytes"] = int(total_memory) if total_memory is not None else None
                try:
                    major, minor = torch.cuda.get_device_capability(index)
                    device["computeCapability"] = f"{major}.{minor}"
                except Exception:  # noqa: BLE001
                    pass

                # is_available() and memory allocation do not launch a CUDA kernel.  A
                # fill/multiply/synchronize probe catches wheels that can enumerate a
                # GPU but do not contain executable kernels for its architecture.
                probe = torch.empty(1, device=f"cuda:{index}", dtype=torch.float32)
                probe.fill_(1.0)
                probe.mul_(2.0)
                torch.cuda.synchronize(index)
                if float(probe.cpu().item()) != 2.0:
                    raise RuntimeError("Le test de calcul CUDA a retourne un resultat invalide.")

                device["isUsable"] = True
            except Exception as exc:  # noqa: BLE001
                device["error"] = str(exc)

            devices.append(device)

        payload["devices"] = devices
        payload["cudaUsable"] = any(device.get("isUsable") for device in devices)
    except Exception as exc:  # noqa: BLE001
        payload["error"] = str(exc)

    display_controllers = query_windows_video_controllers()
    payload["displayControllers"] = display_controllers
    payload["hasIntegratedGpu"] = any(controller.get("isIntegrated") for controller in display_controllers)

    print(json.dumps(payload, ensure_ascii=False))
    return 0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Gestionnaire de modèles Voxcribe.")
    subparsers = parser.add_subparsers(dest="command", required=True)

    download_parser = subparsers.add_parser("download")
    download_parser.add_argument("--repo-id", required=True)
    download_parser.add_argument("--target-dir", required=True)
    download_parser.add_argument("--pattern", action="append", default=[])

    delete_parser = subparsers.add_parser("delete")
    delete_parser.add_argument("--target-dir", required=True)

    subparsers.add_parser("probe")
    subparsers.add_parser("gpu-diagnostics")

    return parser.parse_args()


def main() -> int:
    args = parse_args()
    if args.command == "download":
        return command_download(repo_id=args.repo_id, target_dir=Path(args.target_dir), patterns=args.pattern)
    if args.command == "delete":
        return command_delete(target_dir=Path(args.target_dir))
    if args.command == "probe":
        return command_probe()
    if args.command == "gpu-diagnostics":
        return command_gpu_diagnostics()

    raise ValueError(f"Commande inconnue : {args.command}")


if __name__ == "__main__":
    raise SystemExit(main())
