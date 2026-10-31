#!/usr/bin/env python3
"""Soak-test the curated source hooks (`source_hooks`) in game: stability and by-name attribution.

Relaunches the game repeatedly; each launch stages a couple of fights through STS2 MCP's `console` action
with a rotating batch of the hooked powers, relics and potions, plays them with the screenshot script's
simple policy, and reads the game's stdout for:

  - stability: "Bad IL range", the mod disabling itself, any [STS2 Damage] error, the game crashing, or
    the API going unresponsive (sampled with `sample` as a suspected freeze);
  - attribution: which hooked types fired (debug STAMP lines) and what each hit was credited to (HIT lines).

Needs the mod's config to have "forecast_debug": true (for the STAMP/HIT lines; source_hooks is on by default), STS2 MCP with its
`console` action, and a spare profile (it switches to slot 3 and wipes it when a run ends there).
Writes a JSON report and prints a summary. Usage: scripts/soak_source_hooks.py [--launches N] [--out DIR]
"""
import argparse
import collections
import json
import os
import re
import signal
import subprocess
import sys
import time
import urllib.request

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import screenshots as game  # noqa: E402  (shared game-driving helpers)

GAME_DIR = os.path.expanduser("~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2")
GAME_BIN_DIR = os.path.join(GAME_DIR, "SlayTheSpire2.app/Contents/MacOS")
PROFILES = "http://localhost:15526/api/v1/profiles"
SPARE_PROFILE = 3
SAVES = os.path.expanduser("~/Library/Application Support/SlayTheSpire2/steam")

# Hooked type -> how to make it happen. ("power", ID, target) targets 0 = player, 1 = first enemy;
# ("relic", ID); ("potion", ID). Orb types (and orb-driven Hailstorm, Thunder, Metronome) need the Defect's
# orb slots, which an Ironclad doesn't have, so they're listed as not exercised rather than guessed at.
GRANTS = {
    "PoisonPower": ("power", "POISON_POWER", 1), "DoomPower": ("power", "DOOM_POWER", 1),
    "ConstrictPower": ("power", "CONSTRICT_POWER", 1), "StranglePower": ("power", "STRANGLE_POWER", 1),
    "DemisePower": ("power", "DEMISE_POWER", 1), "DisintegrationPower": ("power", "DISINTEGRATION_POWER", 1),
    "ThornsPower": ("power", "THORNS_POWER", 0), "FlameBarrierPower": ("power", "FLAME_BARRIER_POWER", 0),
    "ReflectPower": ("power", "REFLECT_POWER", 0), "InfernoPower": ("power", "INFERNO_POWER", 0),
    "TheBombPower": ("power", "THE_BOMB_POWER", 0), "MagicBombPower": ("power", "MAGIC_BOMB_POWER", 0),
    "RollingBoulderPower": ("power", "ROLLING_BOULDER_POWER", 0), "PanachePower": ("power", "PANACHE_POWER", 0),
    "GalvanicPower": ("power", "GALVANIC_POWER", 0), "HauntPower": ("power", "HAUNT_POWER", 0),
    "SerpentFormPower": ("power", "SERPENT_FORM_POWER", 0), "BlackHolePower": ("power", "BLACK_HOLE_POWER", 0),
    "PaperCutsPower": ("power", "PAPER_CUTS_POWER", 0), "JuggernautPower": ("power", "JUGGERNAUT_POWER", 0),
    "CrimsonMantlePower": ("power", "CRIMSON_MANTLE_POWER", 0), "TheGambitPower": ("power", "THE_GAMBIT_POWER", 0),
    "SpeedsterPower": ("power", "SPEEDSTER_POWER", 0), "SmokestackPower": ("power", "SMOKESTACK_POWER", 0),
    "OutbreakPower": ("power", "OUTBREAK_POWER", 0), "NecroMasteryPower": ("power", "NECRO_MASTERY_POWER", 0),
    "SleightOfFleshPower": ("power", "SLEIGHT_OF_FLESH_POWER", 0), "GravityPower": ("power", "GRAVITY_POWER", 0),
    "CharonsAshes": ("relic", "CHARONS_ASHES"), "FestivePopper": ("relic", "FESTIVE_POPPER"),
    "ForgottenSoul": ("relic", "FORGOTTEN_SOUL"), "Kusarigama": ("relic", "KUSARIGAMA"),
    "LetterOpener": ("relic", "LETTER_OPENER"), "LostWisp": ("relic", "LOST_WISP"),
    "MercuryHourglass": ("relic", "MERCURY_HOURGLASS"), "MrStruggles": ("relic", "MR_STRUGGLES"),
    "ParryingShield": ("relic", "PARRYING_SHIELD"), "RoyalPoison": ("relic", "ROYAL_POISON"),
    "ScreamingFlagon": ("relic", "SCREAMING_FLAGON"), "StoneCalendar": ("relic", "STONE_CALENDAR"),
    "Tingsha": ("relic", "TINGSHA"),
    "FirePotion": ("potion", "FIRE_POTION"), "ExplosiveAmpoule": ("potion", "EXPLOSIVE_AMPOULE"),
    "FoulPotion": ("potion", "FOUL_POTION"), "PotionShapedRock": ("potion", "POTION_SHAPED_ROCK"),
}
NOT_EXERCISED = ["DarkOrb", "GlassOrb", "LightningOrb", "HailstormPower", "ThunderPower", "Metronome",
                 # Sandpit belongs to one boss fight; granted to a Nibbit the game's own code throws a
                 # NullReferenceException (SandpitPower.get_AllAffectedCreatures) and the enemy turn stalls.
                 "SandpitPower"]

