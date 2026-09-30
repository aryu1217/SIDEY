using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sidey.Installer
{
    internal static class IncrementalPayloadProgram
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation;
            public System.Runtime.InteropServices.ComTypes.FILETIME Access;
            public System.Runtime.InteropServices.ComTypes.FILETIME Write;
            public uint Volume;
            public uint SizeHigh;
            public uint SizeLow;
            public uint Links;
            public uint IndexHigh;
            public uint IndexLow;
        }

        private static int Main(string[] arguments)
        {
            try
            {
                if (arguments.Length != 4 || arguments[0] != "--cache" || arguments[2] != "--staging")
                    throw new ArgumentException("Expected --cache <directory> --staging <directory>.");
                string cache = Path.GetFullPath(arguments[1]);
                string staging = Path.GetFullPath(arguments[3]);
                RequireOrdinaryDirectory(cache);
                RequireOrdinaryDirectory(staging);
                if (Directory.GetFileSystemEntries(staging).Length != 0)
                    throw new IOException("Staging directory must be empty.");
                HashSet<string> paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("Sidey.IncrementalFiles"))
                using (StreamReader reader = new StreamReader(resource, new UTF8Encoding(false, true)))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        string[] fields = line.Split('\t');
                        long size;
                        if (fields.Length != 3 || !IsHash(fields[0]) ||
                            !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out size) || size < 0)
                            throw new InvalidDataException("Invalid embedded file record.");
                        string relative = ValidateRelativePath(fields[2]);
                        if (!paths.Add(relative)) throw new InvalidDataException("Duplicate target path.");
                        string source = Path.Combine(cache, fields[0] + ".bin");
                        string target = Path.Combine(staging, relative);
                        RequireOrdinaryFile(source);
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        RequireOrdinaryDirectory(Path.GetDirectoryName(target));
                        // Deny writes/deletes while copying; verify the bytes actually written,
                        // rather than trusting a prior hash of the user-controlled cache.
                        using (FileStream input = OpenOrdinaryFile(source))
                        using (FileStream output = new FileStream(target, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                        using (SHA256 hash = SHA256.Create())
                        {
                            if (input.Length != size) throw new InvalidDataException("Cached file size differs.");
                            byte[] buffer = new byte[81920];
                            long copied = 0;
                            int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                            {
                                copied += count;
                                if (copied > size) throw new InvalidDataException("Cached file exceeds target size.");
                                output.Write(buffer, 0, count);
                            }
                            output.Flush();
                            output.Position = 0;
                            string actual = BitConverter.ToString(hash.ComputeHash(output)).Replace("-", "").ToLowerInvariant();
                            if (copied != size || actual != fields[0]) throw new InvalidDataException("Cached file hash differs.");
                        }
                    }
                }
                if (!paths.Contains("SIDEY.exe") || !paths.Contains(Path.Combine("Runtime", "SIDEY.Host.exe")) ||
                    !paths.Contains(Path.Combine("Runtime", "SIDEY.UninstallHelper.exe")))
                    throw new InvalidDataException("Required target files are missing.");
                return 0;
            }
            catch (Exception)
            {
                // Transaction rollback owns partial staging cleanup. Never alter cache or live install.
                return 1;
            }
        }

        private static bool IsHash(string value)
        {
            if (value.Length != 64) return false;
            foreach (char character in value)
                if (!(character >= '0' && character <= '9') && !(character >= 'a' && character <= 'f')) return false;
            return true;
        }

        private static string ValidateRelativePath(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 240 || path.IndexOf('\\') >= 0 || Path.IsPathRooted(path))
                throw new InvalidDataException("Unsafe target path.");
            foreach (string part in path.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".", StringComparison.Ordinal) ||
                    part.EndsWith(" ", StringComparison.Ordinal) || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                    throw new InvalidDataException("Unsafe target component.");
                string stem = part.Split('.')[0].ToUpperInvariant();
                if (stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" ||
                    stem == "CLOCK$" || stem == "CONIN$" || stem == "CONOUT$" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3])))
                    throw new InvalidDataException("Reserved target name.");
            }
            if (path.Equals("Uninstall.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Root uninstaller is generated by NSIS.");
            return path.Replace('/', Path.DirectorySeparatorChar);
        }

        private static void RequireOrdinaryDirectory(string path)
        {
            DirectoryInfo current = new DirectoryInfo(path);
            while (current != null)
            {
                if (!current.Exists || (current.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Missing or redirected directory.");
                current = current.Parent;
            }
        }

        private static void RequireOrdinaryFile(string path)
        {
            if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Missing or redirected cache file.");
        }

        private static FileStream OpenOrdinaryFile(string path)
        {
            // OPEN_REPARSE_POINT binds the check to the same handle that is read.
            // FILE_SHARE_READ prevents mutation or replacement during staging.
            SafeFileHandle handle = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            try
            {
                FileInformation information;
                if (handle.IsInvalid || !GetFileInformationByHandle(handle, out information) ||
                    (information.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory)) != 0)
                    throw new IOException("Cannot open an ordinary cache file.");
                return new FileStream(handle, FileAccess.Read);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
    }
}
