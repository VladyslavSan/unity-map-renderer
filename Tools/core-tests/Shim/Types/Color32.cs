namespace UnityEngine
{
    // UMR-176: SnapshotCoverage.cs (engine-free by design, compiled into this dotnet project) reads
    // pixels as UnityEngine.Color32 instead of a flat byte[]. Mirrors the real Color32's byte-per-channel
    // layout — just the fields the shared coverage code touches — so that source compiles identically
    // under both runners, the same pattern as the Unity.Mathematics shims beside this file.
    public struct Color32
    {
        public byte r;
        public byte g;
        public byte b;
        public byte a;

        public Color32(byte r, byte g, byte b, byte a)
        {
            this.r = r; this.g = g; this.b = b; this.a = a;
        }
    }
}
