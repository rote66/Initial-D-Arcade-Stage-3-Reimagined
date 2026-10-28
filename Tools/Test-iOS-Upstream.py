#!/usr/bin/env python3
"""Run upstream managed regressions on Unity's Mono without building a player."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
SESSION_SOURCES = (
    "Idas3MultiplayerSession.cs", "Idas3BattleRecordSettlement.cs", "Idas3ResultJournal.cs",
    "Idas3MultiplayerTypes.cs", "Idas3OnlineActivity.cs", "Idas3OnlineUsername.cs",
    "Idas3LobbyChat.cs", "Idas3NetworkImpairment.cs", "Idas3MultiplayerRecords.cs",
    "Idas3MultiplayerRecords.Moderation.cs", "Idas3MultiplayerRecordChecks.cs",
    "Idas3BuildCompatibility.cs", "Idas3QuickMatch.cs", "Idas3TcpTransport.cs",
    "Idas3ChallengerTestTransport.cs",
)


def execute(command, log, timeout=180, env=None):
    result = subprocess.run(list(map(str, command)), cwd=ROOT, capture_output=True,
                            text=True, timeout=timeout, env=env)
    log.write_text(result.stdout + result.stderr)
    if result.returncode:
        raise RuntimeError(f"{log}\n{result.stdout}{result.stderr}")
    return result.stdout


def hashes(sources):
    return {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sources if path.is_relative_to(ROOT) and "Builds" not in path.parts}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--unity-contents", type=Path, default=Path(
        "/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents"))
    parser.add_argument("--report-dir", type=Path, default=ROOT / "Builds/iOS/Audits/Upstream/Managed")
    parser.add_argument("--compile-runtime", action="store_true",
                        help="Also type-check iOS runtime sources using bundled template assemblies.")
    args = parser.parse_args()
    output = args.report_dir.resolve()
    output.mkdir(parents=True, exist_ok=True)
    (output / "report.json").write_text(json.dumps({"passed": False, "status": "in-progress"}) + "\n")
    scripting = args.unity_contents / "Resources/Scripting"
    mono = scripting / "MonoBleedingEdge/bin/mono"
    compiler = scripting / "MonoBleedingEdge/lib/mono/4.5/csc.exe"
    facade = scripting / "UnityReferenceAssemblies/unity-4.8-api/Facades/netstandard.dll"
    json_library = ROOT / "ThirdParty/DiscordLegacyJson/Newtonsoft.Json.dll"
    native = ROOT / "Assets/Scripts/Multiplayer/Idas3MultiplayerNative.cs"
    text = native.read_text()
    marker = "    internal static class Idas3MultiplayerNative"
    if text.count(marker) != 1:
        raise RuntimeError("Native boundary changed; inspect the upstream test fixture.")
    extracted = output / "ProductionMultiplayerTypes.cs"
    extracted.write_text(text.split(marker)[0] + "}\n")
    multiplayer = ROOT / "Assets/Scripts/Multiplayer"
    fixtures = ROOT / "Tests/Multiplayer"
    session = [multiplayer / name for name in SESSION_SOURCES]
    session += [ROOT / "Assets/Scripts/Idas3LaunchArguments.cs", extracted]
    fakes = [fixtures / name for name in ("LobbyChatSessionFakes.cs", "OnlineSessionFakes.cs")]
    results = []

    def fixture(name):
        return Path(tempfile.mkdtemp(prefix=name + "-", dir=output)) / "data"

    def check(name, sources, defines="", references=(), runs=((),)):
        folder = output / name
        folder.mkdir(exist_ok=True)
        executable = folder / (name + ".exe")
        command = [mono, compiler, "/nologo", "/warnaserror+", "/target:exe",
                   "/out:" + str(executable)]
        if defines:
            command.append("/define:" + defines)
        command += ["/reference:" + str(path) for path in references]
        execute(command + sources, folder / "compile.log")
        if json_library in references:
            shutil.copyfile(json_library, folder / json_library.name)
        checks = []
        for index, flags in enumerate(runs):
            stdout = execute([mono, executable, *flags], folder / f"checks-{index}.log")
            checks += [line.split("\t", 1)[1] for line in stdout.splitlines()
                       if line.startswith("PASS\t")]
        result = dict(name=name, passed=True, count=len(checks), checks=checks,
                      sourceSha256=hashes(sources + [native]))
        (folder / "report.json").write_text(json.dumps(result, indent=2) + "\n")
        results.append(result)
        print(f"PASS {name}: {len(checks)} checks", flush=True)

    router = [ROOT / "Assets/Scripts" / name for name in (
        "Idas3MenuInputRouter.cs", "Idas3MenuInputRouter.Frontend.cs",
        "Idas3MenuInputRouter.Music.cs", "Idas3MenuInputRouter.Online.cs",
        "Idas3PointerNavigation.cs")]
    check("MenuInputRouter", router + [ROOT / "Tests/Input/MenuInputRouterDriver.cs"])
    check("SavedSteering", [ROOT / "Assets/Scripts/Idas3WheelFeedback.cs",
                           ROOT / "Tests/Input/SavedSteeringDriver.cs"])
    check("OnlineSession", session + fakes + [fixtures / "OnlineSessionDriver.cs"],
          "IDAS3_ONLINE_SESSION_TEST", runs=((), ("-idas3-multiplayer-p2p-check",)))
    check("RaceProgress", session + fakes + [fixtures / "RaceProgressDriver.cs"],
          "IDAS3_ONLINE_SESSION_TEST;IDAS3_RACE_PROGRESS_TEST")
    check("ResultSettlement", session + fakes + [fixtures / name for name in (
        "MultiplayerRecordStorageFakes.cs", "ResultSettlementDriver.cs")],
        "IDAS3_ONLINE_SESSION_TEST;IDAS3_RECORD_STORAGE_INTEGRATION;IDAS3_RESULT_SETTLEMENT_TEST",
        references=(json_library, facade), runs=((fixture("settlement"),),))
    check("ResultJournal", [multiplayer / name for name in (
        "Idas3ResultJournal.cs", "Idas3MultiplayerRecords.cs",
        "Idas3MultiplayerRecords.Moderation.cs", "Idas3MultiplayerRecordChecks.cs")] +
        [ROOT / "Assets/Scripts/Idas3LaunchArguments.cs"] +
        [fixtures / name for name in ("MultiplayerRecordStorageFakes.cs", "ResultJournalDriver.cs")],
        references=(json_library, facade), runs=((fixture("journal"), "fake"),))

    if args.compile_runtime:
        folder = output / "Runtime"
        folder.mkdir(exist_ok=True)
        sdk = folder / "Epic.OnlineServices.dll"
        sdk_sources = sorted((ROOT / "ThirdParty/EOS/com.initiald.eos-sdk/Runtime/EOS_SDK").rglob("*.cs"))
        execute([mono, compiler, "/nologo", "/target:library", "/unsafe",
                 "/define:UNITY_IOS;EOS_DYNAMIC_BINDINGS", "/out:" + str(sdk), *sdk_sources],
                folder / "eos-compile.log")
        template = args.unity_contents / (
            "Resources/PackageManager/ProjectTemplates/libcache/"
            "com.unity.template.urp-blank-17.2.1/ScriptAssemblies")
        framework = scripting / "UnityReferenceAssemblies/unity-4.8-api"
        references = sorted(framework.glob("*.dll")) + sorted((framework / "Facades").glob("*.dll"))
        references += list((scripting / "Managed/UnityEngine").glob("*.dll"))
        references += [sdk, scripting / "Managed/Newtonsoft.Json.dll",
                       ROOT / "Assets/Plugins/Discord/DiscordRPC.dll"]
        references += [template / name for name in (
            "Unity.Collections.dll", "Unity.Burst.dll", "Unity.InputSystem.dll", "UnityEngine.UI.dll")]
        missing = [str(path) for path in references if not path.is_file()]
        if missing:
            raise RuntimeError("Missing compile references: " + ", ".join(missing))
        compilers = list((scripting / "DotNetSdk").glob("sdk/*/Roslyn/bincore/csc.dll"))
        if len(compilers) != 1:
            raise RuntimeError("Expected one bundled Unity SDK compiler; inspect the editor installation.")
        environment = dict(os.environ, DOTNET_CLI_HOME=str(folder / ".dotnet"),
                           DOTNET_SKIP_FIRST_TIME_EXPERIENCE="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")
        sources = sorted((ROOT / "Assets/Scripts").rglob("*.cs"))
        execute([scripting / "DotNetSdk/dotnet", compilers[0], "/nologo", "/noconfig",
                 "/nostdlib+", "/langversion:9.0", "/target:library", "/unsafe",
                 "/define:UNITY_IOS;UNITY_6000_0_OR_NEWER;ENABLE_INPUT_SYSTEM;"
                 "EOS_DYNAMIC_BINDINGS;IDAS3_DISABLE_ROM_VALIDATION;ENABLE_UNITY_COLLECTIONS_CHECKS",
                 "/out:" + str(folder / "Assembly-CSharp.dll"),
                 *["/reference:" + str(path) for path in references], *sources],
                folder / "runtime-compile.log", env=environment)
        result = dict(name="Runtime", passed=True, sourceSha256=hashes(sources),
                      references={str(path): hashlib.sha256(path.read_bytes()).hexdigest()
                                  for path in references},
                      scope="iOS conditional C# type-check with bundled template package references, "
                            "not Unity import, exact locked packages, IL2CPP, shaders or a player build.")
        (folder / "report.json").write_text(json.dumps(result, indent=2) + "\n")
        results.append(result)
        print("PASS iOS runtime C# type-check", flush=True)

    report = dict(passed=True, suites=results, count=sum(item.get("count", 0) for item in results),
                  scope="Production menu routing and session/result logic with fake platform boundaries "
                        "and isolated test files. No player, real race, EOS service or user data.")
    (output / "report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(f"PASS {report['count']} managed checks; {output / 'report.json'}")


if __name__ == "__main__":
    main()
