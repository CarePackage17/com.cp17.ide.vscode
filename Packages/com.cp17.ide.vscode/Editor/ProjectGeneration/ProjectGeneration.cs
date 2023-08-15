using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SR = System.Reflection;
using System.Text;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using Unity.Profiling;
using Unity.Jobs;
using Unity.Collections;
using UnityEditor.PackageManager;
using Unity.Collections.LowLevel.Unsafe;
using System.Runtime.InteropServices;

namespace VSCodeEditor
{
    public interface IGenerator
    {
        bool SyncIfNeeded(List<string> affectedFiles, string[] reimportedFiles);
        void Sync();
        string SolutionFile();
        string ProjectDirectory { get; }
        IAssemblyNameProvider AssemblyNameProvider { get; }
        void GenerateAll(bool generateAll);
        bool SolutionExists();
    }

    public class ProjectGeneration : IGenerator
    {
        static readonly ProfilerMarker s_jobifiedSyncMarker = new($"{nameof(VSCodeEditor)}.{nameof(ProjectGeneration)}.{nameof(JobifiedSync)}");
        static readonly ProfilerMarker s_excludedAssemblyMarker = new($"{nameof(GetExcludedAssemblies)}");
        static readonly ProfilerMarker s_getDataMarker = new("GetDataFromUnity");
        static readonly ProfilerMarker s_setupJobsMarker = new("SetupJobs");
        static readonly ProfilerMarker s_completeAndDisposeMarker = new("CompleteAndDisposeAllTheThings");
        static readonly ProfilerMarker s_slnGenMarker = new("SlnGeneration");

        //These don't change at runtime, so we can cache them once and use them forever.
        //We could even use a ScriptableObject so it survives domain reloads if we want...
        static readonly string[] s_netStandardAssemblyDirectories = CompilationPipeline.GetSystemAssemblyDirectories(ApiCompatibilityLevel.NET_Standard);
        static readonly string[] s_net48AssemblyDirectories = CompilationPipeline.GetSystemAssemblyDirectories(ApiCompatibilityLevel.NET_Unity_4_8);

        enum ScriptingLanguage
        {
            None,
            CSharp
        }

        const string k_SettingsJson = @"{
    ""files.exclude"":
    {
        ""**/.DS_Store"":true,
        ""**/.git"":true,
        ""**/.gitmodules"":true,
        ""**/*.pidb"":true,
        ""**/*.suo"":true,
        ""**/*.user"":true,
        ""**/*.userprefs"":true,
        ""**/*.unityproj"":true,
        ""**/*.dll"":true,
        ""**/*.exe"":true,
        ""**/*.pdf"":true,
        ""**/*.mid"":true,
        ""**/*.midi"":true,
        ""**/*.wav"":true,
        ""**/*.gif"":true,
        ""**/*.ico"":true,
        ""**/*.jpg"":true,
        ""**/*.jpeg"":true,
        ""**/*.png"":true,
        ""**/*.psd"":true,
        ""**/*.tga"":true,
        ""**/*.tif"":true,
        ""**/*.tiff"":true,
        ""**/*.3ds"":true,
        ""**/*.3DS"":true,
        ""**/*.fbx"":true,
        ""**/*.FBX"":true,
        ""**/*.lxo"":true,
        ""**/*.LXO"":true,
        ""**/*.ma"":true,
        ""**/*.MA"":true,
        ""**/*.obj"":true,
        ""**/*.OBJ"":true,
        ""**/*.cubemap"":true,
        ""**/*.flare"":true,
        ""**/*.mat"":true,
        ""**/*.meta"":true,
        ""**/*.prefab"":true,
        ""**/*.unity"":true,
        ""build/"":true,
        ""Build/"":true,
        ""Library/"":true,
        ""library/"":true,
        ""obj/"":true,
        ""Obj/"":true,
        ""ProjectSettings/"":true,
        ""temp/"":true,
        ""Temp/"":true
    }
}";
        public string ProjectDirectory { get; }
        IAssemblyNameProvider IGenerator.AssemblyNameProvider => m_AssemblyNameProvider;

        public void GenerateAll(bool generateAll)
        {
            m_AssemblyNameProvider.ToggleProjectGeneration(
                ProjectGenerationFlag.BuiltIn
                | ProjectGenerationFlag.Embedded
                | ProjectGenerationFlag.Git
                | ProjectGenerationFlag.Local
                | ProjectGenerationFlag.LocalTarBall
                | ProjectGenerationFlag.PlayerAssemblies
                | ProjectGenerationFlag.Registry
                | ProjectGenerationFlag.Unknown);
        }

