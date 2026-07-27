namespace CelesteBenchmark
{
    public interface IVirtualInput
    {
        float MoveX { get; }
        float MoveY { get; }
        bool JumpHeld { get; }
        bool JumpPressedEdge { get; }
        bool JumpReleasedEdge { get; }
        bool DashPressedEdge { get; }
        bool ClimbHeld { get; }
    }
}
