using UdonSharp;
using UnityEngine;

public class T04StationControl : UdonSharpBehaviour
{
    public T04PlatformDriver driver;

    public override void Interact()
    {
        if (driver != null)
        {
            driver.LeaveStation();
        }
    }
}