        readonly string m_unityProjectName;
        readonly string m_scriptAssembliesPath;
        readonly IAssemblyNameProvider m_AssemblyNameProvider;
        readonly IFileIO m_FileIOProvider;
        readonly IGUIDGenerator m_GUIDProvider;

        public ProjectGeneration(string tempDirectory)
            : this(tempDirectory, new AssemblyNameProvider(), new FileIOProvider(), new GUIDProvider()) { }

        public ProjectGeneration(string tempDirectory, IAssemblyNameProvider assemblyNameProvider, IFileIO fileIO, IGUIDGenerator guidGenerator)
        {
            ProjectDirectory = tempDirectory.NormalizePath();
            m_scriptAssembliesPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library", "ScriptAssemblies")).Replace('\\', '/');
            m_unityProjectName = Path.GetFileName(ProjectDirectory);
            m_AssemblyNameProvider = assemblyNameProvider;
            m_FileIOProvider = fileIO;
            m_GUIDProvider = guidGenerator;
        }

        /// <summary>
        /// Syncs the scripting solution if any affected files are relevant.
        /// </summary>
        /// <returns>
        /// Whether the solution was synced.
        /// </returns>
        /// <param name='affectedFiles'>
        /// A set of files whose status has changed
        /// </param>
        /// <param name="reimportedFiles">
        /// A set of files that got reimported
        /// </param>
        public bool SyncIfNeeded(List<string> affectedFiles, string[] reimportedFiles)
        {
            if (affectedFiles.Count > 0 || reimportedFiles.Length > 0)
            {
                JobifiedSync();
            }

            //TODO: restore this after figuring out how Unity calls stuff
            // Profiler.BeginSample("SolutionSynchronizerSync");
            // SetupProjectSupportedExtensions();

            // if (!HasFilesBeenModified(affectedFiles, reimportedFiles))
            // {
            //     Profiler.EndSample();
            //     return false;
            // }

            // var assemblies = m_AssemblyNameProvider.GetAssemblies(ShouldFileBePartOfSolution);
            // var allProjectAssemblies = assemblies.ToList();
            // SyncSolution(allProjectAssemblies);

            // var allAssetProjectParts = GenerateAllAssetProjectParts();

            // var affectedNames = affectedFiles.Select(asset => m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset)).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Split(new[] { ".dll" }, StringSplitOptions.RemoveEmptyEntries)[0]);
            // var reimportedNames = reimportedFiles.Select(asset => m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset)).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Split(new[] { ".dll" }, StringSplitOptions.RemoveEmptyEntries)[0]);
            // var affectedAndReimported = new HashSet<string>(affectedNames.Concat(reimportedNames));

            // foreach (var assembly in allProjectAssemblies)
            // {
            //     if (!affectedAndReimported.Contains(assembly.name))
            //         continue;

            //     SyncProject(assembly, allAssetProjectParts, ParseResponseFileData(assembly));
            // }

            // Profiler.EndSample();

            return true;
        }

        // bool HasFilesBeenModified(List<string> affectedFiles, string[] reimportedFiles)
        // {
        //     return affectedFiles.Any(ShouldFileBePartOfSolution) || reimportedFiles.Any(ShouldSyncOnReimportedAsset);
        // }

        // static bool ShouldSyncOnReimportedAsset(string asset)
        // {
        //     ReadOnlySpan<char> extension = Path.GetExtension(asset.AsSpan());

        //     if (extension.Equals(".dll".AsSpan(), StringComparison.InvariantCultureIgnoreCase) ||
        //         extension.Equals(".asmdef".AsSpan(), StringComparison.InvariantCultureIgnoreCase))
        //     {
        //         return true;
        //     }

        //     return false;
        // }

        static IEnumerable<SR.MethodInfo> GetPostProcessorCallbacks(string name)
        {
            return TypeCache
                .GetTypesDerivedFrom<AssetPostprocessor>()
                .Select(t => t.GetMethod(name, SR.BindingFlags.Public | SR.BindingFlags.NonPublic | SR.BindingFlags.Static))
                .Where(m => m != null);
        }