STAMP = re.compile(r"\[STS2 Damage\] STAMP (\w+) label=(.*?) frame=(\d+)")
HIT = re.compile(r"\[STS2 Damage\] HIT (dealt|taken) src=(.*?) amt=(\d+) round=\d+ frame=(\d+)")
ERRORS = re.compile(r"Bad IL|disabled after repeated errors|\[STS2 Damage\].*(error|failed|exception)", re.I)
GAME_ERROR = re.compile(r"^\[ERROR\] (\S+Exception\S*)")


def api_alive(timeout=5):
    try:
        with urllib.request.urlopen("http://localhost:15526/", timeout=timeout):
            return True
    except Exception:
        return False


def game_pids():
    out = subprocess.run(["pgrep", "-f", "Slay the Spire 2$"], capture_output=True, text=True).stdout
    return [int(p) for p in out.split()]


def kill_game():
    for pid in game_pids():
        os.kill(pid, signal.SIGKILL)
    time.sleep(3)


def set_fast_mode(mode):
    """The game's own animation speed setting ("normal" / "fast"), per profile, written while the game is down."""
    for root, _, files in os.walk(SAVES):
        if root.endswith(f"modded/profile{SPARE_PROFILE}/saves") and "prefs.save" in files:
            path = os.path.join(root, "prefs.save")
            with open(path) as f:
                prefs = json.load(f)
            prefs["fast_mode"] = mode
            with open(path, "w") as f:
                json.dump(prefs, f, indent=2)


def launch(log_path, fast_mode="normal"):
    kill_game()
    set_fast_mode(fast_mode)
    env = dict(os.environ, SteamAppId="2868840", SteamGameId="2868840")
    subprocess.Popen(["./Slay the Spire 2"], cwd=GAME_BIN_DIR, env=env, stdout=open(log_path, "w"),
                     stderr=subprocess.STDOUT, start_new_session=True)
    for _ in range(90):
        if api_alive(1):
            break
        time.sleep(1)
    else:
        return False
    game.wait_for(lambda s: s.get("state_type") == "menu" and s.get("options"), timeout=30)
    return True


def profile_post(body):
    req = urllib.request.Request(PROFILES, data=json.dumps(body).encode(), headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=15) as r:
        return json.load(r)


