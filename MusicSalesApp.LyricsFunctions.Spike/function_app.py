"""Phase 0: does a PyTorch stack deploy to Flex Consumption, and can it be fed?

**This is a throwaway.** It exists to answer questions before real work is committed to the hosting
choice, and it should be deleted once it has. There is deliberately no storage, no Durable, no
callbacks - so a failure has exactly one possible meaning.

Two endpoints:

* ``/api/health`` - imports the stack and reports what happened. Answers "does it deploy at all".
* ``/api/stage``  - populates the Azure Files mounts with ffmpeg and the model weights.

``/api/stage`` exists because staging from a developer machine turned out to be the wrong shape.
``az storage file upload`` fails with ParentNotFound on the 76 MB ffmpeg binaries (a small file over
the identical command succeeds), and the model weights are well over a gigabyte that would otherwise
travel down a home connection and back up again. Fetching from inside the app instead runs at Azure
network speed, writes straight to the mount, and - the part that actually matters - exercises the
same paths and permissions the real app will use.
"""

from __future__ import annotations

import json
import os
import platform
import resource
import shutil
import stat
import subprocess
import tarfile
import tempfile
import time
import urllib.request

import azure.functions as func

app = func.FunctionApp(http_auth_level=func.AuthLevel.FUNCTION)

_FFMPEG_URL = "https://johnvansickle.com/ffmpeg/releases/ffmpeg-release-amd64-static.tar.xz"
_TOOLS_MOUNT = "/mnt/tools"
_MODELS_MOUNT = "/mnt/models"


@app.route(route="health", methods=["GET"])
def health(req: func.HttpRequest) -> func.HttpResponse:
    """Import the stack and report what happened.

    Imports are inside the handler on purpose. At module scope an import failure surfaces as the
    whole app failing to start with a stack trace buried in the platform logs; here it comes back in
    the response body, which is the difference between an answer and an afternoon.
    """
    started = time.monotonic()
    report: dict[str, object] = {
        "python": platform.python_version(),
        "platform": platform.platform(),
        "cpuCount": os.cpu_count(),
    }

    try:
        import torch

        report["torch"] = torch.__version__
        # The CPU wheel is what makes the package plausible at all. True here means requirements.txt
        # resolved CUDA wheels and the deployment carries over a gigabyte it can never use.
        report["torchCudaBuild"] = torch.version.cuda is not None
        report["threads"] = torch.get_num_threads()
    except Exception as exc:  # noqa: BLE001 - reporting the failure IS the purpose.
        report["torchError"] = f"{type(exc).__name__}: {exc}"

    for name in ("torchaudio", "demucs"):
        try:
            module = __import__(name)
            report[name] = getattr(module, "__version__", "imported, no __version__")
        except Exception as exc:  # noqa: BLE001
            report[f"{name}Error"] = f"{type(exc).__name__}: {exc}"

    report["importSeconds"] = round(time.monotonic() - started, 2)
    report["peakRssMb"] = round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 1)
    report["mounts"] = _describe_mounts()
    report["ffmpeg"] = _describe_ffmpeg()

    ok = "torchError" not in report and "demucsError" not in report
    return _json(ok, report)


@app.route(route="stage", methods=["POST"])
def stage(req: func.HttpRequest) -> func.HttpResponse:
    """Populate the mounts. ``?what=ffmpeg`` or ``?what=models``; both by default.

    Split so each half can be run and judged on its own - the model download is minutes of traffic
    and there is no reason to repeat it while sorting out a binary.
    """
    what = (req.params.get("what") or "all").lower()
    report: dict[str, object] = {"requested": what}

    if what in ("all", "ffmpeg"):
        try:
            report["ffmpegStaging"] = _stage_ffmpeg()
        except Exception as exc:  # noqa: BLE001
            report["ffmpegStagingError"] = f"{type(exc).__name__}: {exc}"

    if what in ("all", "models"):
        try:
            report["modelStaging"] = _stage_models()
        except Exception as exc:  # noqa: BLE001
            report["modelStagingError"] = f"{type(exc).__name__}: {exc}"

    report["mounts"] = _describe_mounts()
    report["ffmpeg"] = _describe_ffmpeg()

    ok = "ffmpegStagingError" not in report and "modelStagingError" not in report
    return _json(ok, report)


