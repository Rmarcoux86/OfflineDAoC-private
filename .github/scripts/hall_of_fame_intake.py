#!/usr/bin/env python3
"""Hall of Fame and Gauntlet spectator intake for .github/workflows/hall-of-fame.yml.

Players open an issue titled "[Hall of Fame] <name>" (an expedition win) or "[Gauntlet Champion] <name>" (the look of a
character that cleared the Gauntlet, shown as a peaceful spectator at the Gauntlet arena) whose body holds the JSON
object in a ```json fenced block, or links an attached .json file. This script reads the issue from the event file only
(issue text never reaches a shell), validates it and adds it to the shared file on main:

  validate WORK   read $GITHUB_EVENT_PATH, check the JSON, write WORK/meta.json and WORK/payload.json
  apply WORK      merge the payload into the repository file (dedupe by id), write WORK/result.json and the commit message
  report WORK     comment on the issue with the result and close it (uses $GITHUB_TOKEN; $PUSHED is the push outcome)
"""

import datetime
import json
import os
import re
import subprocess
import sys
import urllib.request

HALL_PREFIX = "[Hall of Fame]"
GUEST_PREFIX = "[Gauntlet Champion]"
HALL_FILE = "hall-of-fame/hall-of-fame.json"
# The spectators; the fork update installs it as runtime\server\gauntlet-spectators.json (fork-release.yml).
GUEST_FILE = "gauntlet/guest-champions.json"

MAX_BODY_CHARS = 200_000
MAX_GUEST_BYTES = 60 * 1024
# A win; since 2026-10-27 it may carry the character's spectator look in a "spectator" field (one submission).
MAX_HALL_BYTES = 4 * 1024 + MAX_GUEST_BYTES

REALMS = ("Albion", "Midgard", "Hibernia")
ISO_UTC = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$")
CHARACTER_ID = re.compile(r"^[A-Za-z0-9_-]{1,64}$")
CHARACTER_NAME = re.compile(r"^[A-Za-z][A-Za-z' -]{0,31}$")
CLASS_OR_RACE = re.compile(r"^[A-Za-z][A-Za-z ]{0,31}$")
FIELD_NAME = re.compile(r"^[A-Za-z0-9_]{1,40}$")
FENCED_JSON = re.compile(r"```json[ \t]*\r?\n(.*?)\r?\n[ \t]*```", re.DOTALL)
ATTACHMENT = re.compile(
    r"https://github\.com/(?:user-attachments/files/\d+/[A-Za-z0-9._-]+\.json"
    r"|[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/files/\d+/[A-Za-z0-9._-]+\.json)"
)

# Hall of Fame fields: name -> (type, required). Ints are (min, max).
HALL_STRINGS = {"id": True, "character": True, "className": True, "race": False, "realm": True, "endedUtc": True}
HALL_INTS = {
    "level": (1, 100),
    "seed": (-(2**31), 2**31 - 1),
    "playedSeconds": (0, 10**9),
    "kills": (0, 10**7),
    "bossesKilled": (0, 10**5),
    "uniques": (0, 10**5),
    "realmPoints": (0, 10**12),
}
HALL_ORDER = [
    "id", "character", "className", "race", "realm", "level", "seed", "playedSeconds", "endedUtc",
    "kills", "bossesKilled", "uniques", "realmPoints", "gauntletCleared", "boons",
]
# The run's boons by name (since 2026-10-27; optional for older launchers).
BOON_NAME = re.compile(r"^[A-Za-z][A-Za-z' -]{0,31}$")
MAX_BOONS = 10
# Set by this workflow; a submitted value is ignored and replaced.
ADDED_BY_WORKFLOW = ("player", "submittedUtc")


def is_int(value):
    return isinstance(value, int) and not isinstance(value, bool)


def is_number(value):
    return (isinstance(value, (int, float))) and not isinstance(value, bool)


def field_label(name):
    return f'"{name}"' if FIELD_NAME.match(name) else "(name not shown)"


def write_json(path, value):
    with open(path, "w", encoding="utf-8", newline="\n") as handle:
        json.dump(value, handle, indent=2, ensure_ascii=False)
        handle.write("\n")


