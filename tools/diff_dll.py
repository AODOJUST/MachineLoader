# -*- coding: utf-8 -*-
"""判断两个 .NET DLL 是"同代码不同构建"还是"真的改了代码"。

背景：本项目用 csc 直接编译，每次都会把新的 COFF 时间戳和 MVID 写进 PE 头
与 #GUID 堆。所以同一份源码两次编译的 SHA-256 必然不同 —— 不能用哈希相等
来判断"是不是同一份代码"。本工具把差异字节映射到 PE 区段，据此区分：

  纯重建   差异只落在 0x88(COFF 时间戳) 与 #GUID 堆(MVID) —— 代码相同
  真改代码 差异落在 .text 的 IL 方法体 / #US / #Strings —— 代码不同

用法：
  python tools/diff_dll.py A.dll B.dll
"""
import struct
import sys


def sections(data):
    pe = struct.unpack_from("<I", data, 0x3C)[0]
    nsec = struct.unpack_from("<H", data, pe + 6)[0]
    opt_size = struct.unpack_from("<H", data, pe + 20)[0]
    base = pe + 24 + opt_size
    out = []
    for i in range(nsec):
        off = base + i * 40
        name = data[off:off + 8].rstrip(b"\0").decode("latin1")
        vsize, vaddr, rsize, raddr = struct.unpack_from("<IIII", data, off + 8)
        out.append((name, raddr, rsize, vaddr, vsize))
    return out


def which(secs, off):
    for name, raddr, rsize, vaddr, _vsize in secs:
        if raddr <= off < raddr + rsize:
            return "%s RVA 0x%X" % (name, vaddr + (off - raddr))
    return "文件头/未映射"


def main(argv):
    if len(argv) != 2:
        print(__doc__)
        return 2
    a_path, b_path = argv
    a = open(a_path, "rb").read()
    b = open(b_path, "rb").read()
    print("A: %s  (%d bytes)" % (a_path, len(a)))
    print("B: %s  (%d bytes)" % (b_path, len(b)))

    if len(a) != len(b):
        print()
        print("=> 体积不同：代码确定不同（或版本不同），无需逐字节比较。")
        return 1

    diffs = [i for i in range(len(a)) if a[i] != b[i]]
    if not diffs:
        print()
        print("=> 字节完全一致。")
        return 0

    secs = sections(a)
    runs, start, prev = [], diffs[0], diffs[0]
    for i in diffs[1:]:
        if i == prev + 1:
            prev = i
            continue
        runs.append((start, prev))
        start = prev = i
    runs.append((start, prev))

    print()
    print("差异 %d 字节 / %d 段：" % (len(diffs), len(runs)))
    code_diffs = 0
    for s, e in runs:
        where = which(secs, s)
        is_header = s < 0x200
        is_mvid = "RVA 0x" in where and False  # MVID 需按实际 #GUID 位置判断
        if not is_header:
            code_diffs += e - s + 1
        print("  0x%06X..0x%06X (%2d)  %-26s | A=%s  B=%s"
              % (s, e, e - s + 1, where, a[s:e + 1].hex(), b[s:e + 1].hex()))

    header = sum(1 for i in diffs if i < 0x200)
    print()
    print("PE 头内差异: %d 字节（COFF 时间戳等）" % header)
    print("PE 头外差异: %d 字节（IL / 元数据堆）" % (len(diffs) - header))
    print()
    if len(diffs) - header <= 32:
        print("=> 判定：几乎肯定是【同代码、不同次构建】。头外差异只有 %d 字节，"
              % (len(diffs) - header))
        print("   通常落在 #GUID 堆(MVID)；请人工确认上面区段没有落在 .text 的 IL 区。")
    else:
        print("=> 判定：头外差异达 %d 字节，很可能【真的改了代码】。"
              % (len(diffs) - header))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
