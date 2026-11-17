#!/usr/bin/env python3
"""Offline-only volume-recovery controls; original MAS1 knots, explicitly hypothetical mappings."""
from __future__ import annotations
import argparse
import base64
import csv
import hashlib
from datetime import datetime, timezone
import json
from pathlib import Path
import subprocess
import sys

ROOT=Path(__file__).resolve().parents[2]
sys.path.insert(0,str(ROOT))
from Tools.attach_sim.run import source_hashes, summarize, distribution
from Tools.attach_sim.render import LEVEL, render_all, span_for, open_viewer, png_bytes

MAS1_DIAMETERS=(.05,.10,.15,.20)
MAS1_RATIOS=(.8,.8,.4,.16)

def curve_for(name,start):
    if name=='baseline':return None
    scaled=name!='mas1-absolute'
    factor=start/.05 if scaled else 1
    ratio_factor=.5 if name=='mas1-half-normalized' else 1
    if name not in ('mas1-normalized','mas1-half-normalized','mas1-absolute'):
        raise ValueError(name)
    return {'diameterM':[x*factor for x in MAS1_DIAMETERS],
            'ratio':[x*ratio_factor for x in MAS1_RATIOS],
            'evidence':'MAS1 knots: ~/KatamariRef/meta/raw_growth_mechanism.txt:31; not Cancer measurements',
            'mapping':'start-diameter normalized (hypothesis)' if scaled else 'absolute MAS1 diameter (transfer hypothesis)',
            'ratioFactor':ratio_factor,'extrapolation':'endpoint clamp, experiment assumption',
            'composition':'baked growthMm3 * ratio(pre-pickup diameter) / 0.8; retains relative per-item penalty'}

def floating_summary(document,rendered,threshold_mm=10):
    """Geometry-only disconnected-fragment proxy, not a perceptual score or mesh penetration depth."""
    rows=[]
    starts={r['seed']:r['startDiameterM'] for r in document['runs']}
    for n in sorted({s['n'] for r in rendered['runs'] for s in r['snapshots']}):
        isolated=[];gaps=[];seed_rows=[]
        for run in rendered['runs']:
            snap=next(s for s in run['snapshots'] if s['n']==n)
            geometry=snap['meshGeometry']
            linked={i for pair in geometry['pairs'] for i in pair}
            local_gaps=[max(0,1000*(i['meshDistanceM']-starts[run['seed']]/2)) for i in geometry['items']]
            count=sum(i['itemId'] not in linked and gap>threshold_mm for i,gap in zip(geometry['items'],local_gaps))
            seed_rows.append({'seed':run['seed'],'isolatedItems':count,'isolatedFraction':count/n})
            isolated.append(count/n);gaps.extend(local_gaps)
        rows.append({'n':n,'thresholdMm':threshold_mm,'isolatedFraction':distribution(isolated),
                     'meshToCoreSpherePositiveGapMm':distribution(gaps),'seeds':seed_rows})
    return rows

def overviews(out,name,document,level_path=LEVEL):
    # Retry only this recovery-control contact-sheet browser's pre-connection port-file race.
    for attempt in range(3):
        try:
            return _overviews(out,name,document,level_path)
        except IndexError:
            if attempt==2:raise

def _overviews(out,name,document,level_path):
    # Canvas composition reuses the existing headless renderer; no extra image dependency.
    with open_viewer(out/name/'overview-browser',level_path) as (chrome,_):
        for n in (30,60):
            if n not in [s['n'] for s in document['runs'][0]['snapshots']]:continue
            rows=[]
            for run in document['runs']:
                image=out/name/f'seed_{run["seed"]}'/f'n{n}_six_views.png'
                rows.append({'seed':run['seed'],'image':'data:image/png;base64,'+base64.b64encode(image.read_bytes()).decode()})
            expression="""(async(rows)=>{const c=document.createElement('canvas');c.width=1536;c.height=3*580;const x=c.getContext('2d');x.fillStyle='#111925';x.fillRect(0,0,c.width,c.height);for(let i=0;i<rows.length;i++){let im=new Image();im.src=rows[i].image;await im.decode();let px=(i%2)*768,py=Math.floor(i/2)*580;x.drawImage(im,px,py,768,544);x.fillStyle='#fff';x.font='24px sans-serif';x.fillText('seed='+rows[i].seed,px+10,py+574)}return c.toDataURL('image/png')})(ROWS)""".replace('ROWS',json.dumps(rows))
            (out/name/f'overview_n{n}.png').write_bytes(png_bytes(chrome.cdp.evaluate(expression,await_promise=True,timeout=120)))

