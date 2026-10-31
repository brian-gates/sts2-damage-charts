#!/usr/bin/env python3
"""Capture README / Workshop screenshots by staging fights through the STS2 MCP mod and the dev console.

Needs the game running with STS2 MCP (https://github.com/Gennadiyev/STS2MCP, localhost:15526) built with its
`console` action, and Damage Charts enabled, on a profile whose run you don't mind wrecking (a spare profile
slot is safest: the scenes jump between fights, add potions and damage you). From the main menu it starts an
Ironclad run; mid-run it continues. Captures the game window with macOS `screencapture` (the terminal needs
Screen Recording permission), cropped to the game's 16:9 area:

  fight:   forecast.png  turn 1 vs two Nibbits: the Incoming Forecast panel and its HP preview
           rounds.png    round 4: the Damage Out | In / Round strip, potion damage included
           summary.png   the end-of-combat summary (with potions) on the rewards screen
  lethal:  lethal.png    low HP against an elite's opening hit: the forecast's lethal warning

Setup happens off camera so the shots look like ordinary play: potions are added and HP is lowered before
the captured fight starts, fights are won by playing them out, and invincibility (`godmode`, which only stops
the run from ending) is switched on after the in-combat shots are taken.

Usage: scripts/screenshots.py [--out DIR] [fight] [lethal]
"""
import argparse
import json
import os
import subprocess
import sys
import time
import urllib.request

API = "http://localhost:15526/api/v1/singleplayer"
HERE = os.path.dirname(os.path.abspath(__file__))
GAME_PROCESS = "Slay the Spire 2"


def get_state():
    with urllib.request.urlopen(API, timeout=10) as r:
        return json.load(r)


def act(action, **params):
    if os.environ.get("STS2_TRACE"):
        print(f"  > {action} {params}", file=sys.stderr, flush=True)
    body = json.dumps({"action": action, **params}).encode()
    req = urllib.request.Request(API, data=body, headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=30) as r:
        res = json.load(r)
    if res.get("status") != "ok":
        print(f"  {action} {params}: {res.get('message') or res.get('error')}", file=sys.stderr)
    return res


def wait_for(pred, timeout=30.0, poll=0.5):
    """Poll the game state until pred(state) is true; returns that state (or the last one on timeout)."""
    end = time.time() + timeout
    state = {}
    while time.time() < end:
        try:
            state = get_state()
            if pred(state):
                return state
        except Exception:
            pass
        time.sleep(poll)
    return state


def window_id():
    # CoreGraphics window list via a tiny Swift helper: the game's main (titled) window.
    out = subprocess.run(["swift", os.path.join(HERE, "game_window_id.swift")], capture_output=True, text=True)
    wid = out.stdout.strip()
    if not wid:
        sys.exit("game window not found (is the game running?)")
    return wid


def capture(path):
    # macOS only redraws a visible window, so bring the game forward, let a frame land, then hand focus back.
    front = subprocess.run(["osascript", "-e", 'tell application "System Events" to get name of first '
                            'application process whose frontmost is true'], capture_output=True, text=True).stdout.strip()
    subprocess.run(["osascript", "-e", f'tell application "System Events" to set frontmost of process "{GAME_PROCESS}" to true'])
    time.sleep(1.2)
    res = subprocess.run(["screencapture", "-x", "-o", "-l", window_id(), path], capture_output=True, text=True)
    if front and front != GAME_PROCESS:
        subprocess.run(["osascript", "-e", f'tell application "System Events" to set frontmost of process "{front}" to true'])
    if res.returncode != 0:
        print(f"  capture failed: {res.stderr.strip()}")
        return
    crop_letterbox(path)
    print(f"  captured {path}")


def crop_letterbox(path):
    # The game renders 16:9 centred in the window; trim the black bars above and below with sips.
    out = subprocess.run(["sips", "-g", "pixelWidth", "-g", "pixelHeight", path], capture_output=True, text=True).stdout
    dims = dict(line.split(": ") for line in out.split("\n")[1:] if ": " in line)
    w, h = int(dims["  pixelWidth"]), int(dims["  pixelHeight"])
    game_h = round(w * 9 / 16)
    if game_h < h:
        subprocess.run(["sips", "-c", str(game_h), str(w), path], capture_output=True)


