#!/usr/bin/env python3
"""Render runtime-produced attachment poses through the existing level-editor viewer."""
from __future__ import annotations
import argparse
import contextlib
import base64
import hashlib
import json
import math
import mimetypes
from pathlib import Path
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT))
from Tools.levelbuild import ref_models
from Tools.leveleditor.shot_cdp import Chrome

HERE = Path(__file__).resolve().parent
EDITOR = ROOT / 'Tools/leveleditor/web'
LEVEL = ROOT / 'Docs/Design/Levels/_ref_cancer/placement.v2.json'
CORE = Path.home() / 'KatamariRef/models/KatamariCore/original/core_01.obj'


def load_assets(level_path=LEVEL):
    path = Path(level_path)
    doc = json.loads(path.read_text())
    table = ref_models.load_model_table(path.parent)
    model_root = ref_models.normalized_models_dir()
    assets = {}
    for entry in doc['defs']:
        result = ref_models.resolve_model(entry['id'], entry.get('model'), table, model_root)
        if result and result['kind'] == 'glb':
            assets[entry['id']] = Path(result['path'])
    return doc, assets


def core_geometry(path=CORE):
    """Read original OBJ triangles/UVs; no rendering or gameplay in this loader."""
    if not path.is_file():
        return {}
    vertices, texcoords, position, uv = [], [], [], []
    for line in path.read_text().splitlines():
        parts = line.split()
        if not parts:
            continue
        if parts[0] == 'v':
            vertices.append([float(x) for x in parts[1:4]])
        elif parts[0] == 'vt':
            texcoords.append([float(x) for x in parts[1:3]])
        elif parts[0] == 'f':
            refs = [x.split('/') for x in parts[1:]]
            for i in range(1, len(refs)-1):
                for r in [refs[0], refs[i], refs[i+1]]:
                    position.extend(vertices[int(r[0])-1])
                    uv.extend(texcoords[int(r[1])-1] if len(r)>1 and r[1] else [0,0])
    if not position:
        raise ValueError('original core OBJ contains no faces')
    return {'position':position, 'uv':uv}


class AssetServer:
    def __init__(self, assets):
        self.assets = assets
        self.core = json.dumps(core_geometry()).encode()
        owner = self
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass
            def do_GET(self):
                route = unquote(urlsplit(self.path).path)
                path = None
                if route == '/core.json':
                    data, kind = owner.core, 'application/json'
                else:
                    if route in ('/','/render.html'):
                        path = HERE/'render.html'
                    elif route in ('/render.js','/diagnostics.js'):
                        path = HERE/route[1:]
                    elif route == '/core.png':
                        path = CORE.with_suffix('.png')
                    elif route.startswith('/api/models/') and route.endswith('.glb'):
                        path = owner.assets.get(route.rsplit('/',1)[1][:-4])
                    elif route.startswith('/editor/'):
                        candidate = (EDITOR/route[len('/editor/'):]).resolve()
                        if candidate.is_relative_to(EDITOR):
                            path = candidate
                    if path is None or not path.is_file():
                        self.send_error(404);return
                    data = path.read_bytes()
                    kind = mimetypes.guess_type(str(path))[0] or 'application/octet-stream'
                self.send_response(200)
                self.send_header('Content-Type',kind)
                self.send_header('Content-Length',str(len(data)))
                self.end_headers();self.wfile.write(data)
        self.http = ThreadingHTTPServer(('127.0.0.1',0),Handler)
        self.thread = threading.Thread(target=self.http.serve_forever,daemon=True)
    def __enter__(self):
        self.thread.start();return self
    def __exit__(self,*args):
        self.http.shutdown();self.http.server_close();self.thread.join()
    @property
    def url(self):
        return f'http://127.0.0.1:{self.http.server_port}/'


def png_bytes(data_url):
    if not data_url.startswith('data:image/png;base64,'):
        raise ValueError('renderer did not return PNG')
    return base64.b64decode(data_url.split(',',1)[1], validate=True)


def axes(q):
    """Rotation basis for the independent OBB-overlap DIAGNOSTIC, not absorption."""
    x,y,z,w = q
    norm = math.sqrt(sum(v*v for v in q))
    x,y,z,w = [v/norm for v in (x,y,z,w)]
    return [(1-2*(y*y+z*z),2*(x*y+w*z),2*(x*z-w*y)),
            (2*(x*y-w*z),1-2*(x*x+z*z),2*(y*z+w*x)),
            (2*(x*z+w*y),2*(y*z-w*x),1-2*(x*x+y*y))]