        // static void OnGeneratedCSProjectFiles()
        // {
        //     foreach (var method in GetPostProcessorCallbacks(nameof(OnGeneratedCSProjectFiles)))
        //     {
        //         method.Invoke(null, Array.Empty<object>());
        //     }
        // }

        static string InvokeAssetPostProcessorGenerationCallbacks(string name, string path, string content)
        {
            foreach (var method in GetPostProcessorCallbacks(name))
            {
                var args = new[] { path, content };
                var returnValue = method.Invoke(null, args);
                if (method.ReturnType == typeof(string))
                {
                    // We want to chain content update between invocations
                    content = (string)returnValue;
                }
            }

            return content;
        }

        static string OnGeneratedCSProject(string path, string content)
        {
            return InvokeAssetPostProcessorGenerationCallbacks(nameof(OnGeneratedCSProject), path, content);
        }

        static string OnGeneratedSlnSolution(string path, string content)
        {
            return InvokeAssetPostProcessorGenerationCallbacks(nameof(OnGeneratedSlnSolution), path, content);
        }

        static bool IsAssemblyIncluded(PackageSource source, ProjectGenerationFlag currentSetting)
        {
            return source switch
            {
                PackageSource.BuiltIn => currentSetting.HasFlag(ProjectGenerationFlag.BuiltIn),
                PackageSource.Embedded => currentSetting.HasFlag(ProjectGenerationFlag.Embedded),
                PackageSource.Git => currentSetting.HasFlag(ProjectGenerationFlag.Git),
                PackageSource.Local => currentSetting.HasFlag(ProjectGenerationFlag.Local),
                PackageSource.LocalTarball => currentSetting.HasFlag(ProjectGenerationFlag.LocalTarBall),
                PackageSource.Registry => currentSetting.HasFlag(ProjectGenerationFlag.Registry),
                PackageSource.Unknown => currentSetting.HasFlag(ProjectGenerationFlag.Unknown),
                _ => false
            };
        }

        public void Sync()
        {
            JobifiedSync();
        }

