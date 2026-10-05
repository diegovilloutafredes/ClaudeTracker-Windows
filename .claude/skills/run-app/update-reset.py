"""Removes what the update tests left in the app's settings (the app must not be running).

Left behind, "lastNotifiedUpdateVersion: 0.2.0" would keep a real 0.2.0 from ever being
announced or installed by itself: a version is announced once.
  python update-reset.py            the update bookkeeping
  python update-reset.py autoUpdate the bookkeeping and that preference too
  python update-reset.py --set autoUpdate=false   then set one
"""
import io
import json
import os
import sys

path = os.path.join(os.environ["APPDATA"], "ClaudeTracker", "settings.json")
settings = json.load(io.open(path, encoding="utf-8"))
gone = ["lastNotifiedUpdateVersion", "failedInstallVersion", "failedInstallCount", "updateCheckInterval"]
arguments = sys.argv[1:]
setting = None
if "--set" in arguments:
    setting = arguments[arguments.index("--set") + 1]
    arguments = [a for a in arguments if a not in ("--set", setting)]
gone += arguments
removed = [key for key in gone if settings.pop(key, None) is not None]
if setting:
    key, value = setting.split("=", 1)
    settings[key] = {"true": True, "false": False}.get(value, value)
with io.open(path, "w", encoding="utf-8", newline="\n") as handle:
    json.dump(settings, handle, indent=2, ensure_ascii=False, sort_keys=True)
    handle.write("\n")
print("removed:", ", ".join(removed) or "nothing", "| set:", setting or "nothing")