def use_spare_profile(reset=False):
    """Switch to the spare slot; with reset, wipe it first (a finished run there leaves a Timeline unlock
    that blocks Singleplayer)."""
    def switch(pid):
        profile_post({"action": "switch", "profile_id": pid}); time.sleep(3)
        profile_post({"action": "switch", "profile_id": pid}); time.sleep(5)
    with urllib.request.urlopen(PROFILES, timeout=10) as r:
        current = json.load(r)["current_profile_id"]
    if reset:
        if current == SPARE_PROFILE:
            switch(2)
        profile_post({"action": "delete", "profile_id": SPARE_PROFILE}); time.sleep(3)
        switch(SPARE_PROFILE)
    elif current != SPARE_PROFILE:
        switch(SPARE_PROFILE)


def reach_run():
    use_spare_profile()
    s = game.get_state()
    opts = [o if isinstance(o, str) else o.get("name") for o in s.get("options") or []]
    if s.get("state_type") == "menu" and "singleplayer" not in opts and "continue" not in opts:
        use_spare_profile(reset=True)
    if game.to_combat() is None:
        # The console's `fight` only needs a run in progress, not a fight; the screenshot helper's menu walk
        # can stall on a screen it doesn't handle (seen once on an event), so carry on from any in-run screen.
        state = game.get_state().get("state_type")
        if state in (None, "menu", "game_over"):
            return False
        print(f"  (menu walk stalled on '{state}'; starting the fight from there)", flush=True)
    clear_combat()
    return True


def clear_combat():
    """End whatever fight is running and leave its rewards. Starting a console fight on top of a live one
    softlocks the game (the new enemies show, but the old fight's turn never starts)."""
    if game.get_state().get("state_type") in game.IN_COMBAT:
        game.console("kill all")
        game.wait_for(lambda st: st.get("state_type") not in game.IN_COMBAT, timeout=30)
    if game.get_state().get("state_type") == "rewards":
        game.act("proceed")
        time.sleep(1.5)


def grant(item):
    kind, ident = item[0], item[1]
    if kind == "power":
        amount = 6 if ident in ("POISON_POWER", "DOOM_POWER", "CONSTRICT_POWER") else 3
        return game.console(f"power {ident} {amount} {item[2]}")
    return game.console(f"{kind} {ident}")


def soak_fight(batch, rounds=5):
    """One staged fight: grant the batch, then play `rounds` rounds (healing each round so the run survives)."""
    clear_combat()
    s = game.start_fight("NIBBITS_NORMAL")
    if s.get("state_type") not in game.IN_COMBAT:
        return "no fight"
    granted, failed, stalled = [], [], False
    for name in batch:
        res = grant(GRANTS[name])
        (granted if str(res.get("success")).lower() == "true" else failed).append(name)
    s = game.get_state()
    potions = [i for i, p in enumerate(s["player"].get("potions") or []) if p]
    for slot in reversed(potions):  # use the belt now; later slots first so indices stay valid
        enemies = [e for e in s.get("battle", {}).get("enemies", []) if (e.get("hp") or 0) > 0]
        game.act("use_potion", slot=slot, **({"target": enemies[0]["entity_id"]} if enemies else {}))
        time.sleep(1.0)
    for _ in range(rounds):
        s = game.get_state()
        if s.get("state_type") not in game.IN_COMBAT:
            break
        game.console("heal 999")
        game.play_turn(s)
        if game.get_state().get("state_type") in game.IN_COMBAT:
            s = game.next_round(game.get_state())
            if s.get("state_type") in game.IN_COMBAT and not s.get("battle", {}).get("is_play_phase"):
                stalled = True  # the turn never came back (next_round waited 45 s); end the fight and move on
                break
    if game.get_state().get("state_type") in game.IN_COMBAT:
        game.console("kill all")
    game.wait_for(lambda st: st.get("state_type") not in game.IN_COMBAT, timeout=30)
    if game.get_state().get("state_type") == "rewards":
        game.act("proceed")
    return {"granted": granted, "grant_failed": failed, "stalled": stalled}


