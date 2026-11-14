"""The attribute vault name hash: Bob Jenkins' lookup2 with seed 0xabcdef00, over the original-case text.

usage: python -I vault_hash.py <text> [<text> ...]
Verified against the DepN chunk of a vault (the hash of "<number>.vlt") and against type names in gcschema.vlt.
"""
import struct, sys

M = 0xFFFFFFFF


def _mix(a, b, c):
    a = (a - b - c) & M; a ^= c >> 13
    b = (b - c - a) & M; b ^= (a << 8) & M
    c = (c - a - b) & M; c ^= b >> 13
    a = (a - b - c) & M; a ^= c >> 12
    b = (b - c - a) & M; b ^= (a << 16) & M
    c = (c - a - b) & M; c ^= b >> 5
    a = (a - b - c) & M; a ^= c >> 3
    b = (b - c - a) & M; b ^= (a << 10) & M
    c = (c - a - b) & M; c ^= b >> 15
    return a, b, c


def lookup2(data: bytes, seed: int = 0xabcdef00) -> int:
    a = b = 0x9e3779b9
    c = seed & M
    k, n = 0, len(data)
    while n >= 12:
        a = (a + struct.unpack_from("<I", data, k)[0]) & M
        b = (b + struct.unpack_from("<I", data, k + 4)[0]) & M
        c = (c + struct.unpack_from("<I", data, k + 8)[0]) & M
        a, b, c = _mix(a, b, c)
        k += 12
        n -= 12
    c = (c + len(data)) & M
    t = data[k:] + b"\x00" * 12
    if n >= 1: a = (a + t[0]) & M
    if n >= 2: a = (a + (t[1] << 8)) & M
    if n >= 3: a = (a + (t[2] << 16)) & M
    if n >= 4: a = (a + (t[3] << 24)) & M
    if n >= 5: b = (b + t[4]) & M
    if n >= 6: b = (b + (t[5] << 8)) & M
    if n >= 7: b = (b + (t[6] << 16)) & M
    if n >= 8: b = (b + (t[7] << 24)) & M
    if n >= 9: c = (c + (t[8] << 8)) & M
    if n >= 10: c = (c + (t[9] << 16)) & M
    if n >= 11: c = (c + (t[10] << 24)) & M
    return _mix(a, b, c)[2]


if __name__ == "__main__":
    for text in sys.argv[1:]:
        print(f"{lookup2(text.encode()):#010x}  {text}")
