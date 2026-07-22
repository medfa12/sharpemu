#!/usr/bin/env python3
# Index every exported NID across the decrypted PS5 firmware (games/ps5-403-oracle).
# Output: NID<TAB>name<TAB>module<TAB>vaddr  -> oracle_nids.tsv
import struct, os, sys, hashlib, glob

ORACLE = "/Users/gera/Desktop/sharpemu/games/ps5-403-oracle/filesystems/merged"
OUT = "/Users/gera/Desktop/sharpemu/scripts/oracle_nids.tsv"

# NID name maps: build NID->symbolName from aerolib + ps5_names
def load_names():
    m = {}
    salt = bytes.fromhex('518d64a635ded8c1e6b039b1c3e55230')
    cs = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+-"
    def nid(n):
        v = struct.unpack('<Q', hashlib.sha1(n.encode()+salt).digest()[:8])[0]
        return ''.join(cs[(v>>(58-6*i))&0x3F] for i in range(10)) + cs[(v&0xF)<<2]
    for p in ["/Users/gera/Desktop/sharpemu/scripts/ps5_names.txt"]:
        if os.path.exists(p):
            for line in open(p, errors='ignore'):
                s=line.strip()
                if s and ' ' not in s and len(s)<200:
                    m[nid(s)] = s
    inl="/Users/gera/Desktop/sharpemu/inspiration/shadPS4/scripts/aerolib.inl"
    if os.path.exists(inl):
        import re
        for mo in re.finditer(r'"([A-Za-z0-9+\-]{11})"\s*,\s*"([^"]+)"', open(inl,errors='ignore').read()):
            m.setdefault(mo.group(1), mo.group(2))
    return m

def seg_map(f, e_phoff, phnum):
    segs=[]
    for i in range(phnum):
        off=e_phoff+i*56
        p_type=struct.unpack('<I',f[off:off+4])[0]
        p_offset,p_vaddr=struct.unpack('<QQ',f[off+8:off+24])
        p_filesz=struct.unpack('<Q',f[off+32:off+40])[0]
        if p_type==1: segs.append((p_vaddr,p_offset,p_filesz))
    def v2o(v):
        for vaddr,off,sz in segs:
            if vaddr<=v<vaddr+sz: return off+(v-vaddr)
        return None
    return v2o

def parse_module(path):
    try: f=open(path,'rb').read()
    except: return []
    if f[:4]!=b'\x7fELF': return []
    e_phoff=struct.unpack('<Q',f[0x20:0x28])[0]
    phnum=struct.unpack('<H',f[0x38:0x3a])[0]
    # find DYNAMIC
    dyn=None
    for i in range(phnum):
        off=e_phoff+i*56
        p_type=struct.unpack('<I',f[off:off+4])[0]
        p_offset=struct.unpack('<Q',f[off+8:off+16])[0]
        p_filesz=struct.unpack('<Q',f[off+32:off+40])[0]
        if p_type in (2,0x61000000): dyn=(p_offset,p_filesz)
    if not dyn: return []
    d={}
    for i in range(dyn[1]//16):
        tag,val=struct.unpack('<qQ',f[dyn[0]+i*16:dyn[0]+i*16+16])
        d.setdefault(tag,[]).append(val)
    g=lambda t:(d.get(t) or [None])[0]
    symtab,strtab=g(6),g(5)
    if not symtab or not strtab: return []
    v2o=seg_map(f,e_phoff,phnum)
    so,to=v2o(symtab),v2o(strtab)
    if so is None or to is None: return []
    out=[]
    for i in range(20000):
        e=f[so+i*24:so+i*24+24]
        if len(e)<24: break
        st_name,st_info,st_other,st_shndx,st_value,st_size=struct.unpack('<IBBHQQ',e)
        if st_name==0: continue
        try: nend=f.index(b'\0',to+st_name); nm=f[to+st_name:nend].decode('latin1')
        except: continue
        # exported func: NID#lib#module, defined (value!=0), STT_FUNC
        if '#' in nm and st_value!=0 and (st_info&0xf)==2:
            out.append((nm.split('#')[0], st_value, st_size))
    return out

def main():
    names=load_names()
    rows=[]
    mods=glob.glob(ORACLE+"/**/*.sprx",recursive=True)+glob.glob(ORACLE+"/**/*.elf",recursive=True)+glob.glob(ORACLE+"/**/*.prx",recursive=True)
    for path in mods:
        modname=os.path.basename(path)
        for nid,vaddr,size in parse_module(path):
            rows.append((nid, names.get(nid,'?'), modname, hex(vaddr), size))
    rows.sort(key=lambda r:(r[2],r[1]))
    with open(OUT,'w') as w:
        for nid,nm,mod,va,sz in rows:
            w.write(f"{nid}\t{nm}\t{mod}\t{va}\t{sz}\n")
    print(f"modules scanned: {len(mods)}")
    print(f"exported NIDs indexed: {len(rows)}")
    print(f"named: {sum(1 for r in rows if r[1]!='?')}")
    print(f"-> {OUT}")

main()
