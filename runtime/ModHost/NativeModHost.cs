using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using UnityEngine;

namespace NativeH3VR
{
    internal static class NativeModHost
    {
        private const int SchemaVersion = 2;
        private const string DirectoryVariable = "NATIVEH3VR_MODS_DIR";
        private const string ManifestName = "native-mod.json";
        private static readonly Regex PackageId = new Regex(@"\A[a-z0-9][a-z0-9._-]{0,63}\z");
        private static readonly Regex AssemblyFile = new Regex(@"\A[A-Za-z0-9_][A-Za-z0-9._-]*\.dll\z");
        private static readonly Regex Sha256 = new Regex(@"\A[0-9a-f]{64}\z");

        [Serializable]
        private sealed class Manifest
        {
            public int schemaVersion = 0;
            public string id = null;
            public string unityVersion = null;
            public string buildTarget = null;
            public string hostBuildSha256 = null;
            public AssemblyEntry[] assemblies = null;
            public EntryPoint[] entryPoints = null;
        }

        [Serializable]
        private sealed class AssemblyEntry
        {
            public string file = null;
            public string sha256 = null;
        }

        [Serializable]
        private sealed class EntryPoint
        {
            public string assembly = null;
            public string type = null;
            public string method = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        private static void LoadInstalledMods()
        {
            if (Application.platform != RuntimePlatform.OSXPlayer) return;
            string selected = Environment.GetEnvironmentVariable(DirectoryVariable);
            if (string.IsNullOrEmpty(selected)) return;
            try
            {
                if (!Path.IsPathRooted(selected))
                    throw new InvalidDataException("The external mod directory must be an absolute path.");
                string root = Path.GetFullPath(selected);
                string contents = Path.GetFullPath(Application.dataPath);
                string app = Directory.GetParent(contents).FullName;
                if (Contains(app, root) || Contains(root, app) || !Directory.Exists(root))
                    throw new InvalidDataException("Choose an existing external directory separate from the player app.");
                for (var ancestor = new DirectoryInfo(root); ancestor != null; ancestor = ancestor.Parent)
                    RequirePhysicalPath(ancestor.FullName);

                string buildReceipt = Path.Combine(contents, "Resources", "nativeh3vr-build.json");
                RequirePhysicalPath(buildReceipt);
                string buildSha256 = FileHash(buildReceipt);
                foreach (string package in Directory.GetDirectories(root).OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (!Matches(PackageId, Path.GetFileName(package)) || !File.Exists(Path.Combine(package, ManifestName)))
                        continue;
                    try
                    {
                        LoadPackage(package, buildSha256);
                    }
                    catch (Exception error)
                    {
                        Debug.LogError($"NATIVEH3VR_MOD_FAILED id={Path.GetFileName(package)} {Unwrap(error)}");
                    }
                }
            }
            catch (Exception error)
            {
                Debug.LogError($"NATIVEH3VR_MOD_FAILED directory={selected} {error}");
            }
        }

        private static void LoadPackage(string package, string buildSha256)
        {
            RequirePhysicalPath(package);
            string manifestPath = Path.Combine(package, ManifestName);
            RequirePhysicalPath(manifestPath);
            Manifest manifest = JsonUtility.FromJson<Manifest>(File.ReadAllText(manifestPath));
            if (manifest == null || manifest.schemaVersion != SchemaVersion
                || !Matches(PackageId, manifest.id) || manifest.id != Path.GetFileName(package)
                || manifest.unityVersion != Application.unityVersion || manifest.buildTarget != "StandaloneOSX"
                || manifest.hostBuildSha256 != buildSha256
                || manifest.assemblies == null || manifest.assemblies.Length == 0 || manifest.entryPoints == null)
                throw new InvalidDataException("Invalid or incompatible mod manifest.");

            var filenames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyEntry entry in manifest.assemblies)
            {
                if (entry == null || !Matches(AssemblyFile, entry.file) || !Matches(Sha256, entry.sha256)
                    || !filenames.Add(entry.file))
                    throw new InvalidDataException("Invalid or duplicate mod assembly entry.");
                string path = Path.Combine(package, entry.file);
                RequirePhysicalPath(path);
                if (!StringComparer.OrdinalIgnoreCase.Equals(AssemblyName.GetAssemblyName(path).Name,
                        Path.GetFileNameWithoutExtension(entry.file)) || FileHash(path) != entry.sha256)
                    throw new InvalidDataException($"Assembly identity or hash mismatch for {entry.file}.");
            }
            foreach (EntryPoint entry in manifest.entryPoints)
                if (entry == null || entry.assembly == null || !filenames.Contains(entry.assembly)
                    || string.IsNullOrWhiteSpace(entry.type) || string.IsNullOrWhiteSpace(entry.method))
                    throw new InvalidDataException("Invalid mod entry point.");

            var assemblies = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyEntry entry in manifest.assemblies)
                assemblies.Add(entry.file, LoadAssembly(Path.Combine(package, entry.file), entry.sha256));
            var methods = new List<MethodInfo>();
            foreach (EntryPoint entry in manifest.entryPoints)
            {
                Type type = assemblies[entry.assembly].GetType(entry.type, throwOnError: true);
                MethodInfo method = type.GetMethod(entry.method, BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
                    binder: null, types: Type.EmptyTypes, modifiers: null);
                if (method == null || method.ReturnType != typeof(void) || method.ContainsGenericParameters)
                    throw new MissingMethodException(entry.type, entry.method + " (public static void, no arguments)");
                methods.Add(method);
            }
            foreach (MethodInfo method in methods)
                method.Invoke(null, null);

            Debug.Log($"NATIVEH3VR_MOD_READY id={manifest.id} assemblies={string.Join(",", manifest.assemblies.Select(entry => entry.file))}");
        }

        private static Assembly LoadAssembly(string path, string sha256)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(
                candidate => StringComparer.OrdinalIgnoreCase.Equals(candidate.GetName().Name, name));
            if (assembly == null)
                assembly = Assembly.LoadFrom(path);
            if (assembly.IsDynamic || string.IsNullOrEmpty(assembly.Location)
                || FileHash(assembly.Location) != sha256)
                throw new InvalidDataException($"Conflicting loaded assembly {name}.");
            return assembly;
        }

        private static bool Matches(Regex pattern, string value) => value != null && pattern.IsMatch(value);

        private static bool Contains(string parent, string path)
        {
            var directory = parent.TrimEnd(Path.DirectorySeparatorChar);
            var candidate = path.TrimEnd(Path.DirectorySeparatorChar);
            return candidate.Equals(directory, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static void RequirePhysicalPath(string path)
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Mod paths cannot be symbolic links: {path}");
        }

        private static string FileHash(string path)
        {
            using (var file = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "").ToLowerInvariant();
        }

        private static Exception Unwrap(Exception error) =>
            error is TargetInvocationException invocation && invocation.InnerException != null ? invocation.InnerException : error;
    }
}
