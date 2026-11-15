"""Sync the project's JSON parameters with a MonsterIntentRandomizer game config."""

import argparse
import json
import os
import re
import shutil
import tempfile
from pathlib import Path


FILES = {"Settings": "settings.json", "Profiles": "monster-profiles.json"}
HERE = Path(__file__).resolve().parent
PROJECT = HERE.parent if HERE.name == "tools" else HERE


def read_json(path):
    with path.open(encoding="utf-8-sig") as file:
        value = json.load(file)
    if not isinstance(value, dict):
        raise ValueError(f"Expected a JSON object in {path}")
    return value


def write_json(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.exists():
        shutil.copy2(path, path.with_name(path.name + ".bak"))
    with tempfile.NamedTemporaryFile(
        mode="w", encoding="utf-8", dir=path.parent, delete=False
    ) as file:
        temporary = Path(file.name)
        try:
            json.dump(data, file, ensure_ascii=False, indent=2, allow_nan=False)
            file.write("\n")
            file.close()
            temporary.replace(path)
        finally:
            file.close()
            temporary.unlink(missing_ok=True)


def main():
    appdata = os.environ.get("APPDATA")
    default_game = (
        Path(appdata) / "SlayTheSpire2" / "mod_data" / "MonsterIntentRandomizer" / "config.json"
        if appdata else Path("config.json")
    )
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("diff", "pull", "push"), nargs="?", default="diff")
    parser.add_argument("--game-config", type=Path, default=default_game)
    parser.add_argument("--data-dir", type=Path, default=PROJECT / "data")
    parser.add_argument("--scope", choices=("settings", "profiles", "both"), default="both")
    parser.add_argument("--revision", type=int, help="Schema revision, for standalone copies")
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()
    sections = FILES if args.scope == "both" else (args.scope.title(),)
    revision = args.revision
    if revision is None:
        source = PROJECT / "mod" / "ModEntry.cs"
        if source.exists():
            match = re.search(r"SettingsRevision\s*=\s*(\d+)", source.read_text(encoding="utf-8"))
            revision = int(match.group(1)) if match else None

    try:
        exists = args.game_config.exists()
        if not exists and args.command != "push":
            raise FileNotFoundError(args.game_config)
        game = read_json(args.game_config) if exists else {"Enabled": True, "ShowIntentDetails": True}
        saved_revision = game.get("Revision")
        if revision is not None and saved_revision != revision and args.command != "diff":
            if args.command == "pull" or args.scope != "both":
                raise ValueError("Schema revisions differ. Save settings once in the updated game before syncing.")
        updates = {}
        for section in sections:
            path = args.data_dir / FILES[section]
            if args.game_config.resolve() == path.resolve():
                raise ValueError("Choose a game config file separate from the project JSON files.")
            project_value = read_json(path) if path.exists() else None
            saved_value = game.get(section)
            if project_value == saved_value and isinstance(project_value, dict):
                print(f"{section}: equal")
                continue
            if args.command == "diff":
                print(f"{section}: different")
                continue
            value = saved_value if args.command == "pull" else project_value
            if not isinstance(value, dict):
                raise ValueError(f"Missing input JSON object for {section}")
            updates[section] = value

        if args.command == "pull":
            for section, value in updates.items():
                path = args.data_dir / FILES[section]
                if not args.dry_run:
                    write_json(path, value)
                print(f"{section} -> {path}")
        elif args.command == "push" and (updates or (revision is not None and saved_revision != revision)):
            if not exists and args.scope != "both":
                raise ValueError("Use --scope both to create a new game config.")
            if not exists and revision is None:
                raise ValueError("Pass --revision to create a game config from a standalone copy.")
            game.update(updates)
            if revision is not None:
                game["Revision"] = revision
            if not args.dry_run:
                write_json(args.game_config, game)
            print(f"{', '.join(updates) or 'Revision'} -> {args.game_config}")
    except (OSError, ValueError) as error:
        parser.exit(1, f"{error}\n")


if __name__ == "__main__":
    main()
