using UdonSharp;
using UnityEngine;

public class T04VisualRoll : UdonSharpBehaviour
{
    private void Update()
    {
        transform.Rotate(Vector3.forward, 45f * Time.deltaTime, Space.Self);
    }
}
