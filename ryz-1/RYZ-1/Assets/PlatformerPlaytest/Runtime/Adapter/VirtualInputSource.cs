using CelesteBenchmark;

namespace PlatformerPlaytest
{
    /// <summary>Settable IVirtualInput implementation the adapter writes into each tick. Edges are single-tick pulses.</summary>
    sealed class VirtualInputSource : IVirtualInput
    {
        public float MoveX { get; set; }
        public float MoveY { get; set; }
        public bool JumpHeld { get; set; }
        public bool JumpPressedEdge { get; set; }
        public bool JumpReleasedEdge { get; set; }
        public bool DashPressedEdge { get; set; }
        public bool ClimbHeld { get; set; }

        public void ClearEdges()
        {
            JumpPressedEdge = false;
            JumpReleasedEdge = false;
            DashPressedEdge = false;
        }
    }
}
