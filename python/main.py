import os
import json
import subprocess
import shutil
import logging
from concurrent.futures import ThreadPoolExecutor
import sys
import io
from datetime import datetime

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding='utf-8', line_buffering=True)
sys.stderr = io.TextIOWrapper(sys.stderr.buffer, encoding='utf-8', line_buffering=True)

BASE_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(BASE_DIR, "config.json")

STEAMCMD_PATH  = os.path.join(BASE_DIR, "Tools", "steamcmd", "steamcmd.exe")
GMAD_PATH      = os.path.join(BASE_DIR, "Tools", "gmod",     "gmad.exe")
WORKSHOP_ROOT  = os.path.join(BASE_DIR, "Tools", "steamcmd")
EXTRACT_DIR    = os.path.join(BASE_DIR, "extracted")
BACKUP_DIR     = os.path.join(BASE_DIR, "backups")
APP_ID         = "4000"

def load_config():
    if not os.path.exists(CONFIG_PATH):
        print("[!] config.json not found. Creating default — please set game_dir inside it.", flush=True)
        default = {"game_dir": ""}
        with open(CONFIG_PATH, "w") as f:
            json.dump(default, f, indent=4)
        return default
    with open(CONFIG_PATH, "r") as f:
        return json.load(f)

CONFIG = load_config()

def find_7zip():
    candidates = [
        r"C:\Program Files\7-Zip\7z.exe",
        r"C:\Program Files (x86)\7-Zip\7z.exe",
    ]
    for path in candidates:
        if os.path.exists(path):
            return path
    found = shutil.which("7z")
    if found:
        return found
    return None

SEVENZIP_PATH = find_7zip()

logging.basicConfig(
    filename=os.path.join(BASE_DIR, "app.log"),
    level=logging.INFO,
    format="%(asctime)s | %(levelname)s | %(message)s",
    filemode="w"
)
log = logging.getLogger()

def run_streaming(cmd, label=""):
    process = subprocess.Popen(
        cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT,
        text=True, encoding="utf-8", errors="replace"
    )
    for line in iter(process.stdout.readline, ""):
        line = line.rstrip("\n")
        if line:
            prefix = f"[{label}] " if label else ""
            print(f"{prefix}{line}", flush=True)
    process.stdout.close()
    process.wait()
    return process.returncode

def check_dependencies():
    missing = False
    if not os.path.exists(STEAMCMD_PATH):
        print(f"[MISSING] SteamCMD not found at: {STEAMCMD_PATH}", flush=True)
        missing = True
    if not os.path.exists(GMAD_PATH):
        print(f"[MISSING] GMAD not found at: {GMAD_PATH}", flush=True)
        missing = True
    if not SEVENZIP_PATH:
        print("[MISSING] 7-Zip not found — please install it from https://www.7-zip.org", flush=True)
        missing = True
    else:
        print(f"[OK] 7-Zip found at: {SEVENZIP_PATH}", flush=True)
    if not CONFIG.get("game_dir"):
        print("[MISSING] game_dir is not set in config.json — please set it to your garrysmod/addons path", flush=True)
        missing = True
    elif not os.path.exists(CONFIG["game_dir"]):
        print(f"[MISSING] game_dir does not exist: {CONFIG['game_dir']}", flush=True)
        missing = True
    workshop_base = os.path.join(WORKSHOP_ROOT, "steamapps", "workshop", "content", APP_ID)
    if not os.path.exists(workshop_base):
        print(f"[INFO] Workshop content folder not found yet — SteamCMD will create it on first download", flush=True)
    if missing:
        print("\n[!] Preflight failed", flush=True)
        return False
    print("[OK] All checks passed", flush=True)
    return True

def download_queue(queue):
    print("\n[PHASE 1] Downloading all items...\n", flush=True)
    cmd = [STEAMCMD_PATH, "+login", "anonymous"]
    for item_id in queue:
        cmd += ["+workshop_download_item", APP_ID, item_id]
    cmd += ["+quit"]
    run_streaming(cmd, label="STEAMCMD")
    print("\n[SUCCESS] PHASE 1 COMPLETE\n", flush=True)

