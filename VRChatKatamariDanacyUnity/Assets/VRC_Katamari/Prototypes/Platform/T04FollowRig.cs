using UdonSharp;
using UnityEngine;

// Prototype reference rig intentionally remains stationary. It is not parented
// to the rolling visual and must not chase/write the local player's transform.
public class T04FollowRig : UdonSharpBehaviour
{
    public Transform target; // Builder reference retained; no transform-follow behavior.
}