def main():
    p=argparse.ArgumentParser(description=__doc__)
    p.add_argument('--out',type=Path)
    p.add_argument('--level',type=Path,default=LEVEL)
    p.add_argument('--ns',default='10,20,30,40,60,80')
    p.add_argument('--simulate-only',action='store_true')
    p.add_argument('--resume',action='store_true',help='reuse matching saved simulations and completed render reports after a failed postprocess')
    args=p.parse_args()
    out=(args.out or Path.home()/'KatamariRef/viewer_evidence/attach_sim'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')).expanduser().resolve()
    if out.is_relative_to(ROOT):p.error('output must stay outside repository')
    out.mkdir(parents=True,exist_ok=True)
    dotnet=Path.home()/'.dotnet/dotnet'
    subprocess.run([str(dotnet),'build','Tools/attach_sim','-c','Release','--nologo'],cwd=ROOT,check=True)
    start=json.loads(args.level.read_text())['meta']['ball']['startDiameterM']
    names=('mas1-absolute','baseline','mas1-normalized','mas1-half-normalized')
    documents={}
    for name in names:
        curve=curve_for(name,start);extra=[]
        if curve:
            path=out/f'{name}_curve.json';path.write_text(json.dumps(curve,indent=2)+'\n')
            extra.append(f'--growth-curve={path}')
        if name!='mas1-absolute':extra.append(f'--draws={out/"simulation_mas1-absolute.json"}')
        path=out/f'simulation_{name}.json'
        if not (args.resume and path.is_file()):
            subprocess.run([str(dotnet),str(ROOT/'Tools/attach_sim/bin/Release/net8.0/attach_sim.dll'),str(args.level),str(path),'1,2,3,4,5',f'--ns={args.ns}',*extra],cwd=ROOT,check=True)
        doc=json.loads(path.read_text())
        if args.resume and doc.get('sourceSha256'):
            current=source_hashes()
            assert all(current.get(k)==v for k,v in doc['sourceSha256'].items() if k.endswith('.cs')), 'C# changed; do not reuse simulations'
            assert doc.get('growthControl')==curve, 'curve changed; do not reuse simulations'
            assert [s['n'] for s in doc['runs'][0]['snapshots']]==sorted(map(int,args.ns.split(',')))
        doc['growthControl']=curve;doc['sourceSha256']=source_hashes()
        level_hash=hashlib.sha256(args.level.read_bytes()).hexdigest()
        if args.resume and doc.get('levelSha256'):assert doc['levelSha256']==level_hash, 'level changed'
        doc['levelSha256']=level_hash
        doc['pairedDraws']='mas1-absolute runtime-eligible stream; identical items, directions and orientations in all groups'
        path.write_text(json.dumps(doc,indent=2)+'\n');documents[name]=doc
    # Verify the causal comparison really has identical objects and random orientations.
    for doc in documents.values():
        for base,run in zip(documents['mas1-absolute']['runs'],doc['runs']):
            assert [e['itemId'] for e in base['events']]==[e['itemId'] for e in run['events']]
            assert [e['direction'] for e in base['events']]==[e['direction'] for e in run['events']]
            assert [i['rotationQuat'] for i in base['snapshots'][-1]['items']]==[i['rotationQuat'] for i in run['snapshots'][-1]['items']]
    spans={seed:max(span_for(r['snapshots'][-1]['items'],r['startDiameterM']) for doc in documents.values() for r in doc['runs'] if r['seed']==seed) for seed in range(1,6)}
    summaries={}
    for name in ('baseline','mas1-normalized','mas1-half-normalized','mas1-absolute'):
        doc=documents[name]
        report_path=out/name/'render_report.json'
        rendered=None if args.simulate_only else (json.loads(report_path.read_text()) if args.resume and report_path.is_file()
            else render_all(doc,out/name,args.level,span_overrides=spans))
        summaries[name]=summarize(doc,rendered)
        if rendered:
            summaries[name]['floating']=floating_summary(doc,rendered)
            overviews(out,name,doc,args.level)
    result={'variants':summaries,'comparison':'same currently-eligible low-growth trajectory in all groups; no claim to reproduce human route or unlocked-prop resampling','curves':{name:curve_for(name,start) for name in names},'spanM':spans}
    (out/'recovery_summary.json').write_text(json.dumps(result,indent=2)+'\n')
    with (out/'recovery_metrics.csv').open('w',newline='') as stream:
        writer=csv.writer(stream)
        writer.writerow(['variant','N','diameter_mean_m','diameter_min_m','diameter_max_m',
                         'core_visible_pct','mesh_intersect_pairs_mean','pair_denominator_per_seed',
                         'isolated_pct','core_gap_p50_mm','core_gap_p90_mm'])
        for name,summary in summaries.items():
            if 'visibility' not in summary:continue
            for d,v,o,f in zip(summary['diameterM'],summary['visibility'],summary['overlap'],summary['floating']):
                writer.writerow([name,d['n'],d['mean'],d['min'],d['max'],100*v['mean'],
                    o['meshSurfaceIntersectPairs']['mean'],o['testedPairsPerSeed'],
                    100*f['isolatedFraction']['mean'],f['meshToCoreSpherePositiveGapMm']['p50'],f['meshToCoreSpherePositiveGapMm']['p90']])
    print(f'WROTE {out/"recovery_summary.json"}',flush=True)

if __name__=='__main__':main()
