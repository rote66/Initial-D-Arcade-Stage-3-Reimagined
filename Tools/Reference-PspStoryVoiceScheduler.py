#!/usr/bin/env python3
"""Run the supplied PSP scheduler in RAM and export differential-test vectors.

Requires unicorn 2.1.4. No PSP OS, disc access, patch module, or executable code
is bundled into the application or the generated vectors.
"""
import argparse
import base64
import hashlib
import json
from pathlib import Path
import random
import struct
import subprocess

from unicorn import Uc, UC_ARCH_MIPS, UC_MODE_LITTLE_ENDIAN, UC_MODE_MIPS32, UC_HOOK_CODE
from unicorn.mips_const import (
    UC_MIPS_REG_A0, UC_MIPS_REG_A1, UC_MIPS_REG_PC, UC_MIPS_REG_RA,
    UC_MIPS_REG_SP, UC_MIPS_REG_V0, UC_CPU_MIPS32_24KF,
)

ROOT = Path(__file__).resolve().parents[1]
EBOOT_SHA = "4c0c5865b5157a8b9764448ae68ff1faf9a28e0c2f6ed1f1e5215ce33224d4e5"
OBJ, INPUT, SOURCE, TLS, RETURN = 0x600000, 0x601000, 0x602000, 0x610000, 0x800000


