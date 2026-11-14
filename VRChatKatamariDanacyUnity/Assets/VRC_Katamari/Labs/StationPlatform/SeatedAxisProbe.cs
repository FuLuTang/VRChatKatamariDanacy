using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.Udon.Common;
using VRCKatamari.Riding;

/// <summary>
/// Lab probe for the first playable plan's risk 1: does VRChat still deliver the
/// move axes to Udon while the local player is sitting in a station?
/// </summary>
/// <remarks>
/// <para>
/// One Build &amp; Test answers it. Put this component on its own GameObject: the
/// project convention keeps one behaviour per object (clear ownership, a readable
/// self-check; sharing is legal, the engine does not disable a second behaviour on
/// the same object - see Docs/Design/BallMotor/README.md, assembly constraints),
/// and the station object is already taken by <c>BallRideStation</c>. Drag the scene's
/// <c>BallRideStation</c> into <c>ride</c>, sit in the ball, hold W and then A/D,
/// and read the client log for lines starting with <c>[seated-axis]</c>. A line
/// with <c>seated=True</c> and a non-zero <c>value</c> means the axes do arrive
/// while seated; lines only with <c>seated=False</c> mean they stop arriving, which
/// is the failure the plan needs to know about.
/// </para>
/// <para>
/// It tracks the seat through <see cref="BallRideStation.IsLocalPlayerSeated"/>
/// instead of its own station callbacks, because those callbacks only reach the
/// behaviour on the station's GameObject and that object already carries
/// <c>BallRideStation</c>.
/// </para>
/// <para>
/// Logging is bounded: a line on every change of value plus at most one line per
/// axis every 0.25 s while it stays off zero. The desktop keyboard only sends
/// -1/0/1, so in practice that is one line per keypress, while an analog stick
/// cannot flood the log. Jump is a button event and is logged every time.
/// </para>
/// <para>
/// This is not product code and must stay removable: nothing else references this
/// class, and it references nothing but the ride station. Delete this file, its
/// .meta and its component once the answer is written down.
/// </para>
/// </remarks>
[UdonBehaviourSyncMode(BehaviourSyncMode.None)]
public class SeatedAxisProbe : UdonSharpBehaviour
{
    [SerializeField] private BallRideStation ride;

    // Index 0 = MoveV, 1 = MoveH. Arrays instead of two copies of the throttle.
    private float[] lastLogTime = new float[2];
    private float[] previousValue = new float[2];
    private bool[] hasPreviousValue = new bool[2];

    private void Start()
    {
        if (ride == null)
        {
            Debug.Log("[seated-axis] WARNING ride is not wired: every line will read seated=False");
            return;
        }
        Debug.Log("[seated-axis] probe ready");
    }

    public override void InputMoveVertical(float value, UdonInputEventArgs args)
    {
        if (!ShouldLog(0, value)) return;
        LogAxis("MoveV", value, args);
    }

    public override void InputMoveHorizontal(float value, UdonInputEventArgs args)
    {
        if (!ShouldLog(1, value)) return;
        LogAxis("MoveH", value, args);
    }

    public override void InputJump(bool value, UdonInputEventArgs args)
    {
        Debug.Log("[seated-axis] name=Jump value=" + value + " type=" + args.eventType +
            " seated=" + IsSeated() + " frame=" + Time.frameCount);
    }

    private bool IsSeated()
    {
        if (ride == null) return false;
        return ride.IsLocalPlayerSeated();
    }

    private bool ShouldLog(int index, float value)
    {
        bool first = !hasPreviousValue[index];
        bool changed = hasPreviousValue[index] && value != previousValue[index];
        previousValue[index] = value;
        hasPreviousValue[index] = true;
        if (first || changed)
        {
            lastLogTime[index] = Time.time;
            return true;
        }
        if (value != 0f && Time.time - lastLogTime[index] >= 0.25f)
        {
            lastLogTime[index] = Time.time;
            return true;
        }
        return false;
    }

    private void LogAxis(string axisName, float value, UdonInputEventArgs args)
    {
        Debug.Log("[seated-axis] name=" + axisName + " value=" + value + " type=" + args.eventType +
            " seated=" + IsSeated() + " frame=" + Time.frameCount);
    }
}