def dot(a,b):
    return sum(x*y for x,y in zip(a,b))


def box_overlap(a,b):
    """Separating-axis OBB overlap. Empty corners/cavities can be false positives."""
    aa,bb = axes(a['rotationQuat']),axes(b['rotationQuat'])
    directions=aa+bb
    for u in aa:
        for v in bb:
            directions.append((u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0]))
    delta=[x-y for x,y in zip(a['position'],b['position'])]
    minimum=math.inf
    for axis in directions:
        length=math.sqrt(dot(axis,axis))
        if length<1e-8:
            continue
        axis=[x/length for x in axis]
        ra=sum(abs(dot(axis,v))*s/2 for v,s in zip(aa,a['aabb']))
        rb=sum(abs(dot(axis,v))*s/2 for v,s in zip(bb,b['aabb']))
        depth=ra+rb-abs(dot(delta,axis))
        if depth<=0:
            return None
        minimum=min(minimum,depth)
    return minimum


def overlap_report(items):
    pairs=[]; affected=set()
    for i,a in enumerate(items):
        for b in items[i+1:]:
            depth=box_overlap(a,b)
            if depth is not None:
                pairs.append({'a':a['itemId'],'b':b['itemId'],'satDepthMm':1000*depth})
                affected.update([a['itemId'],b['itemId']])
    return {'testedPairs':len(items)*(len(items)-1)//2,'overlapPairs':len(pairs),
            'affectedItems':len(affected),'maxSatDepthMm':max((p['satDepthMm'] for p in pairs),default=0),
            'metric':'OBB overlap upper bound; not a mesh penetration count', 'pairs':pairs}


def span_for(items, start_diameter):
    # Conservative camera framing only; not a contact/growth calculation.
    radius=max([start_diameter/2]+[math.sqrt(sum(x*x for x in p['position']))+
                   math.sqrt(sum(x*x for x in p['aabb']))/2 for p in items])
    return 2*radius*1.12


@contextlib.contextmanager
def open_viewer(out,level_path=LEVEL):
    """Headless viewer page (leveleditor EditorView) with the level's real GLBs; yields (chrome, assets)."""
    out=Path(out).expanduser().resolve()
    if out.is_relative_to(ROOT):
        raise ValueError('reference renders must be outside repository')
    out.mkdir(parents=True,exist_ok=True)
    _,assets=load_assets(level_path)
    with AssetServer(assets) as server:
        chrome=Chrome(profile_dir=out/'chromium-profile',width=512,height=512,
                      log_path=out/'chromium.log',extra_args=['--headless=new'])
        try:
            chrome.start();chrome.navigate(server.url)
            chrome.cdp.evaluate("new Promise((resolve,reject)=>{let n=0;const t=setInterval(()=>{if(window.__attachReady){clearInterval(t);resolve(true)}else if(++n>600){clearInterval(t);reject(new Error('viewer startup timeout'))}},100)})",await_promise=True,timeout=70)
            yield chrome,assets
            errors=chrome.cdp.errors()
            if errors:raise RuntimeError(f'viewer errors: {errors}')
        finally:
            chrome.stop()


def solve_touch_stops(document,out,level_path=LEVEL):
    """Control only: stop distance per incoming prop when real meshes also stop on attached meshes."""
    stops,detail={},{}
    with open_viewer(out,level_path) as (chrome,assets):
        for run in document['runs']:
            items=run['snapshots'][-1]['items']
            request=[{'def':item['def'],'aabb':item['aabb'],'rotationQuat':item['rotationQuat'],'itemId':item['itemId'],
                      'direction':event['direction'],'ballStopM':event['stopDistanceM']}
                     for item,event in zip(items,run['events'])]
            missing=sorted({r['def'] for r in request}-assets.keys())
            if missing:raise ValueError(f'missing real GLB assets: {missing}')
            rows=chrome.cdp.evaluate(f'window.solveTouchStops({json.dumps(request)})',await_promise=True,timeout=600)
            stops[str(run['seed'])]=[r['stopM'] for r in rows]
            detail[str(run['seed'])]=rows
            print(f'seed={run["seed"]} stopped by attached meshes: {sum(r["stoppedByAttached"] for r in rows)}/{len(rows)}',flush=True)
    return stops,detail


