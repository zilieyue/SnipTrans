"""打包一份可直接上传到 GitHub 的开源快照（排除构建中间产物）。"""
import os
import zipfile

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(os.path.dirname(ROOT), "SnipTrans-open-source.zip")

EXCLUDE_DIRS = {"bin", "obj", "dist", ".vs", ".git"}
EXCLUDE_FILES = {".DS_Store"}

count = 0
with zipfile.ZipFile(OUT, "w", zipfile.ZIP_DEFLATED) as z:
    for dirpath, dirnames, filenames in os.walk(ROOT):
        dirnames[:] = sorted(d for d in dirnames if d not in EXCLUDE_DIRS)
        for name in sorted(filenames):
            if name in EXCLUDE_FILES:
                continue
            full = os.path.join(dirpath, name)
            rel = os.path.relpath(full, ROOT)
            z.write(full, os.path.join("SnipTrans", rel))
            count += 1

size = os.path.getsize(OUT)
print(f"packed {count} files -> {OUT}")
print(f"size: {size/1024/1024:.1f} MB")
