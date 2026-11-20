namespace VRCKatamari.Pickup
{
    public static class AreaCollisionMath
    {
        public static bool CollidesInArea(int hitOnAreaNo, int levelAreaNo)
        {
            return hitOnAreaNo == -1 || hitOnAreaNo == levelAreaNo;
        }
    }
}