def read_json(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def fetch_attachment(url, work, cap):
    """Downloads an attached .json with curl (https only, size and time capped). Returns (text, error)."""
    target = os.path.join(work, "attachment.json")
    command = [
        "curl", "-sSfL", "--proto", "=https", "--proto-redir", "=https", "--max-redirs", "5",
        "--max-time", "30", "--max-filesize", str(cap), "-o", target, url,
    ]
    completed = subprocess.run(command, capture_output=True, text=True, check=False)

    if completed.returncode != 0:
        return None, "The attached .json file could not be downloaded (or it is too large)."

    size = os.path.getsize(target)

    if size > cap:
        return None, f"The attached file is {size} bytes; the limit is {cap}."

    with open(target, "rb") as handle:
        raw = handle.read()

    try:
        return raw.decode("utf-8-sig"), None
    except UnicodeDecodeError:
        return None, "The attached file is not UTF-8 text."


def extract_json_text(body, work, cap):
    """The JSON text from a ```json block in the body, else from an attached .json link. Returns (text, error)."""
    match = FENCED_JSON.search(body)

    if match:
        return match.group(1), None

    link = ATTACHMENT.search(body)

    if link:
        return fetch_attachment(link.group(0), work, cap)

    return None, "No ```json block and no attached .json file was found in the issue."


def validate_hall(entry):
    errors = []

    for name in entry:
        if name not in HALL_STRINGS and name not in HALL_INTS and name not in ("gauntletCleared", "spectator", "boons") and name not in ADDED_BY_WORKFLOW:
            errors.append(f"Unknown field {field_label(name)}.")

    for name, required in HALL_STRINGS.items():
        if name not in entry:
            if required:
                errors.append(f'Missing "{name}".')

            continue

        if not isinstance(entry[name], str):
            errors.append(f'"{name}" must be a string.')

    for name, (low, high) in HALL_INTS.items():
        if name not in entry:
            errors.append(f'Missing "{name}".')
        elif not is_int(entry[name]):
            errors.append(f'"{name}" must be a whole number.')
        elif not low <= entry[name] <= high:
            errors.append(f'"{name}" is out of range.')

    if "gauntletCleared" not in entry:
        errors.append('Missing "gauntletCleared".')
    elif not isinstance(entry["gauntletCleared"], bool):
        errors.append('"gauntletCleared" must be true or false.')

    if "boons" in entry:
        boons = entry["boons"]

        if not isinstance(boons, list) or len(boons) > MAX_BOONS:
            errors.append(f'"boons" must be a list of at most {MAX_BOONS} names.')
        elif not all(isinstance(boon, str) and BOON_NAME.match(boon) for boon in boons):
            errors.append('"boons" holds a name that is not a boon name.')

    if errors:
        return errors

    if not CHARACTER_NAME.match(entry["character"]):
        errors.append('"character" is not a valid character name (letters, at most 32).')

    if not CLASS_OR_RACE.match(entry["className"]):
        errors.append('"className" is not valid.')

    if "race" in entry and not CLASS_OR_RACE.match(entry["race"]):
        errors.append('"race" is not valid.')

    if entry["realm"] not in REALMS:
        errors.append('"realm" must be Albion, Midgard or Hibernia.')

    if not ISO_UTC.match(entry["endedUtc"]):
        errors.append('"endedUtc" is not an ISO 8601 time.')

    character_id, separator, ended = entry["id"].partition(":")

    if not separator or not CHARACTER_ID.match(character_id) or ended != entry["endedUtc"]:
        errors.append('"id" must be "<character id>:<endedUtc>".')

    return errors


def validate_guest(entry, size):
    errors = []

    if size >= MAX_GUEST_BYTES:
        errors.append(f"The snapshot is {size} bytes; it must be under {MAX_GUEST_BYTES}.")

    if not isinstance(entry.get("id"), str) or not 0 < len(entry["id"].strip()) <= 128:
        errors.append('"id" must be a string of 1 to 128 characters.')

    if not isinstance(entry.get("name"), str) or not 0 < len(entry["name"].strip()) <= 64:
        errors.append('"name" must be a string of 1 to 64 characters.')
    elif not CHARACTER_NAME.match(entry["name"].strip()):
        errors.append('"name" is not a valid character name (letters, at most 32).')

    if not is_number(entry.get("classId")):
        errors.append('"classId" must be a number.')

    if not isinstance(entry.get("items"), list):
        errors.append('"items" must be an array.')

    return errors


def command_validate(work):
    with open(os.environ["GITHUB_EVENT_PATH"], encoding="utf-8") as handle:
        event = json.load(handle)

    issue = event.get("issue") or {}
    title = (issue.get("title") or "").strip()
    body = issue.get("body") or ""
    author = ((issue.get("user") or {}).get("login") or "").strip()
    meta = {
        "kind": "hall" if title.startswith(HALL_PREFIX) else "guest" if title.startswith(GUEST_PREFIX) else "",
        "number": int(issue.get("number") or 0),
        "action": event.get("action") or "",
        "author": author,
        "ok": False,
        "errors": [],
    }

    if not meta["kind"]:
        meta["errors"].append("The issue title starts with neither [Hall of Fame] nor [Gauntlet Champion].")
    elif not author:
        meta["errors"].append("The issue has no author.")
    elif len(body) > MAX_BODY_CHARS:
        meta["errors"].append("The issue text is too long.")

    entry = None

    if not meta["errors"]:
        cap = MAX_HALL_BYTES if meta["kind"] == "hall" else MAX_GUEST_BYTES
        text, error = extract_json_text(body, work, cap)

        if error:
            meta["errors"].append(error)
        elif len(text.encode("utf-8")) > cap:
            meta["errors"].append(f"The JSON is larger than {cap} bytes.")
        else:
            try:
                entry = json.loads(text)
            except ValueError:
                meta["errors"].append("The JSON block could not be read; please do not edit it.")

    if entry is not None and not isinstance(entry, dict):
        meta["errors"].append("The JSON must be one object.")
        entry = None

    if entry is not None:
        if meta["kind"] == "hall":
            meta["errors"].extend(validate_hall(entry))
            spectator = entry.get("spectator")

            if spectator is not None and not meta["errors"]:
                if not isinstance(spectator, dict):
                    meta["errors"].append('"spectator" must be an object.')
                else:
                    meta["errors"].extend("Spectator: " + error for error in validate_guest(spectator, len(json.dumps(spectator).encode("utf-8"))))

                    if not meta["errors"] and (spectator["id"].strip() != entry["id"].partition(":")[0] or spectator["name"].strip() != entry["character"]):
                        meta["errors"].append('"spectator" is not the same character as the win.')
        else:
            meta["errors"].extend(validate_guest(entry, len(text.encode("utf-8"))))

    if entry is not None and not meta["errors"]:
        submitted = datetime.datetime.now(datetime.timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")

        if meta["kind"] == "hall":
            payload = {name: entry[name] for name in HALL_ORDER if name in entry}
            spectator = entry.get("spectator")

            if isinstance(spectator, dict):
                guest = {name: value for name, value in spectator.items() if name not in ADDED_BY_WORKFLOW}
                guest["id"] = guest["id"].strip()
                guest["player"] = author
                guest["submittedUtc"] = submitted
                write_json(os.path.join(work, "spectator.json"), guest)
        else:
            payload = {name: value for name, value in entry.items() if name not in ADDED_BY_WORKFLOW}
            payload["id"] = payload["id"].strip()

        payload["player"] = author
        payload["submittedUtc"] = submitted
        write_json(os.path.join(work, "payload.json"), payload)
        meta["ok"] = True

    write_json(os.path.join(work, "meta.json"), meta)


def load_entries(path):
    if not os.path.exists(path):
        return {"entries": []}

    data = read_json(path)

    if not isinstance(data, dict) or not isinstance(data.get("entries"), list):
        raise SystemExit(f"{path} is not an object with an entries array.")

    return data


def same_entry(old, new):
    keep = lambda entry: {name: value for name, value in entry.items() if name != "submittedUtc"}

    return keep(old) == keep(new)


def upsert(path, payload, author):
    """Adds or updates the entry with payload's id in path; the status, and whether the file changed."""
    data = load_entries(path)
    entries = data["entries"]
    index = next((i for i, entry in enumerate(entries) if isinstance(entry, dict) and entry.get("id") == payload["id"]), None)

    if index is None:
        entries.append(payload)
        status = "added"
    elif entries[index].get("player") != author:
        status = "conflict"
    elif same_entry(entries[index], payload):
        status = "unchanged"
    else:
        entries[index] = payload
        status = "updated"

    if status in ("added", "updated"):
        os.makedirs(os.path.dirname(path), exist_ok=True)
        write_json(path, data)

    return status


def command_apply(work):
    meta = read_json(os.path.join(work, "meta.json"))
    result = {"status": "invalid"}

    if meta.get("ok"):
        payload = read_json(os.path.join(work, "payload.json"))
        path = HALL_FILE if meta["kind"] == "hall" else GUEST_FILE
        result["status"] = upsert(path, payload, meta["author"])
        changes = []

        if result["status"] in ("added", "updated"):
            what = "Hall of Fame win" if meta["kind"] == "hall" else "Gauntlet spectator"
            changes.append(f"{'Add' if result['status'] == 'added' else 'Update'} {what}")

        # A win's spectator look (owner 2026-10-27): the character joins the Gauntlet's spectators with the same submission.
        spectator_path = os.path.join(work, "spectator.json")

        if meta["kind"] == "hall" and os.path.exists(spectator_path):
            result["spectator"] = upsert(GUEST_FILE, read_json(spectator_path), meta["author"])

            if result["spectator"] in ("added", "updated"):
                changes.append(f"{'add' if result['spectator'] == 'added' else 'update'} the Gauntlet spectator" if changes else
                               f"{'Add' if result['spectator'] == 'added' else 'Update'} Gauntlet spectator")

        if changes:
            with open(os.path.join(work, "commit-message.txt"), "w", encoding="utf-8", newline="\n") as handle:
                handle.write(f"{' and '.join(changes)} from issue #{int(meta['number'])}\n")

    write_json(os.path.join(work, "result.json"), result)


def github(method, path, body):
    request = urllib.request.Request(
        "https://api.github.com/repos/" + os.environ["GITHUB_REPOSITORY"] + path,
        data=json.dumps(body).encode("utf-8"),
        method=method,
        headers={
            "Authorization": "Bearer " + os.environ["GITHUB_TOKEN"],
            "Accept": "application/vnd.github+json",
            "Content-Type": "application/json",
            "User-Agent": "OfflineDAoC-hall-of-fame",
        },
    )

    with urllib.request.urlopen(request, timeout=30) as response:
        response.read()


def command_report(work):
    meta_path = os.path.join(work, "meta.json")

    if not os.path.exists(meta_path):
        print("No meta.json: the validate step did not run.")

        return

    meta = read_json(meta_path)
    number = int(meta.get("number") or 0)

    if number <= 0:
        return

    result_path = os.path.join(work, "result.json")
    result = read_json(result_path) if os.path.exists(result_path) else {"status": "invalid"}
    status = result["status"]
    spectator = result.get("spectator")

    # A win already listed whose look was just added: report the spectator.
    if status == "unchanged" and spectator in ("added", "updated"):
        status = "spectator-only"
    pushed = os.environ.get("PUSHED", "")
    hall = meta.get("kind") == "hall"
    what = "your win" if hall else "your champion"
    where = "the Hall of Fame" if hall else "the Gauntlet's spectators"
    close_reason = "completed"

    if not meta.get("ok"):
        lines = [f"This could not be added to {where}:", ""] + ["- " + error for error in meta.get("errors", [])]
        source = "the launcher's SHARE MY WIN"
        lines += ["", f"Please submit again from {source} (or fix the JSON and edit this issue)."]
        close_reason = "not_planned"
    elif status == "conflict":
        lines = [f"Another player already submitted this id to {where}; only that player can update it."]
        close_reason = "not_planned"
    elif status == "unchanged":
        if meta.get("action") == "edited":
            # An edit that changes nothing: close quietly.
            github("PATCH", f"/issues/{number}", {"state": "closed", "state_reason": "completed"})

            return

        lines = [f"This is already in {where}, unchanged."]
    elif status == "spectator-only" and pushed == "yes":
        lines = ["Your win was already in the Hall of Fame; your character now joins the Gauntlet's spectators from the next fork update."]
    elif pushed == "yes":
        verb = "added to" if status == "added" else "updated in"
        after = "Launchers show it on their next refresh." if hall else "It watches the Gauntlet from the next fork update."
        lines = [f"Thanks! {what[0].upper() + what[1:]} was {verb} {where}. {after}"]

        if hall and spectator in ("added", "updated"):
            lines.append("Your character also joins the Gauntlet's spectators from the next fork update.")
    else:
        lines = ["The entry is valid, but saving it failed (the repository changed too often). The owner will re-run it."]
        github("POST", f"/issues/{number}/comments", {"body": "\n".join(lines)})
        sys.exit(1)

    github("POST", f"/issues/{number}/comments", {"body": "\n".join(lines)})
    github("PATCH", f"/issues/{number}", {"state": "closed", "state_reason": close_reason})


def main():
    if len(sys.argv) != 3 or sys.argv[1] not in ("validate", "apply", "report"):
        raise SystemExit("usage: hall_of_fame_intake.py validate|apply|report WORK")

    work = sys.argv[2]
    os.makedirs(work, exist_ok=True)
    {"validate": command_validate, "apply": command_apply, "report": command_report}[sys.argv[1]](work)


if __name__ == "__main__":
    main()
