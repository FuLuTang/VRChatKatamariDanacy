#!/usr/bin/env python3
"""Build the linked runtime evaluator, simulate and render the reference attachment test."""
from __future__ import annotations
import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[1]
sys.path.insert(0,str(ROOT))
from Tools.attach_sim.render import LEVEL, render_all, solve_touch_stops, axes


def source_hashes():
    sources={p for p in HERE.iterdir() if p.suffix in ('.cs','.csproj','.py','.js','.html')}
    project=ET.parse(HERE/'attach_sim.csproj')
    for node in project.findall('.//Compile'):
        pattern=node.attrib['Include'].replace('$(RuntimeRoot)','../../VRChatKatamariDanacyUnity/Assets/VRC_Katamari/Runtime')
        sources.update(path.resolve() for path in HERE.glob(pattern))
    return {str(p.relative_to(ROOT)):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(sources)}


def distribution(values):
    ordered=sorted(values)
    if not ordered:return {'count':0}
    def percentile(p):
        x=(len(ordered)-1)*p;i=int(x);j=min(i+1,len(ordered)-1)
        return ordered[i]+(ordered[j]-ordered[i])*(x-i)
    return {'count':len(values),'min':ordered[0],'p50':percentile(.5),'p90':percentile(.9),
            'max':ordered[-1],'mean':sum(values)/len(values)}


def summarize(document,rendered=None):
    events=[e for r in document['runs'] for e in r['events']]
    summary={'absorptionCount':len(events),'attachmentGapMm':distribution([e['attachmentGapMm'] for e in events]),
             'worldToLocalGapDeltaMm':distribution([abs(e['gapDeltaMm']) for e in events]),
             'gapsAbovePointTwoMm':sum(e['attachmentGapMm']>.2 for e in events),
             'proxyAccepted':sum(e['proxyAccepted'] for e in events),
             'proxySkips':{str(k):sum(e['proxySkip']==k for e in events) for k in sorted({e['proxySkip'] for e in events})},
             'diameterM':[]}
    for n in sorted({s['n'] for r in document['runs'] for s in r['snapshots']}):
        snapshots=[s for r in document['runs'] for s in r['snapshots'] if s['n']==n]
        summary['diameterM'].append({'n':n,**distribution([s['diameterM'] for s in snapshots])})
    if rendered:
        summary['visibility']=rendered['visibilitySummary']
        summary['overlap']=[]
        for n in sorted({s['n'] for r in rendered['runs'] for s in r['snapshots']}):
            snapshots=[s for r in rendered['runs'] for s in r['snapshots'] if s['n']==n]
            overlap=[s['overlap'] for s in snapshots]
            summary['overlap'].append({'n':n,'obbPairsUpperBound':distribution([s['overlapPairs'] for s in overlap]),
                'obbAffectedItemsUpperBound':distribution([s['affectedItems'] for s in overlap]),
                'meshSurfaceIntersectPairs':distribution([s['meshGeometry']['surfaceIntersectPairs'] for s in snapshots]),
                'meshAffectedItems':distribution([len({i for pair in s['meshGeometry']['pairs'] for i in pair}) for s in snapshots]),
                'testedPairsPerSeed':n*(n-1)//2,
                'maxObbSatDepthMm':max(s['maxSatDepthMm'] for s in overlap)})
        items=[i for r in rendered['runs'] for i in r['snapshots'][-1]['meshGeometry']['items']]
        meshGaps=[i['meshContactGapMm'] for i in items if i['meshContactGapMm'] is not None]
        summary['meshContactGapMm']=distribution(meshGaps)
        core_gaps=[1000*(i['meshDistanceM']-run['startDiameterM']/2) for run,rr_ in zip(document['runs'],rendered['runs'])
                   for i in rr_['snapshots'][-1]['meshGeometry']['items']]
        summary['meshToFixedCoreSurfaceGapMm']=distribution(core_gaps)
        summary['coreSurfaceToCollisionSphereMm']=[{'n':n,**distribution([1000*(sn['diameterM']-run['startDiameterM'])/2
            for run in document['runs'] for sn in run['snapshots'] if sn['n']==n])} for n in sorted({sn['n'] for sn in document['runs'][0]['snapshots']})]
        summary['meshContactGapUnmeasurable']=len(items)-len(meshGaps)
        summary['meshContactGapsAbovePointTwoMm']=sum(x>.2 for x in meshGaps)
    return summary


# name -> (incoming-contact rule, displayed core). Only baseline is the shipped rule; the rest are hypothetical controls.
# contact: ball = collision sphere (runtime gate); mesh = sphere or attached triangle meshes; boxes = sphere or attached boxes;
# embed-NN = sphere contact point pulled inward by NN% of the prop's radial box extent of the prop's radial box extent.
VARIANTS={'baseline':('ball','fixed'),'core-grows':('ball','grow'),
          'touch-mesh':('mesh','fixed'),'touch-mesh+core-grows':('mesh','grow'),
          'touch-boxes':('boxes','fixed'),'embed-30':('embed-30','fixed'),'embed-45':('embed-45','fixed'),'embed-60':('embed-60','fixed')}
EMBED_FRACTIONS={'embed-30':.3,'embed-45':.45,'embed-60':.6}  # original footage: ~30-60% of the prop's size sunk into the core (weak evidence, see ATTACH_PLACEMENT)
FACTORIAL=('baseline','core-grows','touch-mesh','touch-mesh+core-grows')


