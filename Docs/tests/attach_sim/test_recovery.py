import json
from pathlib import Path
import subprocess
import tempfile
import unittest

from Tools.attach_sim.recovery import curve_for, floating_summary, ROOT, LEVEL


class RecoveryControlTests(unittest.TestCase):
    def test_literature_knots_and_hypothetical_mappings_are_explicit(self):
        self.assertIsNone(curve_for('baseline',.222))
        absolute=curve_for('mas1-absolute',.222)
        self.assertEqual(absolute['diameterM'],[.05,.1,.15,.2])
        self.assertEqual(absolute['ratio'],[.8,.8,.4,.16])
        normalized=curve_for('mas1-normalized',.222)
        for actual,expected in zip(normalized['diameterM'],[.222,.444,.666,.888]):
            self.assertAlmostEqual(actual,expected)
        self.assertEqual(curve_for('mas1-half-normalized',.222)['ratio'],[.4,.4,.2,.08])
        self.assertIn('hypothesis',absolute['mapping'])
        self.assertIn('not Cancer',absolute['evidence'])
        with self.assertRaises(ValueError):curve_for('unknown',.222)

    def test_floating_proxy_excludes_intersections_and_core_proximity(self):
        doc={'runs':[{'seed':1,'startDiameterM':.2}]}
        rendered={'runs':[{'seed':1,'snapshots':[{'n':4,'meshGeometry':{
            'pairs':[['a','b']], 'items':[
                {'itemId':'a','meshDistanceM':.2},{'itemId':'b','meshDistanceM':.2},
                {'itemId':'c','meshDistanceM':.105},{'itemId':'d','meshDistanceM':.13}]}}]}]}
        row=floating_summary(doc,rendered)[0]
        self.assertAlmostEqual(row['isolatedFraction']['mean'],.25)
        self.assertEqual(row['meshToCoreSpherePositiveGapMm']['count'],4)


@unittest.skipUnless((Path.home()/'.dotnet/dotnet').is_file() and LEVEL.is_file(),'local .NET/reference level absent')
class RuntimeRecoveryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.dotnet=Path.home()/'.dotnet/dotnet'
        subprocess.run([str(cls.dotnet),'build','Tools/attach_sim','-c','Release','--nologo','-v:q'],cwd=ROOT,check=True)
        cls.tmp=tempfile.TemporaryDirectory(prefix='recovery-test-')
        cls.folder=Path(cls.tmp.name)
        cls.command=[str(cls.dotnet),str(ROOT/'Tools/attach_sim/bin/Release/net8.0/attach_sim.dll'),str(LEVEL)]
        cls.curve=cls.folder/'curve.json'
        cls.curve.write_text(json.dumps({'diameterM':[.05,.10],'ratio':[.4,.4]}))
        cls.lowpath=cls.folder/'low.json'
        subprocess.run(cls.command+[str(cls.lowpath),'1,2',f'--growth-curve={cls.curve}','--ns=10,20'],cwd=ROOT,check=True)
        cls.low=json.loads(cls.lowpath.read_text())
        cls.basepath=cls.folder/'base.json'
        subprocess.run(cls.command+[str(cls.basepath),'1,2',f'--draws={cls.lowpath}','--ns=10,20'],cwd=ROOT,check=True)
        cls.base=json.loads(cls.basepath.read_text())

    @classmethod
    def tearDownClass(cls):
        cls.tmp.cleanup()

    def test_multiplier_replaces_point_eight_instead_of_doubling_penalty(self):
        for run in self.low['runs']:
            for event in run['events']:
                self.assertAlmostEqual(event['recoveryRatio'],.4,places=6)
                self.assertLessEqual(abs(event['appliedGrowthMm3']-event['baseGrowthMm3']/2),1)
        for base,low in zip(self.base['runs'],self.low['runs']):
            self.assertGreater(base['snapshots'][-1]['diameterM'],low['snapshots'][-1]['diameterM'])
            self.assertEqual([e['itemId'] for e in base['events']],[e['itemId'] for e in low['events']])
            self.assertEqual([e['direction'] for e in base['events']],[e['direction'] for e in low['events']])
            self.assertEqual([i['rotationQuat'] for i in base['snapshots'][-1]['items']],
                             [i['rotationQuat'] for i in low['snapshots'][-1]['items']])
            self.assertTrue(all(e['touchedBallGate'] for e in low['events']))

    def test_piecewise_linear_ratio_uses_pre_pickup_size_and_endpoint_clamp(self):
        curve=self.folder/'sloped.json'
        curve.write_text(json.dumps({'diameterM':[.222,.3],'ratio':[.8,.16]}))
        output=self.folder/'sloped_output.json'
        subprocess.run(self.command+[str(output),'1',f'--growth-curve={curve}',f'--draws={self.lowpath}','--ns=10,20'],cwd=ROOT,check=True)
        run=json.loads(output.read_text())['runs'][0]
        for event in run['events']:
            d=event['preDiameterM']
            expected=.8 if d<=.222 else .16 if d>=.3 else .8+(.16-.8)*(d-.222)/(.3-.222)
            self.assertAlmostEqual(event['recoveryRatio'],expected,places=6)
        self.assertGreater(run['events'][0]['recoveryRatio'],run['events'][-1]['recoveryRatio'])
        # The constant 0.4 test above already exercises the upper endpoint clamp (D > .10 m).

    def test_invalid_curve_is_refused_without_a_simulation_result(self):
        bad=self.folder/'bad.json';bad.write_text(json.dumps({'diameterM':[.2,.1],'ratio':[.8,.4]}))
        output=self.folder/'bad_output.json'
        result=subprocess.run(self.command+[str(output),'1',f'--growth-curve={bad}'],cwd=ROOT,capture_output=True,text=True)
        self.assertNotEqual(result.returncode,0)
        self.assertIn('strictly increasing',result.stderr)
        self.assertFalse(output.exists())
