using UdonSharp;
using UnityEngine;

public class StationLabExitButton : UdonSharpBehaviour
{
    public StationLabDriver driver;

    public override void Interact()
    {
        if (driver != null)
        {
            driver.LeaveStation();
        }
    }
}
