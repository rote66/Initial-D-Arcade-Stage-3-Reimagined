#!/usr/bin/env python3
"""Run existing game diagnostics in a booted iOS Simulator with private saves."""
import argparse
import json
from pathlib import Path
import shutil
import subprocess
import time
from datetime import datetime

ROOT = Path(__file__).resolve().parents[1]
BUNDLE = "org.idas3.reimagined.ios"
CASES = {
    "data": ("-idas3-data-checks", []),
    # Desktop red-header sampling assumes no virtual controls over the title.
    # Mobile visual QA uses the captured window; this case checks playback/I/O.
    "soundroom": ("-idas3-race-music-smoke", ["-idas3-sound-room-check", "-idas3-music-no-capture"]),
    "mobile": ("-idas3-mobile-controls-smoke", []),
    "render": ("-idas3-scene-smoke", ["-idas3-scene-render-check"]),
    "audio": ("-idas3-scene-smoke", ["-idas3-scene-realtime-audio"]),
    "pause": ("-idas3-pause-smoke", ["-idas3-pause-no-capture"]),
    "courses": ("-idas3-mode-flow-smoke", ["-idas3-pause-course-check"]),
    "tsubaki": ("-idas3-mode-flow-smoke", ["-idas3-tsubaki-check"]),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--device", default="booted")
    parser.add_argument("--cases", nargs="+", choices=CASES, default=list(CASES))
    parser.add_argument("--disable-engine-audio", action="store_true",
                        help="Use Unity's diagnostic -disableaudio startup flag for UI tests when Simulator CoreAudio is unavailable.")
    parser.add_argument("--preferred-language", choices=["en", "zh-Hans", "zh-Hant", "ja"],
                        help="Override AppleLanguages for this process only; ja checks the unsupported-language English fallback.")
    args = parser.parse_args()
    if args.disable_engine_audio and any(name in {"audio", "soundroom"} for name in args.cases):
        parser.error("Audio cases require the normal engine audio path.")
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    output = ROOT / "Builds/iOS/Simulator/Tests" / stamp
    output.mkdir(parents=True)
    (output / "run.json").write_text(json.dumps({
        "device": args.device, "cases": args.cases,
        "engineAudioDisabled": args.disable_engine_audio,
        "preferredLanguage": args.preferred_language,
        "scope": "Private-save Simulator diagnostics; physical-device audio and sensors are not assessed.",
    }, indent=2))

    def simctl(*parts, **kwargs):
        return subprocess.run(["xcrun", "simctl", *parts], check=True, **kwargs)

    container = Path(simctl("get_app_container", args.device, BUNDLE, "data",
                            capture_output=True, text=True).stdout.strip())
    results = {}
    try:
        for name in args.cases:
            test_root = container / "Documents" / f"simulator-test-{stamp}-{name}"
            flag, extra = CASES[name]
            report_path = test_root / "report.json"
            with (output / f"{name}.log").open("w") as log:
                proc = subprocess.Popen([
                    "xcrun", "simctl", "launch", "--console", "--terminate-running-process",
                    args.device, BUNDLE, flag, str(test_root), *extra,
                    *(["-disableaudio"] if args.disable_engine_audio else []),
                    *(["-AppleLanguages", "(" + args.preferred_language + ")"] if args.preferred_language else []),
                ], stdout=log, stderr=subprocess.STDOUT)
                try:
                    deadline = time.monotonic() + 180
                    while not report_path.exists() and time.monotonic() < deadline:
                        if proc.poll() is not None:
                            raise RuntimeError(f"{name} exited without a report; see {log.name}")
                        time.sleep(1)
                    if not report_path.exists():
                        raise RuntimeError(f"{name} timed out; see {log.name}")
                    report = json.loads(report_path.read_text())
                    if args.preferred_language:
                        expected = "en" if args.preferred_language == "ja" else args.preferred_language
                        log.flush()
                        actual = (output / f"{name}.log").read_text(errors="replace")
                        if "IDAS3 UI language: " + expected not in actual:
                            raise RuntimeError(f"Device language resolution did not produce {expected}; see {log.name}")
                        report["preferredLanguage"] = args.preferred_language
                        report["resolvedLanguage"] = expected
                        report_path.write_text(json.dumps(report, indent=2))
                    results[name] = report
                    shutil.copytree(test_root, output / name)
                    print(f"{name}: {'PASS' if report['passed'] else 'FAIL'} "
                          f"({report.get('checks', 0)} checks)", flush=True)
                    if not report["passed"]:
                        print(report.get("error"), flush=True)
                finally:
                    subprocess.run(["xcrun", "simctl", "terminate", args.device, BUNDLE],
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                    try:
                        proc.wait(timeout=10)
                    except subprocess.TimeoutExpired:
                        proc.terminate()
                        proc.wait(timeout=10)
    finally:
        (output / "results.json").write_text(json.dumps(results, indent=2))
        simctl("launch", args.device, BUNDLE, *(["-disableaudio"] if args.disable_engine_audio else []))
        print(f"Results: {output}", flush=True)
    return 0 if len(results) == len(args.cases) and all(r["passed"] for r in results.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
