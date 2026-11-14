"""Survey a folder of extracted .rsf shader files: GPU binary counts, passes, render states, constant buffers, samplers.

usage: python -I rsf_survey.py <folder of .rsf files>
Extract the files first with:  blueline extract <game> nhl/data/kettle/shaders/<name>.rsf <folder>/<name>.rsf
"""
import collections, os, re, sys

folder = sys.argv[1]
rx = re.compile(rb"[\x20-\x7e]{3,}")
info = {}
for fn in sorted(os.listdir(folder)):
    if not fn.endswith(".rsf"):
        continue
    d = open(os.path.join(folder, fn), "rb").read()
    first = d.find(b"OrbShdr")
    meta = d[: first if first > 0 else len(d)]
    info[fn] = (d.count(b"OrbShdr"), list(dict.fromkeys(m.group().decode() for m in rx.finditer(meta))))


def kind(s):
    if re.match(r"^rna[A-Z]", s): return "pass"
    if re.match(r"^(rs|bs|ds)[A-Z]", s): return "state"
    if re.match(r"^sampler", s): return "sampler"
    if re.match(r"^(Technique\d+|TECH_)", s): return "technique"
    if s.endswith(("CBuffer", "Params")) or s in ("$Globals", "GlobalParams"): return "constant buffer"
    return None


cat = collections.defaultdict(collections.Counter)
for fn, (orb, strings) in info.items():
    for s in strings:
        k = kind(s)
        if k:
            cat[k][s] += 1
print(len(info), "files,", sum(v[0] for v in info.values()), "GPU binaries")
for k, c in cat.items():
    print(f"\n{k} ({len(c)}):", "; ".join(f"{s} x{n}" for s, n in c.most_common(40)))
