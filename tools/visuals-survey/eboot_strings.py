"""Map the ELF program headers of eboot.bin and write every printable string (6+ characters) with its file offset.

usage: python -I eboot_strings.py <eboot.bin> <output folder>
"""
import os, re, struct, sys

eboot, out = sys.argv[1], sys.argv[2]
os.makedirs(out, exist_ok=True)
data = open(eboot, "rb").read()
(_, etype, mach, _, entry, phoff, _, _, _, phentsize, phnum, *_rest) = struct.unpack_from("<16sHHIQQQIHHHHHH", data, 0)
print("size", len(data), "type", hex(etype), "machine", hex(mach), "entry", hex(entry), "segments", phnum)
names = {1: "LOAD", 2: "DYNAMIC", 3: "INTERP", 4: "NOTE", 7: "TLS", 0x6474e550: "EH_FRAME", 0x61000000: "SCE_DYNLIBDATA",
         0x61000001: "SCE_PROCPARAM", 0x61000010: "SCE_RELRO", 0x6fffff00: "SCE_COMMENT", 0x6fffff01: "SCE_VERSION"}
for i in range(phnum):
    t, fl, off, va, _, fsz, msz, _ = struct.unpack_from("<IIQQQQQQ", data, phoff + i * phentsize)
    print(f"{i:2} {names.get(t, hex(t)):16} flags={fl} off={off:#x} vaddr={va:#x} filesz={fsz:#x} memsz={msz:#x}")
n = 0
with open(os.path.join(out, "eboot_strings.txt"), "w", encoding="utf-8", errors="replace") as f:
    for m in re.finditer(rb"[\x20-\x7e]{6,}", data):
        f.write(f"{m.start():08x} {m.group().decode('ascii')}\n")
        n += 1
print("strings", n)