def find_file(item_id):
    base = os.path.join(WORKSHOP_ROOT, "steamapps", "workshop", "content", APP_ID, item_id)
    if not os.path.exists(base):
        return None
    for root, _, files in os.walk(base):
        for f in files:
            if f.endswith((".gma", ".bin")):
                return os.path.join(root, f)
    return None

def is_gma_file(path):
    try:
        with open(path, "rb") as f:
            return f.read(4) == b"GMAD"
    except:
        return False

def clean_name(name):
    bad = '<>:"/\\|?*'
    for c in bad:
        name = name.replace(c, "")
    return name.strip().replace(" ", "_")

def get_addon_name(gma_file):
    try:
        with open(gma_file, "rb") as f:
            data = f.read(4096)
        text = data.decode("utf-8", errors="ignore")
        parts = text.split("\x00")
        for p in parts:
            p = p.strip()
            if len(p) < 4:
                continue
            if "GMAD" in p:
                continue
            name = clean_name(p)
            if len(name) >= 3 and all(c.isprintable() or c in " _-." for c in name):
                return name
    except:
        pass
    return None

def extract_bin(bin_file, item_id):
    temp = os.path.join(EXTRACT_DIR, f"{item_id}_bin")
    os.makedirs(temp, exist_ok=True)
    run_streaming([SEVENZIP_PATH, "x", bin_file, f"-o{temp}", "-y"], label="7ZIP")
    for root, _, files in os.walk(temp):
        for f in files:
            full = os.path.join(root, f)
            if is_gma_file(full):
                print(f"[BIN] Found GMA (via magic bytes): {f}", flush=True)
                return full
    print(f"[X] BIN extraction yielded no valid GMA for {item_id}", flush=True)
    return None

def extract_gma(gma_file, item_id):
    out = os.path.join(EXTRACT_DIR, item_id)
    os.makedirs(out, exist_ok=True)
    returncode = run_streaming([GMAD_PATH, "extract", "-file", gma_file, "-out", out], label="GMAD")
    if returncode != 0:
        return None
    return out

def find_real_addon_root(path):
    for root, dirs, files in os.walk(path):
        if any(d in ["lua", "materials", "models", "sound"] for d in dirs):
            return root
    for root, dirs, files in os.walk(path):
        if files:
            return root
    return path

def get_dir_size(path):
    total = 0
    for dirpath, _, filenames in os.walk(path):
        for f in filenames:
            fp = os.path.join(dirpath, f)
            if os.path.exists(fp):
                total += os.path.getsize(fp)
    return total

def backup_existing(dst, item_id):
    if not os.path.exists(dst):
        return None
    os.makedirs(BACKUP_DIR, exist_ok=True)
    folder_name = os.path.basename(dst)
    timestamp = datetime.now().strftime("%Y-%m-%d_%H%M%S")
    backup_path = os.path.join(BACKUP_DIR, f"{folder_name}_{timestamp}")
    try:
        try:
            shutil.move(dst, backup_path)
        except OSError:
            shutil.copytree(dst, backup_path)
            shutil.rmtree(dst)
        print(f"[BACKUP] Previous version moved to: {backup_path}", flush=True)
        log.info(f"Backup created: {backup_path}")
        return backup_path
    except Exception as e:
        print(f"[BACKUP] Failed to create backup: {e}", flush=True)
        log.error(f"Backup failed: {e}")
        return None

def detect_conflicts(install_path, item_id):
    game_dir = CONFIG.get("game_dir", "")
    if not game_dir or not os.path.exists(game_dir):
        return []
    conflicts = set()
    new_files = set()
    for dirpath, _, filenames in os.walk(install_path):
        for f in filenames:
            full = os.path.join(dirpath, f)
            rel = os.path.relpath(full, install_path)
            new_files.add(rel.lower())
    for addon_folder in os.listdir(game_dir):
        addon_path = os.path.join(game_dir, addon_folder)
        if not os.path.isdir(addon_path):
            continue
        if os.path.abspath(addon_path) == os.path.abspath(install_path):
            continue
        for dirpath, _, filenames in os.walk(addon_path):
            for f in filenames:
                full = os.path.join(dirpath, f)
                rel = os.path.relpath(full, addon_path)
                if rel.lower() in new_files:
                    conflicts.add(addon_folder)
                    break
            if addon_folder in conflicts:
                break
    return sorted(list(conflicts))

