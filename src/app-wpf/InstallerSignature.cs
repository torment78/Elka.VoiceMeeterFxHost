using System.IO;
using System.Runtime.InteropServices;

namespace Elka.VoiceMeeterFxHost.App;

internal static class InstallerSignature
{
    public static void Verify(string path)
    {
        var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
        var pathPointer = Marshal.StringToCoTaskMemUni(path);
        var filePointer = Marshal.AllocHGlobal(Marshal.SizeOf<TrustFile>());
        var data = new TrustData
        {
            Size = (uint)Marshal.SizeOf<TrustData>(),
            UiChoice = 2,
            UnionChoice = 1,
            File = filePointer,
            StateAction = 1,
            ProviderFlags = 0x80,
            UiContext = 1
        };
        try
        {
            Marshal.StructureToPtr(new TrustFile
            {
                Size = (uint)Marshal.SizeOf<TrustFile>(),
                Path = pathPointer
            }, filePointer, false);
            var result = WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            if (result != 0)
                throw new InvalidDataException($"The stable installer signature could not be verified (0x{result:X8}). Installation was stopped.");
        }
        finally
        {
            data.StateAction = 2;
            WinVerifyTrust(new IntPtr(-1), ref action, ref data);
            Marshal.FreeHGlobal(filePointer);
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref TrustData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustFile
    {
        public uint Size;
        public IntPtr Path;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public IntPtr File;
        public uint StateAction;
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }
}
