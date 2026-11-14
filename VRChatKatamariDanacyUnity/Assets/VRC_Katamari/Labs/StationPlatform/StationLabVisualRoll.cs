using UdonSharp;
using UnityEngine;

public class StationLabVisualRoll : UdonSharpBehaviour
{
    private void Update()
    {
        transform.Rotate(Vector3.forward, 45f * Time.deltaTime, Space.Self);
    }
}
