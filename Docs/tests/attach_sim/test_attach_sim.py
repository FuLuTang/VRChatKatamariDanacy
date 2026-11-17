import json
import math
import shutil
from pathlib import Path
import subprocess
import tempfile
import unittest

from Tools.attach_sim.render import box_overlap, overlap_report, core_geometry, load_assets, LEVEL
from Tools.attach_sim.run import (distribution, summarize, source_hashes, controls, embed_stops,
                                  contact_summary, VARIANTS, FACTORIAL)

ROOT=Path(__file__).resolve().parents[3]


def box(position=(0,0,0), size=(1,1,1), rotation=(0,0,0,1), item='a'):
    return {'position':position,'aabb':size,'rotationQuat':rotation,'itemId':item}


class DiagnosticTests(unittest.TestCase):
    def test_disjoint_and_touching_boxes_are_not_penetrations(self):
        self.assertIsNone(box_overlap(box(),box((2,0,0))))
        self.assertIsNone(box_overlap(box(),box((1,0,0))))

    def test_overlap_depth_and_pair_denominator(self):
        self.assertAlmostEqual(box_overlap(box(),box((.75,0,0))),.25)
        report=overlap_report([box(item='a'),box((.75,0,0),item='b'),box((5,0,0),item='c')])
        self.assertEqual((report['testedPairs'],report['overlapPairs'],report['affectedItems']),(3,1,2))

    def test_rotated_skinny_boxes_are_not_world_aabb_intersections(self):
        q=(0,0,math.sin(math.pi/8),math.cos(math.pi/8))
        a=box(size=(2,.1,.1),rotation=q)
        b=box((0,.3,0),size=(2,.1,.1),rotation=q)
        self.assertIsNone(box_overlap(a,b))

    def test_distribution_uses_seed_level_mean_range(self):
        self.assertEqual(distribution([0,2,4])['mean'],2)
        self.assertEqual(distribution([0,2,4])['p90'],3.6)

    def test_runtime_sources_are_linked_and_hashed(self):
        hashes=source_hashes()
        self.assertTrue(any(p.endswith('/PickupAbsorbStepMath.cs') for p in hashes))
        self.assertTrue(any(p.endswith('/AttachmentStepMath.cs') for p in hashes))
        self.assertTrue(all(len(h)==64 for h in hashes.values()))


class ControlTests(unittest.TestCase):
    def test_controls_pair_variants_against_baseline(self):
        def variant(value):
            return {'visibility':[{'n':10,'mean':value}]}
        rows=controls({'baseline':variant(.5),'core-grows':variant(.45),'touch-mesh':variant(.6),
                       'touch-mesh+core-grows':variant(.62),'embed-45':variant(.3)})
        row=rows[0]
        self.assertAlmostEqual(row['gainCoreGrows'],-.05)
        self.assertAlmostEqual(row['gainTouchAttached'],.1)
        self.assertAlmostEqual(row['gainBoth'],.12)
        self.assertAlmostEqual(row['interaction'],.12-.1+.05)
        self.assertAlmostEqual(row['gainEmbed45'],-.2)
        self.assertAlmostEqual(row['hiddenShareOfBaseline'],.5)

    def test_controls_absent_without_rendering(self):
        self.assertIsNone(controls({'baseline':{}}))
        self.assertTrue(set(FACTORIAL)<=set(VARIANTS))

    def test_embed_pulls_radially_by_fraction_of_box_extent(self):
        doc={'runs':[{'seed':7,'events':[{'direction':[1,0,0],'stopDistanceM':.5}],
                      'snapshots':[{'items':[{'rotationQuat':[0,0,0,1],'aabb':[.2,.4,.6]}]}]}]}
        self.assertAlmostEqual(embed_stops(doc,.5)['7'][0],.5-.5*.2)
        # a quarter turn about Z puts the 0.4 m axis along +X
        q=[0,0,math.sin(math.pi/4),math.cos(math.pi/4)]
        doc['runs'][0]['snapshots'][0]['items'][0]['rotationQuat']=q
        self.assertAlmostEqual(embed_stops(doc,.5)['7'][0],.5-.5*.4,places=6)
        self.assertEqual(embed_stops(doc,100)['7'][0],0.0)

    def test_contact_summary_counts_attached_stops_from_solver_detail(self):
        doc={'runs':[{'events':[{'attachmentGapMm':1,'stopDistanceM':.3},{'attachmentGapMm':2,'stopDistanceM':.2}]}]}
        detail={'1':[{'stoppedByAttached':True},{'stoppedByAttached':False}]}
        self.assertEqual(contact_summary(doc,detail)['itemsStoppedByAttachedItemsOnly'],1)
        self.assertIsNone(contact_summary(doc)['itemsStoppedByAttachedItemsOnly'])