CRASH_REPORTS = os.path.expanduser("~/Library/Logs/DiagnosticReports")


def new_crash_report(since):
    """A macOS crash report for the game written after `since`, or None."""
    try:
        for name in os.listdir(CRASH_REPORTS):
            path = os.path.join(CRASH_REPORTS, name)
            if "Slay" in name and os.path.getmtime(path) >= since:
                return path
    except OSError:
        pass
    return None


def why_gone(log_path, since):
    """The game process is gone: a crash leaves a crash report; a clean exit logs NGame.Quit."""
    if report := new_crash_report(since):
        return f"crashed ({report})"
    with open(log_path, errors="replace") as f:
        if "NGame.Quit called" in f.read():
            return "game quit cleanly (not a crash)"
    return "exited without a crash report or a clean quit"


def suspect_freeze(log_dir, n, log_path, since):
    pids = game_pids()
    if not pids:
        return why_gone(log_path, since)
    path = os.path.join(log_dir, f"launch{n}.sample.txt")
    subprocess.run(["sample", str(pids[0]), "3", "-file", path], capture_output=True)
    return f"unresponsive (sampled to {path})"


def analyse(log_path):
    stamps, hits, errors = collections.Counter(), [], []
    hooks_applied = None
    with open(log_path, errors="replace") as f:
        lines = f.readlines()
    stamp_at = []  # (frame, type, label)
    # Game exceptions with the first game frame of each, and whether our mod or a Harmony patch is on the stack.
    game_errors = []
    for i, line in enumerate(lines):
        if m := GAME_ERROR.search(line):
            stack = [l.strip() for l in lines[i + 1:i + 40] if l.strip().startswith("at ")]
            ours = any("DamageCharts" in l or "_Patch" in l or "Harmony" in l for l in stack)
            game_errors.append({"exception": m.group(1), "at": stack[0] if stack else "", "mod_on_stack": ours})
    for line in lines:
        if m := STAMP.search(line):
            stamps[m.group(1)] += 1
            stamp_at.append((int(m.group(3)), m.group(1), m.group(2)))
        elif m := HIT.search(line):
            hits.append((m.group(1), m.group(2), int(m.group(3)), int(m.group(4))))
        if ERRORS.search(line) and "HIT " not in line:
            errors.append(line.strip())
        if "power-source hooks applied to" in line:
            hooks_applied = line.strip()
    labels = {}
    for _, t, label in stamp_at:
        labels.setdefault(t, label)
    named = collections.Counter()
    for _, src, _, _ in hits:
        for t, label in labels.items():
            if src == label:
                named[t] += 1
    status_hits = sum(1 for kind, src, _, _ in hits if kind == "dealt" and src in ("Status", "Other"))
    return {"hooks_applied": hooks_applied, "stamps": dict(stamps), "named_hits": dict(named),
            "labels": labels, "dealt_hits": sum(1 for h in hits if h[0] == "dealt"),
            "status_dealt_hits": status_hits, "errors": errors, "game_errors": game_errors}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--launches", type=int, default=10)
    ap.add_argument("--fights", type=int, default=4, help="fights per launch")
    ap.add_argument("--batch", type=int, default=6, help="hooked items granted per fight")
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "out", "soak"))
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    items = list(GRANTS)
    cursor = 0
    report = {"launches": [], "not_exercised": NOT_EXERCISED}
    failures_in_a_row, reset_next = 0, False
    for n in range(1, args.launches + 1):
        log_path = os.path.join(args.out, f"launch{n}.log")
        fast_mode = "fast" if n % 2 == 0 else "normal"  # alternate the game's speed setting: both are common
        entry = {"launch": n, "fast_mode": fast_mode, "fights": [], "outcome": "ok"}
        print(f"launch {n}/{args.launches}", flush=True)
        started = time.time()
        try:
            if not launch(log_path, fast_mode):
                entry["outcome"] = suspect_freeze(args.out, n, log_path, started) if game_pids() else why_gone(log_path, started)
            else:
                if reset_next:  # the last launch went wrong: start this one from a fresh spare profile
                    use_spare_profile(reset=True)
                if not reach_run():
                    entry["outcome"] = "couldn't reach a run"
                else:
                    for _ in range(args.fights):
                        if not game_pids():
                            break
                        batch = [items[(cursor + i) % len(items)] for i in range(args.batch)]
                        cursor = (cursor + args.batch) % len(items)
                        entry["fights"].append({"batch": batch, **soak_fight(batch)})
                    if not game_pids():
                        entry["outcome"] = why_gone(log_path, started)
        except Exception as ex:
            entry["outcome"] = suspect_freeze(args.out, n, log_path, started) if not api_alive(30) else f"script error: {ex}"
        entry.update(analyse(log_path))
        if entry["errors"] or any(g["mod_on_stack"] for g in entry["game_errors"]):
            entry["outcome"] = "errors"
        failed = entry["outcome"] != "ok"
        failures_in_a_row = failures_in_a_row + 1 if failed else 0
        reset_next = failed
        stalls = sum(1 for f in entry["fights"] if f.get("stalled"))
        print(f"  {entry['outcome']} ({fast_mode}): {len(entry['fights'])} fights, {stalls} stalled, "
              f"{sum(entry['stamps'].values())} stamps, {entry['dealt_hits']} dealt hits, {len(entry['errors'])} mod errors, "
              f"{len(entry['game_errors'])} game exceptions ({sum(g['mod_on_stack'] for g in entry['game_errors'])} with the mod on the stack)",
              flush=True)
        report["launches"].append(entry)
        with open(os.path.join(args.out, "report.json"), "w") as f:
            json.dump(report, f, indent=2)
        if failures_in_a_row >= 2:
            print("two launches in a row went wrong; stopping (see the launch logs)", flush=True)
            break
    kill_game()

    fired, named = collections.Counter(), collections.Counter()
    for e in report["launches"]:
        fired.update(e["stamps"]); named.update(e["named_hits"])
    granted = {n for e in report["launches"] for f in e["fights"] for n in f.get("granted", [])}
    tried = {n for e in report["launches"] for f in e["fights"] for n in f.get("batch", [])}
    bad = [e for e in report["launches"] if e["outcome"] != "ok"]
    print(f"\n{len(report['launches'])} launches, {len(bad)} with problems")
    for e in bad:
        print(f"  launch {e['launch']}: {e['outcome']} {e['errors'][:2]}")
    print(f"\nhooked types granted: {len(granted)}/{len(GRANTS)}; fired: {len(fired)}; named on a hit: {len(named)}")
    for t in sorted(GRANTS):
        status = "named" if named[t] else ("fired, never named" if fired[t] else
                                           ("granted, never fired" if t in granted else
                                            ("grant failed" if t in tried else "not reached")))
        print(f"  {t:22} {status:22} stamps={fired[t]} named_hits={named[t]}")
    print(f"not exercised (orbs, or unusable outside its own fight): {', '.join(NOT_EXERCISED)}")
    print("\ngame exceptions (first game frame; * = our mod or a Harmony patch on the stack):")
    seen = collections.Counter((g["exception"], g["at"], g["mod_on_stack"]) for e in report["launches"] for g in e["game_errors"])
    for (exc, at, ours), c in seen.most_common():
        print(f"  {'*' if ours else ' '} {c}x {exc} {at}")
    credited = collections.Counter()
    for e in report["launches"]:
        with open(os.path.join(args.out, f"launch{e['launch']}.log"), errors="replace") as f:
            for line in f:
                if m := HIT.search(line):
                    credited[(m.group(1), m.group(2))] += 1
    print("\nevery source a hit was credited to (check for wrong names):")
    for (kind, src), c in sorted(credited.items()):
        print(f"  {kind:5} {src:24} {c}")


if __name__ == "__main__":
    main()
