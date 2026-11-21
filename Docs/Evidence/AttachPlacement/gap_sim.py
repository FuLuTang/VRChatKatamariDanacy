import json,math,random,collections,statistics as st
d=json.load(open('/home/fulutang/Documents/Github/VRChatKatamariDanacy/Docs/Design/Levels/_ref_cancer/placement.json'))
props=[p for p in d['props'] if p.get('absorbable')]
random.seed(7)
def gap_for(p,D,dt=0.0):
    r=D/2;h=[x/2 for x in p['aabb']];R=math.sqrt(sum(x*x for x in h));c=p['position']
    ang=random.uniform(0,2*math.pi);dx,dz=math.cos(ang),math.sin(ang)
    by=r  # ball resting on ground y=0
    s=3.0
    while s>0:
        bx=c[0]+dx*s;bz=c[2]+dz*s
        dist=math.sqrt((c[0]-bx)**2+(c[1]-by)**2+(c[2]-bz)**2)
        if dist<=r+R:break
        s-=0.02*random.uniform(0.2,1)   # ~0.04 m per fixed step worst-case at 2 m/s
    else:return None
    qx=min(max(bx-c[0],-h[0]),h[0]);qy=min(max(by-c[1],-h[1]),h[1]);qz=min(max(bz-c[2],-h[2]),h[2])
    return math.sqrt((bx-c[0]-qx)**2+(by-c[1]-qy)**2+(bz-c[2]-qz)**2)-r, R
for D in (0.222,0.28):
    bykind=collections.defaultdict(list);allg=[]
    for p in props:
        if p['absorbDiameterReq']>D:continue
        for _ in range(20):
            g=gap_for(p,D)
            if g: bykind[p['def']].append(g[0]);allg.append(g[0])
    allg.sort();n=len(allg)
    print(f"D={D} n={n} median={allg[n//2]*100:.1f}cm p75={allg[3*n//4]*100:.1f} p90={allg[9*n//10]*100:.1f} max={allg[-1]*100:.1f}  >1cm {sum(g>.01 for g in allg)/n:.0%} >3cm {sum(g>.03 for g in allg)/n:.0%}  >5cm {sum(g>.05 for g in allg)/n:.0%}")
    rows=sorted(((st.median(v),k,len(v)) for k,v in bykind.items() if len(v)>=20),reverse=True)[:8]
    for m,k,nn in rows:
        pp=[q for q in props if q['def']==k][0]
        print(f"   {k:22s} med gap {m*100:5.1f}cm  aabb={pp['aabb']}")