class Original:
    def __init__(self, binary, rules, seed):
        self.cpu = Uc(UC_ARCH_MIPS, UC_MODE_MIPS32 | UC_MODE_LITTLE_ENDIAN)
        self.cpu.ctl_set_cpu_model(UC_CPU_MIPS32_24KF)
        self.cpu.mem_map(0, 0x900000)
        self.cpu.mem_write(0, binary[0x54:0x54 + 0x2537f0])
        self.heap = 0x620000
        self.cpu.hook_add(UC_HOOK_CODE, self.hook)
        self.cpu.mem_write(TLS + 0xa8, struct.pack("<Q", seed))
        self.cpu.mem_write(SOURCE, struct.pack("<H", len(rules)) + b"".join(rules))
        self.call(0x1aec50, OBJ, SOURCE)
        self.rules = self.u32(OBJ + 4)
        self.count = len(rules)

    def u32(self, address):
        return struct.unpack("<I", self.cpu.mem_read(address, 4))[0]

    def string(self, address):
        return bytes(self.cpu.mem_read(address, 32)).split(b"\0")[0]

    def hook(self, cpu, address, _size, _user):
        if address == 0x16dd8:
            result = self.heap
            self.heap += (cpu.reg_read(UC_MIPS_REG_A0) + 15) & ~15
        elif address == 0x39fb0:
            result = TLS
        elif address == 0x3df98:
            a, b = self.string(cpu.reg_read(UC_MIPS_REG_A0)), self.string(cpu.reg_read(UC_MIPS_REG_A1))
            result = (a > b) - (a < b)
        elif (0x1aec50 <= address < 0x1afb80 or
              0x3c6c8 <= address < 0x3c758 or 0x535b8 <= address < 0x535d4):
            return
        else:
            raise RuntimeError(f"Unexpected code outside isolated scheduler: {address:#x}")
        cpu.reg_write(UC_MIPS_REG_V0, result & 0xffffffff)
        cpu.reg_write(UC_MIPS_REG_PC, cpu.reg_read(UC_MIPS_REG_RA))

    def call(self, address, a=OBJ, b=INPUT):
        self.cpu.reg_write(UC_MIPS_REG_SP, 0x7ff000)
        self.cpu.reg_write(UC_MIPS_REG_RA, RETURN)
        self.cpu.reg_write(UC_MIPS_REG_A0, a)
        self.cpu.reg_write(UC_MIPS_REG_A1, b)
        try:
            self.cpu.emu_start(address, RETURN, count=200000)
        except Exception as error:
            raise RuntimeError(f"Original CPU stopped at {self.cpu.reg_read(UC_MIPS_REG_PC):#x}") from error
        if self.cpu.reg_read(UC_MIPS_REG_PC) != RETURN:
            raise RuntimeError("Scheduler instruction bound exceeded")
        return self.cpu.reg_read(UC_MIPS_REG_V0)

    def step(self, data):
        self.cpu.mem_write(INPUT, data)
        self.call(0x1aefa0)
        requested = self.call(0x1afa84)
        chosen = (requested - self.rules - 28) // 60 if requested else -1
        raw = bytearray(self.cpu.mem_read(OBJ + 0x18, 0x64))
        active = self.u32(OBJ + 0x18)
        last = self.u32(OBJ + 0x20)
        struct.pack_into("<i", raw, 0, (active - self.rules) // 60 if active else -1)
        struct.pack_into("<i", raw, 8, (last - self.rules - 28) // 60 if last else -1)
        raw[12:24] = bytes(12)  # consumed request and two allocation addresses
        raw += self.cpu.mem_read(self.u32(OBJ + 0x28), self.count)
        raw += self.cpu.mem_read(self.u32(OBJ + 0x2c), self.count)
        raw += self.cpu.mem_read(TLS + 0xa8, 8)
        return chosen, base64.b64encode(raw).decode()


def scenarios():
    # Exact boundary values, long steady gaps, crossings, late sectors and
    # frequent order changes. Each scenario starts with a fresh original bank.
    for kind in range(3):
        rng = random.Random(900 + kind)
        for frame in range(2200):
            if kind == 0:
                gap = (0, 4.3, 4.300001, 79.99999, 80, 80.99999, 81, 89.99999, 90, 130)[(frame // 110) % 10]
                lead = int((frame // 550) % 2 == 0)
            elif kind == 1:
                gap = (10, 100, 0, 5)[(frame // 200) % 4]
                lead = int((frame // 160) % 2 == 0)
            else:
                gap = rng.choice([0, 4.299999, 4.3, 4.300001, 20, 49.99, 50, 79.9, 81, 90, 150])
                lead = rng.randrange(2)
            sector = min(3, frame // 550)
            progress = frame / 2200
            speed = 0 if frame % 400 < 100 else 140
            # Alternating audio completion drives both interrupts and normal ends.
            playing = int(frame % (31 + kind * 23) != 0)
            yield kind, frame, struct.pack("<iffiiifiiB3x", sector, progress, speed, 0, 0,
                                          frame, gap, lead, 0, playing)


def verify_banks(directory, catalog):
    hashes = {}
    for bank in catalog["banks"]:
        path = directory / f"ac{bank['id']:02d}.bin"
        data = path.read_bytes()
        digest = hashlib.sha256(data).hexdigest()
        if digest != bank["sourceSha256"]:
            raise ValueError(f"Original bank hash mismatch: {path.name}")
        if data[:4] != b"XBB\x01":
            raise ValueError(f"Invalid original bank: {path.name}")
        entries = {}
        for index in range(struct.unpack_from("<I", data, 4)[0]):
            offset, size, name, _ = struct.unpack_from("<4I", data, 32 + index * 16)
            key = data[name:data.index(0, name)].decode("ascii")
            if offset + size > len(data) or key in entries:
                raise ValueError(f"Invalid original bank entry: {path.name}")
            entries[key] = data[offset:offset + size]
        words = entries[f"RACEWORDS_AC{bank['id']:02d}.BIN"]
        expected = struct.pack("<H", len(bank["rules"])) + b"".join(
            bytes(rule["sourceFlags"]) + (rule["clip"] + ".at3").encode().ljust(32, b"\0")
            for rule in bank["rules"])
        if words != expected:
            raise ValueError(f"Imported rule table differs from original: {path.name}")
        for clip in bank["clips"]:
            audio = entries[clip["id"] + ".at3"]
            if hashlib.sha256(audio).hexdigest() != clip["sourceSha256"]:
                raise ValueError(f"Imported voice identity differs from original: {clip['id']}")
        hashes[path.name] = digest
    return hashes


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("eboot", type=Path)
    parser.add_argument("--output", type=Path, default=ROOT / "Builds/iOS/Audits/PspVoiceScheduler")
    parser.add_argument("--unity-contents", type=Path, default=Path(
        "/Applications/Unity/Hub/Editor/6000.6.4f1/Unity.app/Contents"))
    parser.add_argument("--voice-banks", type=Path,
                        help="Original extracted sound/voice directory; verifies imported rules and voice IDs")
    args = parser.parse_args()
    binary = args.eboot.read_bytes()
    assert hashlib.sha256(binary).hexdigest() == EBOOT_SHA, "Unrecognized original executable"
    catalog = json.loads((ROOT / "Native/data/story_voices/voices.json").read_text())
    bank_hashes = verify_banks(args.voice_banks, catalog) if args.voice_banks else None
    args.output.mkdir(parents=True, exist_ok=True)
    vectors = args.output / "reference.tsv"
    frames = list(scenarios())
    checks = requests = 0
    with vectors.open("w") as out:
        for bank in catalog["banks"]:
            rules = [bytes(r["sourceFlags"]) + (r["clip"] + ".at3").encode().ljust(32, b"\0")
                     for r in bank["rules"]]
            for kind in range(3):
                seed = bank["id"] * 17 + kind + 1
                original = Original(binary, rules, seed)
                out.write(f"B\t{bank['id']}\t{seed}\t{len(rules)}\n")
                for rule in rules:
                    out.write("R\t" + rule.hex() + "\n")
                for scenario, frame, data in frames:
                    if scenario != kind:
                        continue
                    chosen, snapshot = original.step(data)
                    out.write(f"F\t{data.hex()}\t{chosen}\t{snapshot}\n")
                    checks += 1
                    requests += chosen >= 0
                print(f"bank {bank['id']:02d} scenario {kind}: original CPU complete", flush=True)
    report = dict(passed=True, originalSha256=EBOOT_SHA, frames=checks, requests=requests,
                  cases=90, vectorsSha256=hashlib.sha256(vectors.read_bytes()).hexdigest(),
                  originalBankHashes=bank_hashes,
                  scope="Bounded original constructor/reset/tick/RNG in RAM; libc allocation, TLS "
                        "and strcmp boundaries only. No PSP OS, audio hardware or patch executed.")
    (args.output / "original-report.json").write_text(json.dumps(report, indent=2) + "\n")
    scripting = args.unity_contents / "Resources/Scripting/MonoBleedingEdge"
    mono = scripting / "bin/mono"
    sources = [ROOT / "Assets/Scripts/Idas3StoryVoiceLogic.cs", ROOT / "Tests/Input/StoryVoiceDriver.cs"]
    executable = args.output / "StoryVoiceDriver.exe"
    result = subprocess.run(list(map(str, [mono, scripting / "lib/mono/4.5/csc.exe",
        "/nologo", "/warnaserror+", "/define:IDAS3_STORY_VOICE_TEST",
        "/out:" + str(executable), *sources])), capture_output=True, text=True)
    (args.output / "compile.log").write_text(result.stdout + result.stderr)
    result.check_returncode()
    result = subprocess.run([str(mono), str(executable), str(vectors)], capture_output=True, text=True)
    (args.output / "comparison.log").write_text(result.stdout + result.stderr)
    result.check_returncode()
    report.update(managedCompared=True, sources={
        str(p.relative_to(ROOT)): hashlib.sha256(p.read_bytes()).hexdigest() for p in sources})
    (args.output / "comparison-report.json").write_text(json.dumps(report, indent=2) + "\n")
    print(json.dumps(report, indent=2))


if __name__ == "__main__":
    main()