        void GetExcludedAssemblies(Assembly[] assemblies, NativeParallelHashSet<FixedString4096Bytes> excludedAssemblies)
        {
            s_excludedAssemblyMarker.Begin();

            ProjectGenerationFlag settings = (ProjectGenerationFlag)EditorPrefs.GetInt(VSCodeEditor.CsprojGenerationSettingsKey, 0);

            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                //Check settings if we should do anything at all for this assembly.
                //I wonder if it's enough to just check the first source file path to see if it's in a package;
                //I mean you can't have source files outside the package dir, can you? (what about asmref?)
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assembly.sourceFiles[0]);
                if (packageInfo != null)
                {
                    PackageSource source = packageInfo.source;

                    if (!IsAssemblyIncluded(source, settings))
                    {
                        excludedAssemblies.Add(new(assembly.name));
                    }
                }
            }

            s_excludedAssemblyMarker.End();
        }

        void JobifiedSync()
        {
            s_jobifiedSyncMarker.Begin();

            string[] systemReferenceDirs;
            Assembly[] assemblies;
            //ScriptAssemblies folder is necessary for Unity-built assemblies that do not have projects
            //generated for them (excluded by user setting).
            FixedString4096Bytes scriptAssembliesPathFixed = new(m_scriptAssembliesPath);

            using (s_getDataMarker.Auto())
            {
                //This generates a lot of garbage, but it's the only way to get this data as of 2021 LTS.
                var editorAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Editor);

                ProjectGenerationFlag settings = (ProjectGenerationFlag)EditorPrefs.GetInt(VSCodeEditor.CsprojGenerationSettingsKey, 0);
                if (settings.HasFlag(ProjectGenerationFlag.PlayerAssemblies))
                {
                    var playerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);

                    for (int i = 0; i < playerAssemblies.Length; i++)
                    {
                        //here's how rider does this:
                        //https://github.com/needle-mirror/com.unity.ide.rider/blob/master/Rider/Editor/ProjectGeneration/AssemblyNameProvider.cs#L56

                        //we can't do the name change like this because private...
                        //but I guess we could do it in the generate job maybe? either that or use the ctor with copies of most
                        //things (generating lots of garbage, but I guess it's easier for now? usage of assembly.name would need a
                        //cleanup...)
                        // ass.name = 
                        //TODO: We might wanna also change outputPath like rider does? needs investigation.
                        Assembly ass = playerAssemblies[i];
                        playerAssemblies[i] = new($"{ass.name}.Player", ass.outputPath, ass.sourceFiles, ass.defines,
                            ass.assemblyReferences, ass.compiledAssemblyReferences, ass.flags);
                    }

                    assemblies = new Assembly[editorAssemblies.Length + playerAssemblies.Length];
                    editorAssemblies.CopyTo(assemblies, 0);
                    playerAssemblies.CopyTo(assemblies, editorAssemblies.Length);
                }
                else
                {
                    assemblies = editorAssemblies;
                }
            }

            //We can definitely cache this too, user settings change doesn't happen often usually.
            NativeParallelHashSet<FixedString4096Bytes> excludedAssemblies = new(assemblies.Length, Allocator.TempJob);
            GetExcludedAssemblies(assemblies, excludedAssemblies);

            //So this sucks a bunch because of managed allocations, but we can't put GenerateProjectJob
            //in a NativeArray because it contains NativeArrays itself...
            //saving the JobHandles is not enough because we need to clean up all the NativeContainers
            //after all the jobs are done...theoretically there's Dispose jobs, but those are broken for NativeText in 1.4.0 and lower.
            List<(JobHandle, GenerateProjectJob)> jobList = new(assemblies.Length);

            NativeList<ProjectReference> projectsInSln = new(64, Allocator.TempJob);
            StringBuilder sb = new(capacity: 4096);
            sb.AppendLine("Excluded assembles:");

            s_setupJobsMarker.Begin();

            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];
                if (excludedAssemblies.Contains(new(assembly.name)))
                {
                    sb.AppendLine(assembly.name);
                    continue;
                }

                //Skip empty assemblies, they don't need a csproj
                if (assembly.sourceFiles.Length == 0) continue;

                ApiCompatibilityLevel apiCompatLevel = assembly.compilerOptions.ApiCompatibilityLevel;
                string projectGuid = ProjectGuid(assembly.name);

                FixedString4096Bytes assemblyNameUtf8 = new(assembly.name);
                projectsInSln.Add(new(assemblyNameUtf8, new FixedString64Bytes(projectGuid)));
                systemReferenceDirs = GetSystemAssemblyDirectories(apiCompatLevel);

                string[] csDefines = assembly.defines;
                string[] csSourceFiles = assembly.sourceFiles;
                string[] compiledAssemblyRefs = assembly.compiledAssemblyReferences;
                Assembly[] maybeAsmdefReferences = assembly.assemblyReferences;
                string langVersion = assembly.compilerOptions.LanguageVersion;
                bool unsafeCode = assembly.compilerOptions.AllowUnsafeCode;

                NativeList<UnsafeList<char>> definesUtf16 = new(256, Allocator.TempJob);
                NativeList<UnsafeList<char>> sourceFilesUtf16 = new(1024, Allocator.TempJob);
                NativeList<UnsafeList<char>> assemblyReferencePathsUtf16 = new(512, Allocator.TempJob);
                NativeList<UnsafeList<char>> extraAssemblyReferencePathsUtf16 = new(8, Allocator.TempJob);
                FixedString32Bytes nullableContext = new();
                NativeArray<ProjectReference> projectReferences = new(maybeAsmdefReferences.Length, Allocator.TempJob);
                NativeText projectXmlOutput = new(32 * 1024, Allocator.TempJob);

                //references and defines that are in here need to be parsed out, otherwise
                //intellisense won't pick them up even if the compiler will (same for nullable, it
                //needs to go into the csproj proper)
                string[] rspFilePaths = assembly.compilerOptions.ResponseFiles;
                StringBuilder rspStrings = new();

                //I wonder how we can have multiple response files affecting compilation...
                //that'd be good for testing.
                //https://github.com/dotnet/docs/blob/main/docs/csharp/language-reference/compiler-options/miscellaneous.md#responsefiles
                //So how do we pass this stuff into the generation job? Do we have an extra struct with this data?
                //Do we preprocess and just add it to the existing arrays? (will probably break if there's multiple rsp files)
                //Extra struct is probably easier, but needs more memory management...
                foreach (string rspPath in rspFilePaths)
                {
                    ResponseFileData rspData = CompilationPipeline.ParseResponseFile(rspPath,
                        ProjectDirectory,
                        systemReferenceDirs);

                    rspStrings.Clear();
                    rspStrings.AppendLine($"{rspPath}:");

                    //add to defines
                    string[] extraDefines = rspData.Defines;
                    rspStrings.Append("Extra defines: ");
                    rspStrings.Append(string.Join(", ", extraDefines));
                    rspStrings.AppendLine();

                    //print errors if there is any
                    string[] errors = rspData.Errors;
                    rspStrings.Append("Errors: ");
                    rspStrings.Append(string.Join('\n', errors));
                    rspStrings.AppendLine();

                    //precedence: this or whatever assembly.compilerOptions says?
                    bool allowUnsafe = rspData.Unsafe;
                    rspStrings.AppendLine($"Unsafe: {allowUnsafe}");

                    //add to references
                    string[] references = rspData.FullPathReferences;
                    foreach (string r in references)
                    {
                        extraAssemblyReferencePathsUtf16.Add(r.ToUnsafeList(Allocator.TempJob));
                    }

                    rspStrings.Append("Extra references: ");
                    rspStrings.Append(string.Join(", ", references));
                    rspStrings.AppendLine();

                    //check for nullable (do this with assembly.additionalCompilerOptions too)
                    //support at least nullable and warnaserror like rider does:
                    //https://github.com/needle-mirror/com.unity.ide.rider/blob/master/Rider/Editor/ProjectGeneration/ProjectGeneration.cs#L822
                    string[] otherArgs = rspData.OtherArguments;
                    rspStrings.Append("Other args: ");
                    rspStrings.Append(string.Join(", ", otherArgs));
                    rspStrings.AppendLine();

                    foreach (string arg in otherArgs)
                    {
                        var withoutSwitch = arg.AsSpan()[1..];
                        if (withoutSwitch.StartsWith("nullable".AsSpan()))
                        {
                            int colonIndex = withoutSwitch.IndexOf(':');
                            if (colonIndex != -1)
                            {
                                //ugh, please unity make it possible to init from span. please.
                                nullableContext = withoutSwitch[(colonIndex + 1)..].ToString();
                            }
                        }
                    }

                    //TODO: add path to rsp file into csproj as well so compiler picks it up (only 1st though)
                    Debug.Log(rspStrings.ToString());
                }

                PrepareDataJob prepJob = new()
                {
                    pathArrayHandle = GCHandle.Alloc(csSourceFiles),
                    projectDirectoryStringHandle = GCHandle.Alloc(ProjectDirectory),
                    compiledAssemblyRefsHandle = GCHandle.Alloc(compiledAssemblyRefs),
                    definesArrayHandle = GCHandle.Alloc(csDefines),
                    assembliesArrayHandle = GCHandle.Alloc(maybeAsmdefReferences),
                    unityProjectNameHandle = GCHandle.Alloc(m_unityProjectName),
                    projectReferences = projectReferences,
                    definesUtf16 = definesUtf16,
                    assemblyReferencePathsUtf16 = assemblyReferencePathsUtf16,
                    sourceFilesUtf16 = sourceFilesUtf16
                };

                JobHandle prepJobHandle = prepJob.Schedule();

                GenerateProjectJob generateJob = new()
                {
                    assemblyName = new(assembly.name),
                    definesUtf16 = definesUtf16,
                    sourceFilesUtf16 = sourceFilesUtf16,
                    scriptAssembliesPath = scriptAssembliesPathFixed,
                    assemblyReferencePathsUtf16 = assemblyReferencePathsUtf16,
                    extraAssemblyReferencePathsUtf16 = extraAssemblyReferencePathsUtf16,
                    projectXmlOutput = projectXmlOutput,
                    langVersion = new(langVersion),
                    unsafeCode = unsafeCode,
                    projectReferences = projectReferences,
                    excludedAssemblies = excludedAssemblies,
                    nullableContext = nullableContext
                };

                WriteToFileJob writeJob = new()
                {
                    content = projectXmlOutput,
                    filePath = new FixedString4096Bytes(Path.Combine(ProjectDirectory, $"{assembly.name}.csproj"))
                };

                var projHandle = generateJob.Schedule(prepJobHandle);
                var handle = writeJob.Schedule(projHandle);

                //Unfortunately, Dispose(JobHandle) seems to be broken for NativeText :(
                //Let's see if Unity fixes it.
                //https://issuetracker.unity3d.com/issues/burst-collections-nullreferenceexceptions-thrown-when-using-nativetext-dot-dispose-jobhandle
                // NativeArray<JobHandle> cleanupJobs = new(6, Allocator.Temp);
                // cleanupJobs[0] = assemblyReferences.Dispose(handle);
                // cleanupJobs[1] = defines.Dispose(handle);
                // cleanupJobs[2] = sourceFilesUtf16.Dispose(projHandle);
                // cleanupJobs[3] = searchPaths.Dispose(projHandle);
                // cleanupJobs[4] = projectReferences.Dispose(handle);
                // cleanupJobs[5] = projectTextOutput.Dispose(handle);
                // JobHandle cleanupHandle = JobHandle.CombineDependencies(cleanupJobs);

                jobList.Add((handle, generateJob));
            }

            s_setupJobsMarker.End();

            s_completeAndDisposeMarker.Begin();

            //complete all the jobs, dispose all the things
            foreach ((JobHandle handle, var jobData) in jobList)
            {
                handle.Complete();

                jobData.definesUtf16.Dispose();
                jobData.sourceFilesUtf16.Dispose();
                jobData.assemblyReferencePathsUtf16.Dispose();
                jobData.extraAssemblyReferencePathsUtf16.Dispose();
                jobData.projectReferences.Dispose();
                jobData.projectXmlOutput.Dispose();
            }
            excludedAssemblies.Dispose();
            s_completeAndDisposeMarker.End();

            Debug.Log(sb.ToString());

            JobifiedCreateSln(projectsInSln);

            string dotVsCodePath = Path.Combine(ProjectDirectory, ".vscode");
            Directory.CreateDirectory(dotVsCodePath);
            string settingsJsonPath = Path.Combine(dotVsCodePath, "settings.json");

            //Only do this when it's not there yet -> respect user modifications
            if (!File.Exists(settingsJsonPath))
            {
                File.WriteAllText(settingsJsonPath, k_SettingsJson);
            }

            s_jobifiedSyncMarker.End();
        }

        static string[] GetSystemAssemblyDirectories(ApiCompatibilityLevel apiCompatLevel)
        {
            //We cache this for the most common api levels (netstandard and unity4_8) so we generate garbage
            //only once (per domain reload).
            if (apiCompatLevel == ApiCompatibilityLevel.NET_Standard)
            {
                return s_netStandardAssemblyDirectories;
            }
            else if (apiCompatLevel == ApiCompatibilityLevel.NET_Unity_4_8)
            {
                return s_net48AssemblyDirectories;
            }
            else
            {
                //slow path, shouldn't really end up here
                return CompilationPipeline.GetSystemAssemblyDirectories(apiCompatLevel);
            }
        }

        public bool SolutionExists()
        {
            return m_FileIOProvider.Exists(SolutionFile());
        }

        void JobifiedCreateSln(NativeList<ProjectReference> projectsInSln)
        {
            s_slnGenMarker.Begin();

            NativeText slnText = new(8192, Allocator.TempJob);
            GenerateSlnJob slnJob = new()
            {
                projectsInSln = projectsInSln,
                output = slnText
            };

            string slnPath = Path.Combine(ProjectDirectory, $"{Path.GetFileName(ProjectDirectory)}.sln");

            WriteToFileJob writeSlnJob = new()
            {
                content = slnText,
                filePath = new FixedString4096Bytes(slnPath)
            };

            writeSlnJob.Schedule(slnJob.Schedule()).Complete();

            slnText.Dispose();
            projectsInSln.Dispose();

            s_slnGenMarker.End();
        }

        public string SolutionFile()
        {
            return Path.Combine(ProjectDirectory, $"{m_unityProjectName}.sln");
        }

        string ProjectGuid(string assembly)
        {
            return m_GUIDProvider.ProjectGuid(m_unityProjectName, assembly);
        }
    }

    public static class SolutionGuidGenerator
    {
        static readonly MD5 mD5 = MD5.Create();

        public static string GuidForProject(string projectName)
        {
            return ComputeGuidHashFor(projectName + "salt");
        }

        static string ComputeGuidHashFor(string input)
        {
            var hash = mD5.ComputeHash(Encoding.Default.GetBytes(input));
            return new Guid(hash).ToString();
        }
    }

        internal static class Extensions
    {
        public static UnsafeList<char> ToUnsafeList(this string source, Allocator allocator)
        {
            UnsafeList<char> data = new(source.Length, allocator);
            unsafe
            {
                fixed (char* sourceStringPtr = source.AsSpan())
                {
                    data.AddRangeNoResize(sourceStringPtr, source.Length);
                }
            }
            return data;
        }
    }
}