def render_all(document,out,level_path=LEVEL,core_mode='fixed',span_overrides=None):
    if core_mode not in ('fixed','grow'):raise ValueError('core_mode must be fixed or grow')
    out=Path(out).expanduser().resolve()
    doc,_=load_assets(level_path)
    report={'levelSha256':hashlib.sha256(Path(level_path).read_bytes()).hexdigest(),
            'renderer':'Tools/leveleditor EditorView + shot_cdp.Chrome; SwiftShader',
            'core':str(CORE) if CORE.is_file() else 'sphere','coreMode':core_mode,
            'mask':'exact RGB 255,0,255; props black preserving texture alpha/opacity; no tone mapping on mask materials',
            'viewDirections':['+X','-X','+Y','-Y','+Z','-Z'], 'runs':[]}
    with open_viewer(out,level_path) as (chrome,assets):
        montage_rows=[]
        for run in document['runs']:
            seed=run['seed'];folder=out/f'seed_{seed}';folder.mkdir(exist_ok=True)
            span=(span_overrides or {}).get(seed,span_for(run['snapshots'][-1]['items'],run['startDiameterM']))
            results=[]
            for snap in run['snapshots']:
                missing=sorted({p['def'] for p in snap['items']}-assets.keys())
                if missing:raise ValueError(f'missing real GLB assets: {missing}')
                payload=chrome.cdp.evaluate(f'window.renderAttachment({json.dumps(snap)},{snap["diameterM"] if core_mode=="grow" else run["startDiameterM"]},{span})',await_promise=True,timeout=120)
                rows=[]
                for i,row in enumerate(payload['rows']):
                    for key,suffix in [('beauty',''),('mask','_mask'),('coreOnly','_core_only')]:
                        (folder/f'n{snap["n"]}_{i}{suffix}.png').write_bytes(png_bytes(row.pop(key)))
                    rows.append(row)
                montage=folder/f'n{snap["n"]}_six_views.png';montage.write_bytes(png_bytes(payload['montage']))
                montage_rows.append({'seed':seed,'n':snap['n'],'image':payload['montage']})
                results.append({'n':snap['n'],'diameterM':snap['diameterM'],'coreDiameterM':snap['diameterM'] if core_mode=='grow' else run['startDiameterM'],
                                'meanVisibleFraction':sum(r['fraction'] for r in rows)/6,'views':rows,
                                'overlap':overlap_report(snap['items']),'meshGeometry':payload['geometry'],
                                'montage':str(montage)})
                print(f'seed={seed} N={snap["n"]} core={results[-1]["meanVisibleFraction"]:.6%}',flush=True)
            report['runs'].append({'seed':seed,'spanM':span,'snapshots':results})
        expression="""(async (rows)=>{const c=document.createElement('canvas');c.width=COLS*512;c.height=Math.ceil(rows.length/COLS)*394;const x=c.getContext('2d');x.fillStyle='#111925';x.fillRect(0,0,c.width,c.height);for(let i=0;i<rows.length;i++){const im=new Image();im.src=rows[i].image;await im.decode();const px=(i%COLS)*512,py=Math.floor(i/COLS)*394;x.drawImage(im,px,py,512,363);x.fillStyle='#ffffff';x.font='20px sans-serif';x.fillText(`seed=${rows[i].seed} N=${rows[i].n}`,px+10,py+386)}return c.toDataURL('image/png')})(ROWS)""".replace('COLS',str(len(document['runs'][0]['snapshots']))).replace('ROWS',json.dumps(montage_rows))
        (out/'overview.png').write_bytes(png_bytes(chrome.cdp.evaluate(expression,await_promise=True,timeout=120)))
        report['browserErrors']=chrome.cdp.errors()
    summaries=[]
    for n in sorted({s['n'] for r in report['runs'] for s in r['snapshots']}):
        values=[s['meanVisibleFraction'] for r in report['runs'] for s in r['snapshots'] if s['n']==n]
        if values:summaries.append({'n':n,'seeds':len(values),'mean':sum(values)/len(values),'min':min(values),'max':max(values)})
    report['visibilitySummary']=summaries
    (out/'render_report.json').write_text(json.dumps(report,indent=2)+'\n')
    return report


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('input',type=Path,help='JSON emitted by the shared runtime C# evaluator')
    parser.add_argument('--out',required=True,type=Path)
    parser.add_argument('--level',default=LEVEL,type=Path)
    args=parser.parse_args()
    report=render_all(json.loads(args.input.read_text()),args.out,args.level)
    print(json.dumps(report['visibilitySummary'],indent=2))

if __name__=='__main__':main()