IN_COMBAT = ("monster", "elite", "boss")


def to_combat():
    """Drive menus and the map until a fight starts. Returns the combat state, or None if stuck."""
    for _ in range(40):
        s = get_state()
        t = s.get("state_type")
        if t in IN_COMBAT:
            return s
        if t == "game_over":
            act("menu_select", option="main_menu")
        elif t == "menu" and s.get("menu_screen") == "character_select":
            act("menu_select", option="IRONCLAD")
            time.sleep(1.0)
            act("menu_select", option="embark")
        elif t == "menu":
            opts = [o if isinstance(o, str) else o.get("name") for o in s.get("options") or []]
            for pick in ("continue", "singleplayer", "no", "main_menu"):
                if pick in opts:
                    act("menu_select", option=pick)
                    break
        elif t == "map":
            nxt = s["map"]["next_options"]
            fights = [o for o in nxt if o.get("type") == "Monster"] or nxt
            act("choose_map_node", index=fights[0]["index"])
        elif t == "event":
            ev = s.get("event") or {}
            opts = [o for o in ev.get("options", []) if not o.get("is_locked")]
            if opts:
                act("choose_event_option", index=opts[0].get("index", 0))
            else:
                act("advance_dialogue")
        elif t == "rewards":
            act("proceed")
        elif t == "card_reward":
            act("skip_card_reward")
        elif t in ("rest_site", "shop", "treasure", "fake_merchant"):
            act("proceed")
        time.sleep(1.5)
    return None


def incoming(s):
    total = 0
    for e in s.get("battle", {}).get("enemies", []):
        for it in e.get("intents") or []:
            if it.get("type", "").startswith("Attack"):
                label = str(it.get("label", "0")).lower().replace("x", "*")
                try:
                    total += eval(label, {"__builtins__": {}})  # labels look like "12" or "5x3"
                except Exception:
                    pass
    return total


def play_turn(s):
    """Block first while the attack is unblocked, then hit the weakest enemy; stop when nothing is playable."""
    for _ in range(12):
        p = s["player"]
        hand = p.get("hand", [])
        enemies = [e for e in s.get("battle", {}).get("enemies", []) if (e.get("hp") or 0) > 0]
        if not enemies:
            return
        energy = int(p.get("energy") or 0)
        need_block = incoming(s) > int(p.get("block") or 0)

        def playable(c):
            cost = str(c.get("cost", "0"))
            return c.get("can_play", True) and (not cost.isdigit() or int(cost) <= energy)

        cards = [(i, c) for i, c in enumerate(hand) if playable(c)]
        defend = [(i, c) for i, c in cards if c.get("target_type") == "Self"]
        attack = [(i, c) for i, c in cards if c.get("target_type") == "AnyEnemy"]
        if need_block and defend:
            act("play_card", card_index=defend[0][0])
        elif attack:
            target = min(enemies, key=lambda e: e.get("hp") or 0)["entity_id"]
            act("play_card", card_index=attack[0][0], target=target)
        elif defend:
            act("play_card", card_index=defend[0][0])
        else:
            return
        time.sleep(1.0)
        s = get_state()
        if s.get("state_type") not in IN_COMBAT:
            return


def console(command):
    """Run a dev-console command through STS2 MCP's `console` action (needs the console-action build)."""
    res = act("console", command=command)
    result = res.get("result") or {}
    if str(result.get("success")).lower() != "true":
        print(f"  console '{command}': {result.get('msg') or res.get('error')}", file=sys.stderr)
    return result


def start_fight(encounter):
    console(f"fight {encounter}")
    return wait_for(lambda st: st.get("state_type") in IN_COMBAT and st.get("battle", {}).get("is_play_phase"), timeout=30)


def next_round(s):
    """End the turn and wait for the next play phase (or the fight to end)."""
    rnd = s.get("battle", {}).get("round", 0)
    act("end_turn")
    return wait_for(lambda st: st.get("state_type") not in IN_COMBAT
                    or (st.get("battle", {}).get("is_play_phase") and st.get("battle", {}).get("round", 0) > rnd),
                    timeout=45)


