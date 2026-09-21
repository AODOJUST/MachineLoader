import struct, sys, string

path = sys.argv[1] if len(sys.argv) > 1 else r"D:\steam\steamapps\workshop\content\2660460\3798428144\PL-15.planedesign"
data = open(path, 'rb').read()
print("size =", len(data))

def rd_str(b, p):
    # observed layout: 0x00 <len> <ascii...>
    n = b[p + 1]
    s = b[p + 2:p + 2 + n].decode('utf-8', 'replace')
    return s, p + 2 + n

def looks_name(b, p):
    if p + 2 > len(b): return None
    if b[p] != 0: return None
    n = b[p + 1]
    if n < 1 or n > 40: return None
    raw = b[p + 2:p + 2 + n]
    if len(raw) < n: return None
    for ch in raw:
        if ch < 0x20 or ch > 0x7e: return None
    return raw.decode('ascii')

p = 0
h1 = struct.unpack_from('<i', data, p)[0]; p += 4
h2 = struct.unpack_from('<f', data, p)[0]; p += 4
print("HDR int=%d float=%s" % (h1, h2))
n1 = struct.unpack_from('<i', data, p)[0]; p += 4
print("nameCount =", n1)
names = []
for _ in range(n1):
    s, p = rd_str(data, p)
    names.append(s)
print("names =", names)
print("pos after names = 0x%x  next i32 = %d" % (p, struct.unpack_from('<i', data, p)[0]))
n2 = struct.unpack_from('<i', data, p)[0]; p += 4
print("nodeCount =", n2)

for i in range(n2):
    nm, p2 = rd_str(data, p)
    # find record length that makes the next bytes look like a node name
    chosen = None
    for nf in range(3, 30):
        q = p2 + 4 * nf
        if looks_name(data, q):
            chosen = (nf, q, looks_name(data, q)); break
    fl = [round(struct.unpack_from('<f', data, p2 + 4 * k)[0], 5) for k in range(chosen[0])] if chosen else []
    print("node[%02d] @0x%04x %-22s floats=%d" % (i, p, nm, chosen[0] if chosen else -1))
    if chosen:
        print("        pos=%s quat=%s scale=%s rest=%s -> next='%s' @0x%x" % (
            fl[0:3], fl[3:7], fl[7:10], fl[10:], chosen[2], chosen[1]))
        p = chosen[1]
    else:
        print("        !! could not align; raw:", data[p2:p2 + 40].hex())
        break
print("EOF pos=0x%x size=0x%x remaining=%d" % (p, len(data), len(data) - p))
if len(data) - p > 0:
    print("tail:", data[p:].hex())