def controls(variants):
    """Per-N visibility of each variant and the paired differences against the baseline."""
    base=variants.get('baseline')
    if not base or 'visibility' not in base:return None
    rows=[]
    for entry in base['visibility']:
        n=entry['n'];row={'n':n,'visible':{}}
        for name,summary in variants.items():
            match=[v for v in summary['visibility'] if v['n']==n]
            if match:row['visible'][name]=match[0]['mean']
        v=row['visible']
        if 'touch-boxes' in v:row['gainTouchBoxes']=v['touch-boxes']-v['baseline']
        for k in EMBED_FRACTIONS:
            if k in v:row['gain'+k.title().replace('-','')]=v[k]-v['baseline']
        if all(k in v for k in FACTORIAL):
            row['hiddenShareOfBaseline']=1-v['baseline']
            row['gainCoreGrows']=v['core-grows']-v['baseline']
            row['gainTouchAttached']=v['touch-mesh']-v['baseline']
            row['gainBoth']=v['touch-mesh+core-grows']-v['baseline']
            row['interaction']=row['gainBoth']-row['gainCoreGrows']-row['gainTouchAttached']
        rows.append(row)
    return rows


def embed_stops(document,fraction):
    stops={}
    for run in document['runs']:
        values=[]
        for item,event in zip(run['snapshots'][-1]['items'],run['events']):
            d=event['direction']
            extent=sum(abs(sum(a*b for a,b in zip(d,axis)))*size for axis,size in zip(axes(item['rotationQuat']),item['aabb']))
            values.append(max(0.0,event['stopDistanceM']-fraction*extent))
        stops[str(run['seed'])]=values
    return stops


def contact_summary(document,detail=None):
    events=[e for r in document['runs'] for e in r['events']]
    stopped=sum(row['stoppedByAttached'] for rows in detail.values() for row in rows) if detail else None
    return {'itemsStoppedByAttachedItemsOnly':stopped,'items':len(events),
            'ballSurfaceStandoffMm':distribution([e['attachmentGapMm'] for e in events]),
            'stopDistanceM':distribution([e['stopDistanceM'] for e in events])}


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--level',type=Path,default=LEVEL)
    parser.add_argument('--seeds',default='1,2,3,4,5')
    parser.add_argument('--out',type=Path)
    parser.add_argument('--variants',default=','.join(VARIANTS),help='comma list of: '+', '.join(VARIANTS))
    parser.add_argument('--ns',default='10,20,30,40',help='absorb counts at which to snapshot and render')
    parser.add_argument('--simulate-only',action='store_true')
    args=parser.parse_args()
    chosen=args.variants.split(',')
    unknown=[v for v in chosen if v not in VARIANTS]
    if unknown:parser.error(f'unknown variants {unknown}')
    out=(args.out or Path.home()/'KatamariRef/viewer_evidence/attach_sim'/datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')).expanduser().resolve()
    if out.is_relative_to(ROOT):parser.error('reference output must stay outside repository')
    refs=Path(os.environ.get('VRCK_CSHARP_CHECK_CACHE',Path.home()/'.cache/vrc-katamari-refasm'))/'refs'
    if not (refs/'UnityEngine.CoreModule.dll').is_file():parser.error('cached Unity managed references missing; this tool never uses SSH')
    dotnet=Path(os.environ.get('VRCK_DOTNET_ROOT',Path.home()/'.dotnet'))/'dotnet'
    out.mkdir(parents=True,exist_ok=True)
    print(f'OUTPUT {out}',flush=True)
    subprocess.run([str(dotnet),'build',str(HERE/'attach_sim.csproj'),'-c','Release','--nologo',f'-p:UnityRefs={refs}'],check=True,cwd=ROOT)
    documents={}
    contacts=sorted({VARIANTS[v][0] for v in chosen}|{'ball'},key=lambda c:(['ball','mesh','boxes'].index(c) if c in('ball','mesh','boxes') else 3,c))
    details={}
    for contact in contacts:
        path=out/{'ball':'simulation.json','mesh':'simulation_touch_mesh.json','boxes':'simulation_touch_boxes.json'}.get(contact,f'simulation_{contact}.json')
        extra=[f'--ns={args.ns}']
        if contact=='boxes':extra+=['--touch-attached']
        if contact=='mesh':
            stops,detail=solve_touch_stops(documents['ball'],out,args.level)
            (out/'touch_mesh_stops.json').write_text(json.dumps(stops)+'\n')
            (out/'touch_mesh_stops_detail.json').write_text(json.dumps(detail,indent=1)+'\n')
            extra+=[f'--stops={out/"touch_mesh_stops.json"}'];details[contact]=detail
        if contact in EMBED_FRACTIONS:
            (out/f'{contact}_stops.json').write_text(json.dumps(embed_stops(documents['ball'],EMBED_FRACTIONS[contact]))+'\n')
            extra+=[f'--stops={out/(contact+"_stops.json")}']
        subprocess.run([str(dotnet),str(HERE/'bin/Release/net8.0/attach_sim.dll'),str(args.level),str(path),args.seeds,*extra],check=True,cwd=ROOT)
        doc=json.loads(path.read_text())
        doc['sourceSha256']=source_hashes();doc['levelSha256']=hashlib.sha256(args.level.read_bytes()).hexdigest()
        path.write_text(json.dumps(doc,indent=2)+'\n');documents[contact]=doc
    summaries={}
    for name in chosen:
        contact,core=VARIANTS[name]
        rendered=None if args.simulate_only else render_all(documents[contact],out/name,args.level,core)
        summaries[name]={'contact':contact,'core':core,**summarize(documents[contact],rendered)}
        if contact!='ball':summaries[name]['attachedContactStops']=contact_summary(documents[contact],details.get(contact))
    summary={'variants':summaries,'controls':controls(summaries)}
    (out/'summary.json').write_text(json.dumps(summary,indent=2)+'\n')
    print(json.dumps(summary['controls'],indent=2))
    print(f'WROTE {out/"summary.json"}')

if __name__=='__main__':main()