def godmode(on):
    """Set the console's godmode, which keeps a fight from ending the run (a death on a fresh profile leaves
    a Timeline unlock that blocks the menu). It also makes your hits one-shot and shows a 999999999 HP
    readout, so it's only ever on in fights that aren't captured. `godmode` toggles; its reply says which."""
    msg = str(console("godmode").get("msg", "")).lower()
    if ("deactivated" not in msg) != on:
        console("godmode")


def win_fight(max_rounds=30):
    """Play it out normally (no console kills or buffs, which would show in the summary) and wait for the
    rewards screen, where the combat summary appears."""
    s = get_state()
    for _ in range(max_rounds):
        if s.get("state_type") not in IN_COMBAT:
            break
        play_turn(s)
        if get_state().get("state_type") in IN_COMBAT:
            s = next_round(get_state())
        else:
            break
    s = wait_for(lambda st: st.get("state_type") in ("rewards", "game_over"), timeout=30)
    time.sleep(2.5)  # the summary fades in over the rewards screen
    return s


def between_fights():
    """Out of combat (rewards screen): restore HP and turn invincibility off. Nothing here is logged in a fight."""
    console("heal 999")
    godmode(False)


def scene_fight(out):
    """A two-Nibbit fight with a couple of potions on the belt, played normally: turn-1 forecast, the round
    strip at round 4 (potion damage included), then the summary with its potions row. (The elite is too much
    for this script's simple play and would end the run.)"""
    console("potion FIRE_POTION")
    console("potion BLOCK_POTION")
    s = start_fight("NIBBITS_NORMAL")
    time.sleep(1.5)  # let the forecast panel slide in
    capture(os.path.join(out, "forecast.png"))
    for rnd in range(1, 5):
        if s.get("state_type") not in IN_COMBAT:
            break
        if rnd == 2:
            enemy = next(e["entity_id"] for e in s["battle"]["enemies"] if (e.get("hp") or 0) > 0)
            act("use_potion", slot=0, target=enemy)
            act("use_potion", slot=1)
            time.sleep(1.0)
            s = get_state()
        if rnd == 4:
            capture(os.path.join(out, "rounds.png"))
        play_turn(s)
        s = next_round(s)
    # Played out for real (godmode would one-shot it and spoil the summary).
    if win_fight().get("state_type") == "rewards":
        capture(os.path.join(out, "summary.png"))
    between_fights()


def scene_lethal(out):
    """Low HP walking into an elite's opening hit, so the forecast flags lethal. HP is lowered in a throwaway
    fight first, so the captured fight's own log and strip stay clean."""
    s = start_fight("NIBBITS_WEAK")
    hp = int(s["player"].get("hp") or 0)
    # Down to 6 HP: the Ironclad's Burning Blood heals 6 after this fight, so the elite meets 12 HP.
    console(f"damage {max(0, hp - 6)} 0")  # 0 = the player
    godmode(True)
    win_fight()
    godmode(False)
    s = start_fight("BYRDONIS_ELITE")
    time.sleep(1.5)
    capture(os.path.join(out, "lethal.png"))
    godmode(True)  # not captured past this point; just don't let the 12 HP end the run
    win_fight()
    between_fights()


SCENES = {"fight": scene_fight, "lethal": scene_lethal}


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--out", default=os.path.join(HERE, "..", "docs", "screenshots"))
    ap.add_argument("scenes", nargs="*", default=list(SCENES), help=f"scenes to shoot (default all: {', '.join(SCENES)})")
    args = ap.parse_args()
    os.makedirs(args.out, exist_ok=True)

    if to_combat() is None:
        sys.exit("couldn't reach a run (start from the main menu or mid-run)")
    godmode(True)  # the run's first fight is just a way in; finish it safely
    win_fight()
    between_fights()
    for name in args.scenes:
        print(f"scene {name}")
        SCENES[name](args.out)
    print("done")


if __name__ == "__main__":
    main()
