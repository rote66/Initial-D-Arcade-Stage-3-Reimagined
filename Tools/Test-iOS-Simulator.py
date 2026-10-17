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
    "mobile-render": ("-idas3-scene-smoke", ["-idas3-scene-render-check", "-idas3-mobile-render-check"]),
    "mobile-night": ("-idas3-scene-smoke", ["-idas3-scene-render-check", "-idas3-mobile-render-check", "-idas3-scene-night-check"]),
    "mobile-toggle": ("-idas3-scene-smoke", ["-idas3-mobile-toggle-check"]),
    "memory": ("-idas3-scene-smoke", ["-idas3-memory-retention-check"]),
    "audio": ("-idas3-scene-smoke", ["-idas3-scene-realtime-audio"]),
    "pause": ("-idas3-pause-smoke", ["-idas3-pause-no-capture"]),
    "fov": ("-idas3-pause-smoke", ["-idas3-fov-check", "-idas3-pause-scene-only-capture"]),
    "headlights": ("-idas3-scene-smoke", ["-idas3-scene-perf-check", "-idas3-scene-perf-course", "17",
        "-idas3-scene-perf-night", "-idas3-scene-perf-warmup", "60", "-idas3-scene-perf-frames", "60",
        "-idas3-perf-offscreen", "-idas3-perf-manual-render", "-idas3-imported-headlight-check"]),
    "courses": ("-idas3-mode-flow-smoke", ["-idas3-pause-course-check"]),
    "gunsai": ("-gunsai-ta-smoke", []),
    "odawara": ("-odawara-ta-smoke", []),
    "tsubaki": ("-idas3-mode-flow-smoke", ["-idas3-tsubaki-check"]),
}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--device", default="booted")
    parser.add_argument("--cases", nargs="+", choices=CASES, default=list(CASES))
    parser.add_argument("--disable-engine-audio", action="store_true",
                        help="Use Unity's diagnostic -disableaudio startup flag for UI tests when Simulator CoreAudio is unavailable.")
    parser.add_argument("--preferred-language", choices=["en", "zh-Hans", "zh-Hant", "ja"],
                        help="Override AppleLanguages for this process only; supports upstream English, Japanese and Simplified Chinese.")
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
            pass_path = test_root / "PASS.txt"
            failure_path = test_root / "failure.txt"
            text_report = name in {"gunsai", "odawara"}
            with (output / f"{name}.log").open("w") as log:
                proc = subprocess.Popen([
                    "xcrun", "simctl", "launch", "--console", "--terminate-running-process",
                    args.device, BUNDLE, flag, str(test_root), *extra,
                    *(["-disableaudio"] if args.disable_engine_audio else []),
                    *(["-AppleLanguages", "(" + args.preferred_language + ")"] if args.preferred_language else []),
                ], stdout=log, stderr=subprocess.STDOUT)
                try:
                    deadline = time.monotonic() + (900 if name == "memory" else 180)
                    while not report_path.exists() and not (text_report and pass_path.exists()) and time.monotonic() < deadline:
                        if failure_path.exists():
                            raise RuntimeError(failure_path.read_text())
                        if proc.poll() is not None:
                            raise RuntimeError(f"{name} exited without a report; see {log.name}")
                        time.sleep(1)
                    if text_report and pass_path.exists():
                        report_path.write_text(json.dumps({"passed": True, "scope": pass_path.read_text(), "course": name}, indent=2))
                    if not report_path.exists():
                        raise RuntimeError(f"{name} timed out; see {log.name}")
                    report = json.loads(report_path.read_text())
                    if args.preferred_language:
                        expected = "zh-Hans" if args.preferred_language.startswith("zh") else args.preferred_language
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
                    if test_root.is_dir() and not test_root.is_symlink():
                        exported = output / name
                        if not exported.exists():
                            shutil.copytree(test_root, exported)
                        shutil.rmtree(test_root)
    finally:
        (output / "results.json").write_text(json.dumps(results, indent=2))
        print(f"Results: {output}", flush=True)
    return 0 if len(results) == len(args.cases) and all(r["passed"] for r in results.values()) else 1


if __name__ == "__main__":
    raise SystemExit(main())
