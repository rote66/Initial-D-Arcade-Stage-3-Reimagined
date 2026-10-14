#!/usr/bin/env python3
"""Test bounded diagnostic I/O and stable iOS package names without a player."""
import argparse
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

from ios_packaging import ROOT, VERSION, data_path, ipa_path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    unity = Path("/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents/Resources/Scripting/MonoBleedingEdge")
    parser.add_argument("--mono", type=Path, default=unity / "bin/mono")
    parser.add_argument("--compiler", type=Path, default=unity / "lib/mono/4.5/csc.exe")
    parser.add_argument("--report-dir", type=Path, default=ROOT / "Builds/iOS/Audits/Tools")
    args = parser.parse_args()
    output = args.report_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    sources = [ROOT / "Assets/Scripts/Idas3DiagnosticWriter.cs", ROOT / "Tests/iOS/DiagnosticWriterDriver.cs"]
    executable = output / "DiagnosticWriterDriver.exe"
    compiled = subprocess.run([str(args.mono), str(args.compiler), "/nologo", "/warnaserror+",
                               "/out:" + str(executable), *map(str, sources)], capture_output=True, text=True)
    (output / "compile.log").write_text(compiled.stdout + compiled.stderr)
    if compiled.returncode:
        raise RuntimeError(compiled.stdout + compiled.stderr)
    with tempfile.TemporaryDirectory(prefix="diagnostic-writer-", dir=output) as directory:
        private = Path(directory)
        target = private / "untouched"
        target.mkdir()
        (target / "keep.txt").write_text("untouched")
        linked = private / "linked"
        linked.symlink_to(target, target_is_directory=True)
        run = subprocess.run([str(args.mono), str(executable), str(private / "test"), str(linked)],
                             capture_output=True, text=True)
        assert (target / "keep.txt").read_text() == "untouched"
    (output / "checks.log").write_text(run.stdout + run.stderr)
    if run.returncode:
        raise RuntimeError(run.stdout + run.stderr)
    checks = [line.split("\t", 1)[1] for line in run.stdout.splitlines() if line.startswith("PASS\t")]
    assert VERSION == 3
    assert data_path("small").name == "InitialD-GameData-mp3.zip"
    assert data_path("full").name == "InitialD-GameData-full.zip"
    assert ipa_path("core-small") == ipa_path("core-full")
    checks += ["Data manifest version stays 3", "Stable MP3 filename", "Stable full filename", "Universal core filename"]
    for mode in ("core-small", "core-full", "full"):
        dry = subprocess.run(["bash", str(ROOT / "build-ios.sh"), mode, "--dry-run"],
                             cwd=ROOT, capture_output=True, text=True, check=True)
        assert "InitialD-GameData-v" not in dry.stdout
        assert "ROM validation: 0" in dry.stdout
        (output / (mode + "-dry-run.log")).write_text(dry.stdout)
        checks.append(mode + " dry run has stable filenames and ROM gate off")
    report = {"passed": True, "count": len(checks), "checks": checks,
              "sourceSha256": {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in sources},
              "scope": "Background diagnostic writer rotation, shutdown/deletion, truncation, unrelated-file and symlink safety; build dry-run filenames. No device-performance claim."}
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f'PASS {len(checks)} iOS tool checks; {output / "report.json"}')


if __name__ == "__main__":
    main()
