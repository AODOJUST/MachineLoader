import io, sys

SRC = r"D:\豆包的下载\Machine_Dev\src\Mods\MachineAAM.cs"

def read(p):
    return io.open(p, "r", encoding="utf-8").read()

def splice(text, start_anchor, end_anchor, replacement, label):
    i = text.find(start_anchor)
    j = text.find(end_anchor)
    if i < 0 or j < 0 or j <= i:
        print("ANCHOR FAIL %s i=%d j=%d" % (label, i, j))
        return None
    return text[:i] + replacement + text[j:]

text = read(SRC)
frag_src = read(r"D:\豆包的下载\Machine_Dev\tools\_frag_src.cs")
frag_trail = read(r"D:\豆包的下载\Machine_Dev\tools\_frag_trail.cs")

# --- region 2 first (later in file, do it first so earlier offsets stay valid) ---
text2 = splice(
    text,
    "        private static GameObject _cachedSmokePrefab;",
    "        private static void BuildSmokeTrail(GameObject root, Transform anchor)",
    frag_src,
    "smoke-source",
)
if text2 is None:
    sys.exit(1)

# --- region 1: AddTrail ---
text3 = splice(
    text2,
    "        private static void AddTrail(GameObject root, Transform dummy)",
    "        /// <summary>把渐变颜色灌进 TrailerRender（head 在 t=0，tail 在 t=1）。</summary>".replace("TrailerRender", "TrailRenderer"),
    frag_trail,
    "AddTrail",
)
if text3 is None:
    sys.exit(1)

io.open(SRC, "w", encoding="utf-8", newline="").write(text3)
print("spliced ok, lines =", text3.count("\n") + 1)
