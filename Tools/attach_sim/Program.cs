using System.Text.Json;
using System.Text.Json.Nodes;
using Random = System.Random;
using UnityEngine;
using VRCKatamari.Pickup;
using VRCKatamari.Economy;
using VRCKatamari.BallLayers;
using VRCKatamari.BallContact;

// Offline driver owns sampling and the incoming trajectory, never gameplay policy.
// Every gate, commit, event, size change and needle registration uses linked runtime code.
internal sealed class Simulation
{
    readonly int[] roundState = new int[GrowthLedgerMath.RoundStateCount];
    readonly string[] players = new string[1];
    readonly long[] volumes = new long[1], scores = new long[1];
    readonly int[] lives = new int[1], playerGenerations = new int[1];
    readonly bool[] respawnSet = new bool[1];
    readonly float[] respawnAt = new float[1];
    readonly string[] commitIds = new string[256], winners = new string[256];
    readonly int[] commitGenerations = new int[256], commitSequences = new int[256];
    readonly long[] commitGains = new long[256];
    readonly string[] claimIds = new string[8], claimPlayers = new string[8];
    readonly int[] claimGenerations = new int[8], claimFrames = new int[8], claimRequests = new int[8];
    readonly string[] predictionPlayers = new string[0], predictionIds = new string[0];
    readonly int[] predictionGenerations = new int[0];
    readonly long[] predictionGains = new long[0];
    int commitCount, claimCount, predictionCount, requestSequence, absorbedCount;
    long unbanked, banked, confirmed, startVolume;
    float coreRadius, collisionRadius, diameter;
    const float MaxDiameter = 30f;
    readonly string[] allowed = {"reference"};

    const int P = 4;
    readonly bool[] absorbable = new bool[P], accepted = new bool[P], committed = new bool[P], failures = new bool[P];
    readonly string[] kinds = new string[P], ids = new string[P];
    readonly Vector3[] sizes = new Vector3[P], positions = new Vector3[P];
    readonly Quaternion[] rotations = new Quaternion[P];
    readonly long[] growth = new long[P], gains = new long[P];
    readonly float[] volumeK = new float[P], submitTimes = new float[P], distances = new float[P], limits = new float[P];
    readonly int[] generations = new int[P], outcomes = new int[P], claimFailures = new int[P], resolveFailures = new int[P], claimCountsAtFailure = new int[P];

    readonly string[] eventIds = new string[AttachmentStepMath.EventCapacity];
    readonly Vector3[] eventPositions = new Vector3[AttachmentStepMath.EventCapacity], localPositions = new Vector3[AttachmentStepMath.EventCapacity], centers = new Vector3[AttachmentStepMath.EventCapacity];
    readonly Quaternion[] localRotations = new Quaternion[AttachmentStepMath.EventCapacity];
    readonly float[] eventCollision = new float[AttachmentStepMath.EventCapacity], eventContact = new float[AttachmentStepMath.EventCapacity];
    readonly int[] eventSteps = new int[AttachmentStepMath.EventCapacity];
    readonly bool[] eventValid = new bool[AttachmentStepMath.EventCapacity];
    int eventCount, ringCount;

    readonly Vector3[] directions = new Vector3[RadialProxyMath.DirectionCount];
    readonly float[] supports = new float[RadialProxyMath.DirectionCount], scratch = new float[RadialProxyMath.DirectionCount];
    readonly int[] affected = new int[RadialProxyMath.DirectionCount];
    readonly Vector3[] endpoints = new Vector3[AttachmentStepMath.ProxyEndpointCapacity], corners = new Vector3[RadialProxyShellMath.AabbCornerCount];
    readonly int[] starts = new int[AttachmentStepMath.ProxyItemCapacity], lengths = new int[AttachmentStepMath.ProxyItemCapacity];
    readonly string[] proxyKeys = new string[AttachmentStepMath.ProxyItemCapacity], candidateKeys = new string[RadialProxyMath.MaxExtremeCandidates];
    readonly bool[] candidates = new bool[AttachmentStepMath.ProxyItemCapacity];
    int endpointCount, proxyCount, candidateCount;
    readonly Random random;
    // Hypothetical control only: the incoming prop also stops on already-attached item boxes.
    readonly bool touchAttached;
    // Hypothetical control: stop distances solved elsewhere (real-mesh contact), replayed through the same runtime steps.
    readonly float[] givenStops;
    readonly List<(Vector3 position,Quaternion rotation,Vector3 size)> attachedBoxes = new();
    public readonly float StartDiameter;
    public readonly List<JsonObject> Items = new();
    public readonly List<object> Events = new();

