using System.Runtime.InteropServices;
using System.Text;

namespace AbyssRunner.Input;

internal static class InterceptionNative
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyStroke
    {
        public ushort Code;
        public ushort State;
        public uint Information;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseStroke
    {
        public ushort State;
        public ushort Flags;
        public short Rolling;
        public int X;
        public int Y;
        public uint Information;
    }

    internal const ushort KeyDown = 0x00;
    internal const ushort KeyUp = 0x01;
    internal const ushort MouseLeftDown = 0x001;
    internal const ushort MouseLeftUp = 0x002;
    internal const ushort MouseMoveAbsolute = 0x001;
    internal const ushort MouseVirtualDesktop = 0x002;

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "interception_create_context")]
    internal static extern nint CreateContext();

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "interception_destroy_context")]
    internal static extern void DestroyContext(nint context);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "interception_send")]
    internal static extern int SendKey(nint context, int device, ref KeyStroke stroke, uint nstroke);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl, EntryPoint = "interception_send")]
    internal static extern int SendMouse(nint context, int device, ref MouseStroke stroke, uint nstroke);

    [DllImport("interception.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode, EntryPoint = "interception_get_hardware_id")]
    internal static extern uint GetHardwareId(nint context, int device, StringBuilder hardwareId, uint size);
}
