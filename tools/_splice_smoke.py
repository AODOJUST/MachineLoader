import io, sys

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"
BLOCK = r"D:\豆包的下载\Machine_Dev\tools\_smoke_block.cs"

with io.open(SRC, "r", encoding="utf-8") as f:
    lines = f.readlines()

start_marker = "尾部持续喷出的烟雾/火星粒子"
end_marker = "[AAM] exhaust fallback particles playing"

start = None
end = None
for i, ln in enumerate(lines):
    if start is None and start_marker in ln:
        start = i
    if end_marker in ln and start is not None:
        end = i
        break

if start is None or end is None:
    print("MARKER NOT FOUND start=%s end=%s" % (start, end))
    sys.exit(1)

print("replacing lines %d..%d" % (start + 1, end + 1))
print("START:", lines[start].rstrip())
print("END  :", lines[end].rstrip())

with io.open(BLOCK, "r", encoding="utf-8") as f:
    block = f.read()
if not block.endswith("\n"):
    block += "\n"

new = lines[:start] + [block] + lines[end + 1:]
with io.open(SRC, "w", encoding="utf-8", newline="") as f:
    f.write("".join(new))
print("done, new line count =", len("".join(new).splitlines()))
