using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AndroidGameUnpacker
{
    internal static class MeshFormatConverter
    {
        private const uint ImportFlags = 0x2u | 0x8u | 0x20u | 0x40u | 0x800000u;

        [StructLayout(LayoutKind.Sequential)]
        private struct ExportFormatDescription
        {
            public IntPtr Id;
            public IntPtr Description;
            public IntPtr FileExtension;
        }

        [DllImport("assimp-vc143-mt.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern IntPtr aiImportFile([MarshalAs(UnmanagedType.LPStr)] string file, uint flags);

        [DllImport("assimp-vc143-mt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void aiReleaseImport(IntPtr scene);

        [DllImport("assimp-vc143-mt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr aiGetErrorString();

        [DllImport("assimp-vc143-mt.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        private static extern int aiExportScene(IntPtr scene, [MarshalAs(UnmanagedType.LPStr)] string formatId, [MarshalAs(UnmanagedType.LPStr)] string file, uint preprocessing);

        [DllImport("assimp-vc143-mt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr aiGetExportFormatCount();

        [DllImport("assimp-vc143-mt.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr aiGetExportFormatDescription(UIntPtr index);

        private static int Main(string[] args)
        {
            try
            {
                if (args.Length == 1 && args[0] == "--list") return ListFormats();
                if (args.Length != 4 || args[0] != "--batch")
                {
                    Console.Error.WriteLine("Usage: MeshFormatConverter --batch <obj-root> <output-root> <fbx,gltf>");
                    Console.Error.WriteLine("       MeshFormatConverter --list");
                    return 2;
                }
                return ConvertBatch(args[1], args[2], args[3]);
            }
            catch (DllNotFoundException ex)
            {
                Console.Error.WriteLine("Assimp DLL missing: " + ex.Message);
                return 3;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex);
                return 1;
            }
        }

        private static int ListFormats()
        {
            ulong count = aiGetExportFormatCount().ToUInt64();
            for (ulong i = 0; i < count; i++)
            {
                IntPtr pointer = aiGetExportFormatDescription(new UIntPtr(i));
                if (pointer == IntPtr.Zero) continue;
                ExportFormatDescription item = (ExportFormatDescription)Marshal.PtrToStructure(pointer, typeof(ExportFormatDescription));
                Console.WriteLine("{0}\t{1}\t.{2}", Ansi(item.Id), Ansi(item.Description), Ansi(item.FileExtension));
            }
            return 0;
        }

        private static int ConvertBatch(string inputRoot, string outputRoot, string requested)
        {
            inputRoot = Path.GetFullPath(inputRoot).TrimEnd(Path.DirectorySeparatorChar);
            outputRoot = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar);
            if (!Directory.Exists(inputRoot)) throw new DirectoryNotFoundException(inputRoot);

            Dictionary<string, FormatTarget> formats = new Dictionary<string, FormatTarget>(StringComparer.OrdinalIgnoreCase)
            {
                { "fbx", new FormatTarget("fbx", "FBX", ".fbx") },
                { "gltf", new FormatTarget("gltf2", "GLTF", ".gltf") }
            };
            List<FormatTarget> targets = requested.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(delegate(string value) { return value.Trim(); })
                .Where(formats.ContainsKey)
                .Select(delegate(string value) { return formats[value]; })
                .Distinct(new TargetComparer()).ToList();
            if (targets.Count == 0) throw new ArgumentException("No supported output format requested.");

            string[] files = Directory.GetFiles(inputRoot, "*.obj", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Console.Error.WriteLine("No OBJ source files were found in " + inputRoot);
                return 5;
            }
            int converted = 0;
            int failed = 0;
            int processed = 0;
            int total = files.Length * targets.Count;
            foreach (string file in files)
            {
                IntPtr scene = aiImportFile(file, ImportFlags);
                if (scene == IntPtr.Zero)
                {
                    failed++;
                    Console.Error.WriteLine("[FAILED] {0}: {1}", file, Ansi(aiGetErrorString()));
                    continue;
                }
                try
                {
                    string relative = file.Substring(inputRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                    string relativeWithoutExtension = Path.ChangeExtension(relative, null);
                    foreach (FormatTarget target in targets)
                    {
                        string output = Path.Combine(outputRoot, target.Folder, relativeWithoutExtension + target.Extension);
                        Directory.CreateDirectory(Path.GetDirectoryName(output));
                        int result = aiExportScene(scene, target.Id, output, 0);
                        if (result == 0)
                        {
                            converted++;
                            Console.WriteLine("[OK] {0}", output);
                        }
                        else
                        {
                            failed++;
                            Console.Error.WriteLine("[FAILED] {0}: {1}", output, Ansi(aiGetErrorString()));
                        }
                        processed++;
                        Console.WriteLine("[{0:000}%]", (int)Math.Floor(processed * 100.0 / total));
                    }
                }
                finally { aiReleaseImport(scene); }
            }
            Console.WriteLine("SUMMARY source={0} converted={1} failed={2}", files.Length, converted, failed);
            return failed == 0 ? 0 : 4;
        }

        private static string Ansi(IntPtr value)
        {
            return value == IntPtr.Zero ? "" : Marshal.PtrToStringAnsi(value);
        }

        private sealed class FormatTarget
        {
            public readonly string Id;
            public readonly string Folder;
            public readonly string Extension;
            public FormatTarget(string id, string folder, string extension) { Id = id; Folder = folder; Extension = extension; }
        }

        private sealed class TargetComparer : IEqualityComparer<FormatTarget>
        {
            public bool Equals(FormatTarget x, FormatTarget y) { return string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase); }
            public int GetHashCode(FormatTarget value) { return value.Id.ToLowerInvariant().GetHashCode(); }
        }
    }
}
