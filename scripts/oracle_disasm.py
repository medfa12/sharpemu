#!/usr/bin/env python3
# oracle_disasm.py <NID> [<NID> ...] : disassemble the real PS5 firmware implementation of each NID.
import sys, struct, glob, os
try: import capstone
except: capstone=None
IDX="/Users/gera/Desktop/sharpemu/scripts/oracle_nids.tsv"
ORACLE="/Users/gera/Desktop/sharpemu/games/ps5-403-oracle/filesystems/merged"

def find(nid):
    for line in open(IDX):
        p=line.rstrip('\n').split('\t')
        if p[0]==nid: return p  # nid,name,module,vaddr,size
    return None

def modpath(mod):
    r=glob.glob(ORACLE+f"/**/{mod}",recursive=True)
    return r[0] if r else None

def v2o(f,vaddr):
    e_phoff=struct.unpack('<Q',f[0x20:0x28])[0]; phnum=struct.unpack('<H',f[0x38:0x3a])[0]
    for i in range(phnum):
        off=e_phoff+i*56
        p_type=struct.unpack('<I',f[off:off+4])[0]
        p_offset,p_vaddr=struct.unpack('<QQ',f[off+8:off+24])
        p_filesz=struct.unpack('<Q',f[off+32:off+40])[0]
        if p_type==1 and p_vaddr<=vaddr<p_vaddr+p_filesz: return p_offset+(vaddr-p_vaddr)
    return None

for nid in sys.argv[1:]:
    row=find(nid)
    if not row: print(f"# {nid}: not found in oracle index"); continue
    _,name,mod,vahex,size=row
    va=int(vahex,16); size=min(int(size),4096) if size.isdigit() else 512
    path=modpath(mod)
    print(f"=== {nid}  {name}  {mod}  vaddr={vahex} size={size} ===")
    if not path: print("# module file not found"); continue
    f=open(path,'rb').read(); fo=v2o(f,va)
    if fo is None: print("# vaddr not in a LOAD segment"); continue
    code=f[fo:fo+size]
    if capstone:
        md=capstone.Cs(capstone.CS_ARCH_X86,capstone.CS_MODE_64)
        for ins in md.disasm(code,va):
            print(f"  {ins.address:#08x}: {ins.mnemonic} {ins.op_str}")
            if ins.mnemonic in ('ret','jmp') and ins.address>va+8: break
    else:
        print("# capstone missing; raw bytes:", code[:64].hex())
