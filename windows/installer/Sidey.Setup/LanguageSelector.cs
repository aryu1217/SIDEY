using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Sidey.Installer
{
    internal static class LanguageSelector
    {
        private const uint InitializeDialogMessage = 0x0110;
        private const uint CommandMessage = 0x0111;
        private const uint SetIconMessage = 0x0170;
        private const uint GetCurrentSelectionMessage = 0x0147;
        private const uint InsertStringMessage = 0x014a;
        private const uint SetCurrentSelectionMessage = 0x014e;
        private const int DialogIconControl = 1008;
        private const int DialogPromptControl = 1007;
        private const int LanguageListControl = 1002;
        private const int AcceptCommand = 1;
        private const int CancelCommand = 2;

        private delegate IntPtr DialogProcedure(
            IntPtr window,
            uint message,
            IntPtr wordParameter,
            IntPtr longParameter);

        [STAThread]
        private static int Main(string[] arguments)
        {
            try
            {
                if (Array.IndexOf(arguments, "--update-launch-warning") >= 0)
                {
                    MessageBox(
                        IntPtr.Zero,
                        InstallerLanguages.UpdateLaunchWarning(GetUserDefaultUILanguage()),
                        "SIDEY",
                        0x00010030); // MB_SETFOREGROUND | MB_ICONWARNING | MB_OK
                    return 2;
                }

                if (Array.IndexOf(arguments, "--activate-existing") >= 0)
                {
                    return InstallerWindowActivation.TryActivateExisting() ? 0 : 1;
                }

                int savedLanguage = 0;
                if (arguments.Length > 0)
                {
                    int.TryParse(arguments[0], out savedLanguage);
                }

                int systemLanguage = GetUserDefaultUILanguage();
                int selectedLanguage = InstallerLanguages.DefaultSelection(systemLanguage, savedLanguage);
                if (Array.IndexOf(arguments, "--silent") >= 0)
                {
                    return selectedLanguage;
                }

                uint installerProcess = 0;
                if (arguments.Length > 1)
                {
                    uint.TryParse(arguments[1], out installerProcess);
                }

                InstallerLanguage[] languages = InstallerLanguages.Ordered(systemLanguage);
                IntPtr dialogWindow = IntPtr.Zero;
                using (Stream stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("Sidey.Installer.LanguageDialog"))
                using (BinaryReader reader = new BinaryReader(stream))
                using (Icon icon = Icon.ExtractAssociatedIcon(Process.GetCurrentProcess().MainModule.FileName))
                {
                    byte[] bytes = reader.ReadBytes(checked((int)stream.Length));
                    IntPtr template = Marshal.AllocHGlobal(bytes.Length);
                    try
                    {
                        Marshal.Copy(bytes, 0, template, bytes.Length);
                        DialogProcedure callback = delegate(
                            IntPtr window,
                            uint message,
                            IntPtr wordParameter,
                            IntPtr longParameter)
                        {
                            if (message == InitializeDialogMessage)
                            {
                                dialogWindow = window;
                                InstallerWindowActivation.MarkWindow(window);
                                SetWindowText(window, "Installer Language");
                                SetDlgItemText(window, DialogPromptControl, "Please select a language.");
                                SendDlgItemMessage(
                                    window,
                                    DialogIconControl,
                                    SetIconMessage,
                                    icon.Handle,
                                    IntPtr.Zero);
                                for (int index = 0; index < languages.Length; index++)
                                {
                                    // CB_INSERTSTRING preserves our order even though the
                                    // original NSIS template has the CBS_SORT style.
                                    InsertLanguage(
                                        window,
                                        LanguageListControl,
                                        InsertStringMessage,
                                        (IntPtr)index,
                                        languages[index].DisplayName);
                                    if (languages[index].Id == selectedLanguage)
                                    {
                                        SendDlgItemMessage(
                                            window,
                                            LanguageListControl,
                                            SetCurrentSelectionMessage,
                                            (IntPtr)index,
                                            IntPtr.Zero);
                                    }
                                }

                                return (IntPtr)1;
                            }

                            if (message == CommandMessage)
                            {
                                int command = (int)(wordParameter.ToInt64() & 0xffff);
                                if (command == AcceptCommand)
                                {
                                    int index = SendDlgItemMessage(
                                        window,
                                        LanguageListControl,
                                        GetCurrentSelectionMessage,
                                        IntPtr.Zero,
                                        IntPtr.Zero).ToInt32();
                                    if (installerProcess != 0)
                                    {
                                        AllowSetForegroundWindow(installerProcess);
                                    }

                                    int selectionResult = index >= 0 && index < languages.Length
                                        ? languages[index].Id
                                        : 1;
                                    EndDialog(window, (IntPtr)selectionResult);
                                    return (IntPtr)1;
                                }

                                if (command == CancelCommand)
                                {
                                    EndDialog(window, IntPtr.Zero);
                                    return (IntPtr)1;
                                }
                            }

                            return IntPtr.Zero;
                        };
                        int dialogResult = DialogBoxIndirectParam(
                            GetModuleHandle(null),
                            template,
                            IntPtr.Zero,
                            callback,
                            IntPtr.Zero).ToInt32();
                        GC.KeepAlive(callback);
                        return dialogResult == -1 ? 1 : dialogResult;
                    }
                    finally
                    {
                        InstallerWindowActivation.UnmarkWindow(dialogWindow);
                        Marshal.FreeHGlobal(template);
                    }
                }
            }
            catch
            {
                return 1;
            }
        }

        [DllImport("kernel32.dll")]
        private static extern ushort GetUserDefaultUILanguage();

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "MessageBoxW")]
        private static extern int MessageBox(IntPtr window, string text, string caption, uint type);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr GetModuleHandle(string name);

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(uint processId);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr DialogBoxIndirectParam(
            IntPtr instance,
            IntPtr template,
            IntPtr parent,
            DialogProcedure callback,
            IntPtr parameter);

        [DllImport("user32.dll")]
        private static extern bool EndDialog(IntPtr window, IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowText(IntPtr window, string text);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetDlgItemText(IntPtr window, int item, string text);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendDlgItemMessage(
            IntPtr window,
            int item,
            uint message,
            IntPtr wordParameter,
            IntPtr longParameter);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendDlgItemMessageW")]
        private static extern IntPtr InsertLanguage(
            IntPtr window,
            int item,
            uint message,
            IntPtr index,
            string text);
    }
}