def _stage_ffmpeg() -> dict[str, object]:
    """Fetch the static build and put ffmpeg and ffprobe on the tools mount.

    Both binaries, deliberately. prep_audio locates ffprobe by substituting the name in the ffmpeg
    path, so shipping only ffmpeg leaves duration probing silently returning zero - which disables
    the track-length clamp and the "timings ran past the end" check without any error to say so.
    """
    result: dict[str, object] = {}

    with tempfile.TemporaryDirectory() as work:
        archive = os.path.join(work, "ffmpeg.tar.xz")

        started = time.monotonic()
        urllib.request.urlretrieve(_FFMPEG_URL, archive)
        result["downloadSeconds"] = round(time.monotonic() - started, 1)
        result["archiveMb"] = round(os.path.getsize(archive) / 1048576, 1)

        with tarfile.open(archive) as tar:
            wanted = [m for m in tar.getmembers() if os.path.basename(m.name) in ("ffmpeg", "ffprobe")]
            for member in wanted:
                member.name = os.path.basename(member.name)
                tar.extract(member, work)

        os.makedirs(_TOOLS_MOUNT, exist_ok=True)
        for name in ("ffmpeg", "ffprobe"):
            source = os.path.join(work, name)
            target = os.path.join(_TOOLS_MOUNT, name)

            # copyfile, NOT copy2. copy2 preserves metadata, which on this SMB mount raises
            # PermissionError: [Errno 1] Operation not permitted - after the bytes have landed, so
            # the first file appears to succeed and the second never gets copied at all.
            #
            # Nothing is lost by dropping the metadata: the mount presents everything as 0777 and
            # already-executable, so there is no permission to preserve and no chmod to attempt.
            shutil.copyfile(source, target)

    return result


def _stage_models() -> dict[str, object]:
    """Pull the model weights onto the models mount.

    Nothing is downloaded by hand. TORCH_HOME and XDG_CACHE_HOME already point at the mount, so
    asking the libraries to load their models puts the weights exactly where they will be looked for
    later - which is a stronger guarantee than reproducing their cache layout from documentation.
    """
    result: dict[str, object] = {
        "torchHome": os.environ.get("TORCH_HOME"),
        "xdgCacheHome": os.environ.get("XDG_CACHE_HOME"),
    }

    started = time.monotonic()
    import torchaudio

    bundle = torchaudio.pipelines.MMS_FA
    bundle.get_model()
    result["alignerSeconds"] = round(time.monotonic() - started, 1)

    started = time.monotonic()
    from demucs import pretrained

    pretrained.get_model(os.environ.get("DEMUCS_MODEL", "htdemucs"))
    result["demucsSeconds"] = round(time.monotonic() - started, 1)

    return result


def _describe_mounts() -> dict[str, object]:
    out: dict[str, object] = {}
    for label, path in (("tools", _TOOLS_MOUNT), ("models", _MODELS_MOUNT)):
        if not os.path.isdir(path):
            out[label] = "absent"
            continue

        total = 0
        files = 0
        for root, _dirs, names in os.walk(path):
            for name in names:
                try:
                    total += os.path.getsize(os.path.join(root, name))
                    files += 1
                except OSError:
                    pass

        out[label] = {"files": files, "megabytes": round(total / 1048576, 1)}
    return out


def _describe_ffmpeg() -> dict[str, object]:
    """Whether the binary on the mount can actually be run.

    The question the mount raises and nothing else answers: an SMB mount may present the file without
    the executable bit, in which case the real app's subprocess call fails at runtime with a bare
    PermissionError. Better to find that out here than inside a Demucs run.
    """
    path = os.environ.get("FFMPEG_BINARY", "/mnt/tools/ffmpeg")
    if not os.path.isfile(path):
        return {"path": path, "present": False}

    info: dict[str, object] = {
        "path": path,
        "present": True,
        "mode": oct(os.stat(path).st_mode & 0o777),
        "executableBit": os.access(path, os.X_OK),
    }

    try:
        completed = subprocess.run(
            [path, "-version"], capture_output=True, text=True, timeout=30, check=False)
        info["runs"] = completed.returncode == 0
        info["version"] = (completed.stdout or completed.stderr or "").splitlines()[:1]
    except Exception as exc:  # noqa: BLE001
        info["runs"] = False
        info["runError"] = f"{type(exc).__name__}: {exc}"

    return info


def _json(ok: bool, report: dict[str, object]) -> func.HttpResponse:
    return func.HttpResponse(
        json.dumps({"ok": ok, **report}, indent=2, default=str),
        status_code=200 if ok else 500,
        mimetype="application/json",
    )