    public Simulation(int seed, float startDiameter, bool touchAttached = false, float[] givenStops = null)
    {
        random = new Random(seed); this.touchAttached = touchAttached; this.givenStops = givenStops;
        roundState[GrowthLedgerMath.RoundPhaseState] = (int)LedgerPhase.Playing;
        players[0] = "offline-player"; lives[0] = (int)PlayerLifecycle.Active;
        Check(SizeModelMath.TrySphereVolumeMm3(startDiameter,1f,out startVolume),"start volume");
        Recompute(); StartDiameter = diameter;
        Check(RadialProxyMath.TryFillDirections(directions),"directions");
    }
    void Recompute() => Check(PickupAbsorbStepMath.RecomputeConfirmedAndSize(unbanked,0,startVolume,
        MaxDiameter,1f,1f,ref confirmed,ref coreRadius,ref collisionRadius,ref diameter),"size");
    static void Check(bool ok,string operation) { if(!ok)throw new InvalidOperationException(operation+" refused"); }
    static Vector3 Vec(JsonNode a) => new(a[0].GetValue<float>(),a[1].GetValue<float>(),a[2].GetValue<float>());
    static float[] Array(Vector3 v) => new[]{v.x,v.y,v.z};
    static float[] Array(Quaternion q) => new[]{q.x,q.y,q.z,q.w};
    Vector3 RandomDirection()
    {
        double y = 2*random.NextDouble()-1, phi=2*Math.PI*random.NextDouble();
        double r=Math.Sqrt(1-y*y);
        return new Vector3((float)(r*Math.Cos(phi)),(float)y,(float)(r*Math.Sin(phi)));
    }
    Quaternion RandomRotation()
    {
        double a=random.NextDouble(), b=2*Math.PI*random.NextDouble(), c=2*Math.PI*random.NextDouble();
        return new Quaternion((float)(Math.Sqrt(1-a)*Math.Sin(b)),(float)(Math.Sqrt(1-a)*Math.Cos(b)),
            (float)(Math.Sqrt(a)*Math.Sin(c)),(float)(Math.Sqrt(a)*Math.Cos(c)));
    }
    AbsorbOutcome Evaluate(JsonObject prop, Vector3 position, Quaternion rotation)
    {
        return PickupAbsorbStepMath.EvaluateCandidate(prop["absorbable"].GetValue<bool>(),prop["kind"].GetValue<string>(),
            allowed,allowed.Length,Vec(prop["aabb"]),rotation,position,Vector3.zero,collisionRadius,0,
            prop["volumeK"].GetValue<float>(),diameter,MaxDiameter/2,prop["growthMm3"].GetValue<long>(),out _,out _);
    }
    // Separating-axis test for two boxes (centre, orientation, full size); touching counts as overlap.
    static bool BoxesTouch(Vector3 ca,Quaternion qa,Vector3 sa,Vector3 cb,Quaternion qb,Vector3 sb)
    {
        var a=new[]{qa*Vector3.right,qa*Vector3.up,qa*Vector3.forward};
        var b=new[]{qb*Vector3.right,qb*Vector3.up,qb*Vector3.forward};
        float[] ha={sa.x/2,sa.y/2,sa.z/2}, hb={sb.x/2,sb.y/2,sb.z/2};
        var axes=new List<Vector3>(15);axes.AddRange(a);axes.AddRange(b);
        foreach(var u in a)foreach(var v in b){var c=Vector3.Cross(u,v);if(c.sqrMagnitude>1e-10f)axes.Add(c.normalized);}
        var delta=cb-ca;
        foreach(var axis in axes)
        {
            float ra=0,rb=0;
            for(int i=0;i<3;i++){ra+=Math.Abs(Vector3.Dot(axis,a[i]))*ha[i];rb+=Math.Abs(Vector3.Dot(axis,b[i]))*hb[i];}
            if(Math.Abs(Vector3.Dot(delta,axis))>ra+rb)return false;
        }
        return true;
    }
    bool TouchesAttached(Vector3 size,Quaternion rotation,Vector3 position)
    {
        foreach(var box in attachedBoxes)
            if(BoxesTouch(position,rotation,size,box.position,box.rotation,box.size))return true;
        return false;
    }
    Vector3 FlyToContact(Vector3 size, Quaternion rotation, Vector3 direction)
    {
        float far = collisionRadius+size.magnitude+0.01f;
        foreach(var box in attachedBoxes)far=Math.Max(far,box.position.magnitude+box.size.magnitude/2+size.magnitude+0.01f);
        float previous=far, current=far;
        bool Touch(float distance)
        {
            Check(PickupAbsorbMath.TryComputeContact(size,rotation,direction*distance,Vector3.zero,
                collisionRadius,0,out float d,out float limit),"contact geometry");
            return d<=limit || (touchAttached && TouchesAttached(size,rotation,direction*distance));
        }
        Check(!Touch(far),"outside trajectory start");
        while(!Touch(current)) { previous=current; current=Math.Max(0,current-.001f); }
        // Refine the first 1 mm crossing without replacing the runtime contact predicate.
        for(int i=0;i<24;i++) { float mid=(previous+current)*.5f; if(Touch(mid))current=mid;else previous=mid; }
        return direction*current;
    }
    public void AbsorbOne(List<JsonObject> remaining)
    {
        var eligible=remaining.Where(p=>Evaluate(p,Vector3.zero,Quaternion.identity)==AbsorbOutcome.Absorb).ToList();
        if(eligible.Count==0)throw new InvalidOperationException("no remaining runtime-eligible reference props");
        var prop=eligible[random.Next(eligible.Count)];remaining.Remove(prop);
        var direction=RandomDirection();var rotation=RandomRotation();var size=Vec(prop["aabb"]);
        var position=givenStops!=null?direction*givenStops[Items.Count]:FlyToContact(size,rotation,direction);
        absorbable[0]=prop["absorbable"].GetValue<bool>();kinds[0]=prop["kind"].GetValue<string>();
        sizes[0]=size;rotations[0]=rotation;positions[0]=position;
        growth[0]=prop["growthMm3"].GetValue<long>();volumeK[0]=prop["volumeK"].GetValue<float>();
        ids[0]=prop["itemId"].GetValue<string>();generations[0]=0;int count=1;
        PickupAbsorbStepMath.EvaluatePendingCandidates(absorbable,kinds,sizes,rotations,positions,growth,
            volumeK,count,allowed,allowed.Length,Vector3.zero,collisionRadius,0,diameter,MaxDiameter/2,
            accepted,outcomes,distances,limits);
        // The attached-contact control stops outside the collision sphere, where the runtime gate
        // correctly refuses; it forces acceptance (item already passed the eligibility gate).
        bool gateAccepted=accepted[0];
        if(touchAttached||givenStops!=null)accepted[0]=true;
        Check(accepted[0],"candidate gate");
        float beforeRadius=collisionRadius, beforeDiameter=diameter;
        int eventIndex=eventCount, step=Items.Count+1;
        Check(PickupAbsorbStepMath.CommitAcceptedAbsorbs(true,accepted,ids,generations,growth,count,
            roundState,players,volumes,scores,lives,respawnSet,respawnAt,playerGenerations,1,
            commitIds,commitGenerations,winners,commitSequences,commitGains,ref commitCount,
            claimIds,claimGenerations,claimPlayers,claimFrames,claimRequests,ref claimCount,
            predictionPlayers,predictionIds,predictionGenerations,predictionGains,ref predictionCount,
            "offline-round","offline-player",0,step,ref requestSequence,ref unbanked,ref banked,
            ref absorbedCount,committed,gains,failures,claimFailures,resolveFailures,claimCountsAtFailure),"commit stage");
        Check(committed[0],"ledger commit");
        AttachmentStepMath.RecordAbsorbedEvent(ids[0],position,rotation,Vector3.zero,Quaternion.identity,
            Vector3.zero,Vector3.up,coreRadius,collisionRadius,0,false,.5f,step,
            eventIds,eventPositions,localPositions,localRotations,centers,eventCollision,eventContact,
            eventSteps,eventValid,ref eventCount,ref ringCount);
        PickupAbsorbStepMath.CompactPendingCandidates(committed,ref count,absorbable,kinds,sizes,
            rotations,positions,growth,volumeK,ids,generations,accepted,submitTimes);
        Check(count==0,"candidate removal");Recompute();
        Check(AttachmentStepMath.TryReadAbsorbedEventAttachment(eventIndex,step,eventIds,localPositions,
            localRotations,centers,eventCollision,eventContact,eventSteps,eventValid,eventCount,ringCount,
            out var localPosition,out var localRotation,out _,out float contactCollision,
            out float contactMargin,out int delay),"frozen event read");
        Check(AttachmentMath.TryComputeBoxGap(size,localRotation,localPosition,Vector3.zero,
            contactCollision,contactMargin,out float gap),"attached gap");
        attachedBoxes.Add((localPosition,localRotation,size));
        float gateGap=distances[0]-limits[0];
        bool proxy=AttachmentStepMath.TryAttachRadialProxy(prop["itemId"].GetValue<string>(),size,
            localPosition,localRotation,coreRadius,.01f,directions,supports,endpoints,ref endpointCount,
            starts,lengths,proxyKeys,candidates,ref proxyCount,candidateKeys,ref candidateCount,
            affected,scratch,corners,out int skip);
        if(!proxy && skip!=AttachmentStepMath.ProxySkipInsideCore)throw new InvalidOperationException("needle refused: "+skip);
        var item=(JsonObject)prop.DeepClone();item["position"]=JsonSerializer.SerializeToNode(Array(localPosition));
        item["rotationQuat"]=JsonSerializer.SerializeToNode(Array(localRotation));
        item["contactCollisionRadiusM"]=contactCollision;Items.Add(item);
        Events.Add(new {n=step,itemId=prop["itemId"].GetValue<string>(),eligible=eligible.Count,
            direction=Array(direction),preDiameterM=beforeDiameter,preCollisionRadiusM=beforeRadius,
            postDiameterM=diameter,gateGapMm=gateGap*1000,attachmentGapMm=gap*1000,
            gapDeltaMm=(gap-gateGap)*1000,delaySteps=delay,proxyAccepted=proxy,proxySkip=skip,
            touchedBallGate=gateAccepted,stopDistanceM=position.magnitude});
    }
    public object Snapshot()
    {
        var clamped=new float[RadialProxyMath.DirectionCount];var compact=new int[RadialProxyMath.DirectionCount];
        for(int i=0;i<clamped.Length;i++)Check(BallShapeMath.TryClampNeedleSupport(supports[i],collisionRadius,out clamped[i]),"needle clamp");
        Check(BallShapeMath.TryCollectProtrudingNeedles(clamped,clamped.Length,collisionRadius,compact,out int n),"needle selection");
        return new {n=Items.Count,diameterM=diameter,collisionRadiusM=collisionRadius,confirmedGrowthMm3=confirmed,
            proxyItemCount=proxyCount,needleCapM=BallShapeMath.NeedleProtrusionCap(collisionRadius),
            needleCount=n,rawNeedleSupports=supports.ToArray(),clampedNeedleSupports=clamped,
            items=Items.Select(x=>(JsonObject)x.DeepClone()).ToArray()};
    }
}