def install(extract_path, addon_name, item_id):
    real_src = find_real_addon_root(extract_path)
    if not os.path.exists(real_src):
        print("[X] Missing extracted content", flush=True)
        return False
    folder_name = addon_name if addon_name else os.path.basename(real_src)
    folder_name = clean_name(folder_name)
    dst = os.path.join(CONFIG["game_dir"], folder_name)
    backup_path = None
    if os.path.exists(dst):
        backup_path = backup_existing(dst, item_id)
        if backup_path:
            print(f"BACKUP_DATA: {json.dumps({'id': item_id, 'backup_path': backup_path})}", flush=True)
    shutil.copytree(real_src, dst)
    size = get_dir_size(dst)
    print(f"INSTALL_DATA: {json.dumps({'id': item_id, 'path': dst, 'size_bytes': size})}", flush=True)
    conflicts = detect_conflicts(dst, item_id)
    if conflicts:
        print(f"CONFLICT_DATA: {json.dumps({'id': item_id, 'conflicts_with': conflicts})}", flush=True)
    print(f"[SUCCESS] Installed to: {dst}", flush=True)
    log.info(f"Installed to: {dst}")
    return True

def process(item_id):
    force = item_id.startswith("!")
    if force:
        item_id = item_id[1:]
    file = find_file(item_id)
    if not file:
        print(f"[X] No file for {item_id}", flush=True)
        return
    addon_name = None
    extract_path = None
    if file.endswith(".bin"):
        gma = extract_bin(file, item_id)
        if not gma:
            print(f"[X] BIN had no GMA for {item_id}", flush=True)
            return
        addon_name = get_addon_name(gma)
        if not addon_name or len(addon_name) < 3:
            addon_name = f"Workshop_{item_id}"
            print(f"[WARN] Could not read addon name from GMA, using: {addon_name}", flush=True)
        extract_path = extract_gma(gma, item_id)
    elif file.endswith(".gma"):
        addon_name = get_addon_name(file)
        if not addon_name or len(addon_name) < 3:
            addon_name = f"Workshop_{item_id}"
            print(f"[WARN] Could not read addon name from GMA, using: {addon_name}", flush=True)
        extract_path = extract_gma(file, item_id)
    else:
        if is_gma_file(file):
            print(f"[WARN] Unrecognised extension but valid GMA magic, attempting extraction: {file}", flush=True)
            addon_name = get_addon_name(file)
            if not addon_name or len(addon_name) < 3:
                addon_name = f"Workshop_{item_id}"
                print(f"[WARN] Could not read addon name from GMA, using: {addon_name}", flush=True)
            extract_path = extract_gma(file, item_id)
        else:
            print(f"[X] Unsupported format for {item_id}: {file}", flush=True)
            return
    if not extract_path:
        print(f"[X] Extraction failed for {item_id}", flush=True)
        return
    if install(extract_path, addon_name, item_id):
        print(f"[SUCCESS] Done {item_id}", flush=True)

def process_queue(queue, max_workers=3):
    print("\n[PHASE 2] Processing all items...\n", flush=True)
    with ThreadPoolExecutor(max_workers=max_workers) as executor:
        executor.map(process, queue)
    print("\n[SUCCESS] PHASE 2 COMPLETE\n", flush=True)

def main():
    try:
        if not check_dependencies():
            print("DEPENDENCY CHECK FAILED", flush=True)
            return
        args = sys.argv[1:]
        max_workers = 3
        if len(args) >= 2 and args[0] == "--max-workers":
            max_workers = int(args[1])
            args = args[2:]
        queue = args
        print("QUEUE:", queue, flush=True)
        if not queue:
            print("[X] EMPTY QUEUE", flush=True)
            return
        print("START DOWNLOAD PHASE", flush=True)
        clean_queue = [q.lstrip("!") for q in queue]
        download_queue(clean_queue)
        process_queue(queue, max_workers)
        print("DONE", flush=True)
    except Exception as e:
        print("FATAL ERROR:", e, flush=True)

if __name__ == "__main__":
    main()