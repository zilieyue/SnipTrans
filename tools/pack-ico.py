"""Pack icon_*.png into a single multi-size Windows .ico (PNG-compressed entries)."""
import struct
import os

HERE = os.path.dirname(os.path.abspath(__file__))
SIZES = [16, 32, 48, 256]

entries = []
blobs = []
offset = 6 + 16 * len(SIZES)

for s in SIZES:
    with open(os.path.join(HERE, f"icon_{s}.png"), "rb") as f:
        data = f.read()
    # 256 is encoded as 0 in the directory entry
    dim = 0 if s >= 256 else s
    entries.append(struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset))
    offset += len(data)
    blobs.append(data)

out = os.path.join(HERE, "..", "app.ico")
with open(out, "wb") as f:
    f.write(struct.pack("<HHH", 0, 1, len(SIZES)))
    for e in entries:
        f.write(e)
    for b in blobs:
        f.write(b)

print(f"written: {os.path.abspath(out)}  ({os.path.getsize(out)} bytes, {len(SIZES)} sizes)")