internal static class Program
{
    static int Main(string[] args)
    {
        try
        {
            if(args.Length<2)throw new ArgumentException("attach_sim <placement.v2.json> <output.json> [seeds CSV] [--ns=10,20,30,40] [--touch-attached | --stops=<stops.json>]");
            var level=JsonNode.Parse(File.ReadAllText(args[0]));
            float start=level["meta"]["ball"]["startDiameterM"].GetValue<float>();
            int[] seeds=(args.Length>2&&!args[2].StartsWith("--")?args[2]:"1,2,3,4,5").Split(',').Select(int.Parse).ToArray();
            bool touchAttached=args.Contains("--touch-attached");
            var stopsArg=args.FirstOrDefault(a=>a.StartsWith("--stops="));
            var stopsDoc=stopsArg==null?null:JsonNode.Parse(File.ReadAllText(stopsArg.Substring(8)));
            var nsArg=args.FirstOrDefault(a=>a.StartsWith("--ns="));
            var snapshotAt=(nsArg==null?"10,20,30,40":nsArg.Substring(5)).Split(',').Select(int.Parse).ToHashSet();
            var runs=new List<object>();
            foreach(int seed in seeds)
            {
                var sim=new Simulation(seed,start,touchAttached,stopsDoc?[seed.ToString()]?.AsArray().Select(x=>x.GetValue<float>()).ToArray());
                var remaining=level["props"].AsArray().Select(x=>(JsonObject)x.DeepClone()).ToList();
                var snapshots=new List<object>();
                for(int n=1;n<=snapshotAt.Max();n++) { sim.AbsorbOne(remaining);if(snapshotAt.Contains(n))snapshots.Add(sim.Snapshot()); }
                runs.Add(new {seed,startDiameterM=sim.StartDiameter,snapshots,events=sim.Events});
                Console.WriteLine($"seed={seed} committed={sim.Items.Count}");
            }
            var result=new {evaluator="linked runtime C#; UnityEngine managed value math",sampling="without replacement, uniform over currently eligible placement instances",contact=stopsDoc!=null?"collision sphere OR already-attached item meshes (hypothetical control, stops solved on triangle meshes)":touchAttached?"collision sphere OR already-attached item boxes (hypothetical control)":"collision sphere (runtime gate)",approach="uniform sphere direction; Haar SO(3) orientation; straight 1 mm steps, contact crossing refined by runtime predicate",ball="stationary at origin, no roll",runs};
            File.WriteAllText(args[1],JsonSerializer.Serialize(result,new JsonSerializerOptions{WriteIndented=true})+"\n");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
