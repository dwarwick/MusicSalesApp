"""Phase 0: does a PyTorch stack deploy to Flex Consumption at all?

**This is a throwaway.** It exists to answer one question before any real work is committed to the
hosting choice, and it should be deleted once it has.

The question is not academic. CPU-only torch is several hundred megabytes installed; torchaudio,
demucs, numpy and scipy take it to roughly a gigabyte. Flex Consumption is zip-deploy rather than
custom containers, so there is a size beyond which nothing can be made to work and the answer becomes
"use Container Apps instead". Finding that out on day one costs an afternoon; finding it out after
the pipeline is written costs the pipeline.

There is deliberately nothing else in here. No storage, no Durable, no callbacks, no ffmpeg - so a
failure has exactly one possible meaning.

Deploy:
    az functionapp create --name streamtunes-lyrics-spike --resource-group <rg> \\
        --flexconsumption-location <region> --runtime python --runtime-version 3.11 \\
        --instance-memory 4096 --maximum-instance-count 1 --storage-account <standard-account>

    func azure functionapp publish streamtunes-lyrics-spike --build remote

Then:
    curl https://streamtunes-lyrics-spike.azurewebsites.net/api/health?code=<key>

Delete the app afterwards. It is not part of the solution and nothing references it.
"""

from __future__ import annotations

import json
import os
import platform
import resource
import time

import azure.functions as func

app = func.FunctionApp(http_auth_level=func.AuthLevel.FUNCTION)


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

        # The single most important line in this file. The CPU wheel is what makes the package
        # plausible at all; if this says True, requirements.txt resolved CUDA wheels and the
        # deployment is carrying well over a gigabyte it can never use - Flex has no GPU.
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

    # Cold-start cost, which decides whether the creator's progress bar sits at "Starting…" for
    # seconds or for minutes. ru_maxrss is bytes on macOS and kilobytes on Linux; Flex is Linux.
    report["importSeconds"] = round(time.monotonic() - started, 2)
    report["peakRssMb"] = round(resource.getrusage(resource.RUSAGE_SELF).ru_maxrss / 1024, 1)

    # Whether the Azure Files mounts are actually reachable. Provisioning attaches them, but Flex's
    # support for that is the other thing worth verifying before relying on it - the model weights
    # cannot live in the package.
    for label, path in (("modelsMount", "/mnt/models"), ("toolsMount", "/mnt/tools")):
        report[label] = "present" if os.path.isdir(path) else "absent"

    ok = "torchError" not in report and "demucsError" not in report

    return func.HttpResponse(
        json.dumps({"ok": ok, **report}, indent=2),
        status_code=200 if ok else 500,
        mimetype="application/json",
    )