class MeshDiagnosticTests(unittest.TestCase):
    @unittest.skipUnless(shutil.which('node'),'node absent')
    def test_triangle_and_tree_self_tests_run_in_node(self):
        script=("import('%s').then(m=>console.log(JSON.stringify([m.diagnosticSelfTest(),m.treeSelfTest()])))"
                % (ROOT/'Tools/attach_sim/diagnostics.js'))
        out=subprocess.run(['node','--input-type=module','-e',script],check=True,capture_output=True,text=True).stdout
        triangles,tree=json.loads(out)
        self.assertFalse(triangles['disjoint'])
        self.assertTrue(triangles['crossing'])
        self.assertTrue(tree['hit'])
        self.assertAlmostEqual(tree['distance'],3,places=6)

    @unittest.skipUnless(shutil.which('node'),'node absent')
    def test_coplanar_overlap_and_shifted_tree_contact(self):
        script="""import('%s').then(m=>{
          const a=[[0,0,0],[1,0,0],[0,1,0]],b=[[.2,.2,0],[1.2,.2,0],[.2,1.2,0]],far=[[5,5,5],[6,5,5],[5,6,5]];
          const w=m.tree([[[0,0,0],[1,0,0],[0,1,0]]]);
          console.log(JSON.stringify([m.triangleMeet(a,b),m.triangleMeet(a,far),
            m.treesMeetShifted(w,[0,0,0],w),m.treesMeetShifted(w,[0,0,.5],w)]))})""" % (ROOT/'Tools/attach_sim/diagnostics.js')
        out=subprocess.run(['node','--input-type=module','-e',script],check=True,capture_output=True,text=True).stdout
        self.assertEqual(json.loads(out),[True,False,True,False])


class ReferenceAssetTests(unittest.TestCase):
    @unittest.skipUnless(LEVEL.is_file(),'local unlicensed reference level absent')
    def test_real_models_resolve_without_fallback(self):
        doc,assets=load_assets()
        self.assertEqual(len(doc['props']),511)
        self.assertTrue({p['def'] for p in doc['props']}.issubset(assets))
        self.assertTrue(all(not p.is_relative_to(ROOT) for p in assets.values()))

    def test_core_obj_has_triangle_uvs(self):
        data=core_geometry()
        if not data:self.skipTest('local original core absent')
        self.assertEqual(len(data['position'])%9,0)
        self.assertEqual(len(data['uv'])*3,len(data['position'])*2)


class RuntimeDriverTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dotnet=Path.home()/'.dotnet/dotnet'
        if not cls.dotnet.is_file() or not LEVEL.is_file():
            raise unittest.SkipTest('local .NET/reference level absent')
        subprocess.run([str(cls.dotnet),'build','Tools/attach_sim/attach_sim.csproj','-c','Release','--nologo','-v:q'],cwd=ROOT,check=True)
        cls.tmp=tempfile.TemporaryDirectory(prefix='attach-sim-test-')
        cls.output=Path(cls.tmp.name)/'simulation.json'
        cls.command=[str(cls.dotnet),str(ROOT/'Tools/attach_sim/bin/Release/net8.0/attach_sim.dll'),str(LEVEL),str(cls.output),'1,2,3,4,5']
        subprocess.run(cls.command,cwd=ROOT,check=True)
        cls.data=json.loads(cls.output.read_text())

    @classmethod
    def tearDownClass(cls):
        if hasattr(cls,'tmp'):cls.tmp.cleanup()

    def test_five_seeds_freeze_forty_unique_instances(self):
        self.assertEqual(len(self.data['runs']),5)
        for run in self.data['runs']:
            self.assertEqual([s['n'] for s in run['snapshots']],[10,20,30,40])
            self.assertEqual(len({i['itemId'] for i in run['snapshots'][-1]['items']}),40)
            ds=[s['diameterM'] for s in run['snapshots']]
            self.assertEqual(ds,sorted(ds))
            self.assertAlmostEqual(run['startDiameterM'],.222,places=6)

    def test_runtime_gaps_and_rigid_event_roundtrip(self):
        summary=summarize(self.data)
        self.assertEqual(summary['absorptionCount'],200)
        self.assertEqual(summary['gapsAbovePointTwoMm'],0)
        self.assertLess(summary['worldToLocalGapDeltaMm']['max'],.2)
        self.assertGreater(summary['attachmentGapMm']['min'],-.001)

    def test_seed_replay_is_deterministic(self):
        before=self.output.read_bytes()
        subprocess.run(self.command,cwd=ROOT,check=True)
        self.assertEqual(before,self.output.read_bytes())

    def test_attached_contact_controls_replay_same_draws_and_stop_no_nearer_than_sphere(self):
        boxes=Path(self.tmp.name)/'boxes.json'
        subprocess.run(self.command[:3]+[str(boxes),'1,2','--touch-attached'],cwd=ROOT,check=True)
        control=json.loads(boxes.read_text())
        self.assertEqual([r['seed'] for r in control['runs']],[1,2])
        for base,ctrl in zip(self.data['runs'],control['runs']):
            self.assertEqual([e['itemId'] for e in base['events']],[e['itemId'] for e in ctrl['events']])
            self.assertEqual([e['direction'] for e in base['events']],[e['direction'] for e in ctrl['events']])
            for b,c in zip(base['events'],ctrl['events']):
                self.assertGreaterEqual(c['stopDistanceM'],b['stopDistanceM']-1e-5)
            self.assertEqual([s['diameterM'] for s in base['snapshots']],[s['diameterM'] for s in ctrl['snapshots']])

    def test_given_stops_are_replayed_through_runtime_steps(self):
        stops=Path(self.tmp.name)/'stops.json';out=Path(self.tmp.name)/'stopped.json'
        base=self.data['runs'][0]
        stops.write_text(json.dumps({'1':[e['stopDistanceM']+.05 for e in base['events']]}))
        subprocess.run(self.command[:3]+[str(out),'1',f'--stops={stops}'],cwd=ROOT,check=True)
        run=json.loads(out.read_text())['runs'][0]
        for b,r in zip(base['events'],run['events']):
            self.assertAlmostEqual(r['stopDistanceM'],b['stopDistanceM']+.05,places=4)
        self.assertEqual(len(run['snapshots']),4)

    def test_snapshot_counts_are_configurable(self):
        out=Path(self.tmp.name)/'ns.json'
        subprocess.run(self.command[:3]+[str(out),'1','--ns=5,7'],cwd=ROOT,check=True)
        self.assertEqual([s['n'] for s in json.loads(out.read_text())['runs'][0]['snapshots']],[5,7])

    def test_directions_and_quaternions_are_unit_random_samples(self):
        for run in self.data['runs']:
            for event in run['events']:
                self.assertAlmostEqual(sum(x*x for x in event['direction']),1,places=5)
            for item in run['snapshots'][-1]['items']:
                self.assertAlmostEqual(sum(x*x for x in item['rotationQuat']),1,places=5)


if __name__=='__main__':unittest.main()
