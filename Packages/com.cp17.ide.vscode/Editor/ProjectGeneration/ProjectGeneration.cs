using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security;
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
        public bool OnlyJobified { get; set; }
        bool SyncIfNeeded(List<string> affectedFiles, string[] reimportedFiles);
        void Sync();
        string SolutionFile();
        string ProjectDirectory { get; }
        IAssemblyNameProvider AssemblyNameProvider { get; }
        void GenerateAll(bool generateAll);
        bool SolutionExists();
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

    public class ProjectGeneration : IGenerator
    {
        static readonly ProfilerMarker s_syncMarker = new($"{nameof(VSCodeEditor)}.{nameof(ProjectGeneration)}.{nameof(Sync)}");
        static readonly ProfilerMarker s_genMarker = new($"{nameof(VSCodeEditor)}.{nameof(ProjectGeneration)}.{nameof(GenerateAndWriteSolutionAndProjects)}");
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

        const string k_WindowsNewline = "\r\n";

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

        /// <summary>
        /// Map source extensions to ScriptingLanguages
        /// </summary>
        static readonly Dictionary<string, ScriptingLanguage> k_BuiltinSupportedExtensions = new Dictionary<string, ScriptingLanguage>
        {
            { "cs", ScriptingLanguage.CSharp },
            { "uxml", ScriptingLanguage.None },
            { "uss", ScriptingLanguage.None },
            { "shader", ScriptingLanguage.None },
            { "compute", ScriptingLanguage.None },
            { "cginc", ScriptingLanguage.None },
            { "hlsl", ScriptingLanguage.None },
            { "glslinc", ScriptingLanguage.None },
            { "template", ScriptingLanguage.None },
            { "raytrace", ScriptingLanguage.None }
        };

        const string SlnProjectEntryTemplate = "Project(\"{{{0}}}\") = \"{1}\", \"{2}\", \"{{{3}}}\"" + "\r\n" + "EndProject";
        const string SlnProjectConfigurationTemplate = "\t\t" + @"{{{0}}}.Debug|Any CPU.ActiveCfg = Debug|Any CPU" + "\r\n\t\t" + "{{{0}}}.Debug|Any CPU.Build.0 = Debug|Any CPU";

        string[] m_ProjectSupportedExtensions = Array.Empty<string>();

        public string ProjectDirectory { get; }
        IAssemblyNameProvider IGenerator.AssemblyNameProvider => m_AssemblyNameProvider;

        public bool OnlyJobified { get; set; }

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

        bool HasFilesBeenModified(List<string> affectedFiles, string[] reimportedFiles)
        {
            return affectedFiles.Any(ShouldFileBePartOfSolution) || reimportedFiles.Any(ShouldSyncOnReimportedAsset);
        }

        static bool ShouldSyncOnReimportedAsset(string asset)
        {
            ReadOnlySpan<char> extension = Path.GetExtension(asset.AsSpan());

            if (extension.Equals(".dll".AsSpan(), StringComparison.InvariantCultureIgnoreCase) ||
                extension.Equals(".asmdef".AsSpan(), StringComparison.InvariantCultureIgnoreCase))
            {
                return true;
            }

            return false;
        }

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
            if (OnlyJobified)
            {
                JobifiedSync();
            }
            else
            {
                using (s_syncMarker.Auto())
                {
                    SetupProjectSupportedExtensions();
                    GenerateAndWriteSolutionAndProjects();

                    // OnGeneratedCSProjectFiles();
                }

                JobifiedSync();
            }
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

        void SetupProjectSupportedExtensions()
        {
            //This calls EditorSettings.projectGenerationUserExtensions
            m_ProjectSupportedExtensions = m_AssemblyNameProvider.ProjectSupportedExtensions;
            // Debug.Log($"Project supported extensions: {string.Join('\n', m_ProjectSupportedExtensions)}");
        }

        bool ShouldFileBePartOfSolution(string file)
        {
            // Exclude files coming from packages except if they are internalized.
            if (m_AssemblyNameProvider.IsInternalizedPackagePath(file))
            {
                return false;
            }

            return HasValidExtension(file);
        }

        bool HasValidExtension(string file)
        {
            string extension = Path.GetExtension(file);

            // Dll's are not scripts but still need to be included..
            if (extension == ".dll")
                return true;

            if (file.ToLower().EndsWith(".asmdef"))
                return true;

            return IsSupportedExtension(extension);
        }

        bool IsSupportedExtension(string extension)
        {
            extension = extension.TrimStart('.');
            if (k_BuiltinSupportedExtensions.ContainsKey(extension))
                return true;
            if (m_ProjectSupportedExtensions.Contains(extension))
                return true;
            return false;
        }

        static ScriptingLanguage ScriptingLanguageFor(string extension)
        {
            return k_BuiltinSupportedExtensions.TryGetValue(extension.TrimStart('.'), out var result)
                ? result
                : ScriptingLanguage.None;
        }

        public void GenerateAndWriteSolutionAndProjects()
        {
            using (s_genMarker.Auto())
            {
                // Only synchronize assemblies that have associated source files and ones that we actually want in the project.
                // This also filters out DLLs coming from .asmdef files in packages.
                Assembly[] assemblies = m_AssemblyNameProvider.GetAssemblies(ShouldFileBePartOfSolution).ToArray();

                var allAssetProjectParts = GenerateAllAssetProjectParts();

                SyncSolution(assemblies);
                var allProjectAssemblies = assemblies.ToList();
                foreach (Assembly assembly in allProjectAssemblies)
                {
                    // StringBuilder debugInfo = new();
                    var api = assembly.compilerOptions.ApiCompatibilityLevel;
                    var lang = assembly.compilerOptions.LanguageVersion;
                    var additional = string.Join(' ', assembly.compilerOptions.AdditionalCompilerArguments);
                    //for deterministic comp there is ScriptCompilerOptions.UseDeterministicCompilation (internal)
                    //either fuck around with reflection or set it to true unconditionally, the assemblies dotnet build compiles
                    //aren't used by unity editor anyway
                    // debugInfo.AppendLine($"{assembly.name}:\nrootNamespace: {assembly.rootNamespace}");
                    // debugInfo.AppendLine($"ApiCompatibilityLevel: {api}, languageVersion: {lang}, additionalArgs: {additional}");
                    // debugInfo.AppendLine($"defines: {string.Join(';', assembly.defines)}");
                    // debugInfo.AppendLine($"sourceFiles: {string.Join(' ', assembly.sourceFiles)}");
                    // debugInfo.AppendLine($"allReferences: {string.Join(' ', assembly.allReferences)}");
                    // Debug.Log(debugInfo.ToString());

                    var responseFileData = ParseResponseFileData(assembly);
                    SyncProject(assembly, allAssetProjectParts, responseFileData);
                }

                WriteVSCodeSettingsFiles();
            }
        }

        List<ResponseFileData> ParseResponseFileData(Assembly assembly)
        {
            var systemReferenceDirectories = CompilationPipeline.GetSystemAssemblyDirectories(assembly.compilerOptions.ApiCompatibilityLevel);

            Dictionary<string, ResponseFileData> responseFilesData = assembly.compilerOptions.ResponseFiles.ToDictionary(x => x, x => m_AssemblyNameProvider.ParseResponseFile(
                x,
                ProjectDirectory,
                systemReferenceDirectories
            ));

            Dictionary<string, ResponseFileData> responseFilesWithErrors = responseFilesData.Where(x => x.Value.Errors.Any())
                .ToDictionary(x => x.Key, x => x.Value);

            if (responseFilesWithErrors.Any())
            {
                foreach (var error in responseFilesWithErrors)
                    foreach (var valueError in error.Value.Errors)
                    {
                        Debug.LogError($"{error.Key} Parse Error : {valueError}");
                    }
            }

            return responseFilesData.Select(x => x.Value).ToList();
        }

        Dictionary<string, string> GenerateAllAssetProjectParts()
        {
            Dictionary<string, StringBuilder> stringBuilders = new Dictionary<string, StringBuilder>();

            foreach (string asset in m_AssemblyNameProvider.GetAllAssetPaths())
            {
                // Exclude files coming from packages except if they are internalized.
                // TODO: We need assets from the assembly API
                if (m_AssemblyNameProvider.IsInternalizedPackagePath(asset))
                {
                    continue;
                }

                string extension = Path.GetExtension(asset);
                if (IsSupportedExtension(extension) && ScriptingLanguage.None == ScriptingLanguageFor(extension))
                {
                    // Find assembly the asset belongs to by adding script extension and using compilation pipeline.
                    var assemblyName = m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset);

                    if (string.IsNullOrEmpty(assemblyName))
                    {
                        continue;
                    }

                    assemblyName = Path.GetFileNameWithoutExtension(assemblyName);

                    if (!stringBuilders.TryGetValue(assemblyName, out var projectBuilder))
                    {
                        projectBuilder = new StringBuilder();
                        stringBuilders[assemblyName] = projectBuilder;
                    }

                    projectBuilder.Append("    <None Include=\"").Append(m_FileIOProvider.EscapedRelativePathFor(asset, ProjectDirectory)).Append("\" />").Append(k_WindowsNewline);
                }
            }

            var result = new Dictionary<string, string>();

            foreach (var entry in stringBuilders)
                result[entry.Key] = entry.Value.ToString();

            return result;
        }

        void SyncProject(
            Assembly assembly,
            Dictionary<string, string> allAssetsProjectParts,
            List<ResponseFileData> responseFilesData)
        {
            SyncProjectFileIfNotChanged(
                // ProjectFile(assembly),
                // ProjectText(assembly, allAssetsProjectParts, responseFilesData)
                Path.Combine(ProjectDirectory, string.Concat(assembly.name, ".csproj")),
                ProjectText2(assembly, allAssetsProjectParts, responseFilesData)
            );
        }

        void SyncProjectFileIfNotChanged(string path, string newContents)
        {
            if (Path.GetExtension(path) == ".csproj")
            {
                newContents = OnGeneratedCSProject(path, newContents);
            }

            SyncFileIfNotChanged(path, newContents);
        }

        void SyncSolutionFileIfNotChanged(string path, string newContents)
        {
            newContents = OnGeneratedSlnSolution(path, newContents);

            SyncFileIfNotChanged(path, newContents);
        }

        void SyncFileIfNotChanged(string filename, string newContents)
        {
            try
            {
                if (m_FileIOProvider.Exists(filename) && newContents == m_FileIOProvider.ReadAllText(filename))
                {
                    return;
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
            }

            m_FileIOProvider.WriteAllText(filename, newContents);
        }

        //capacity is specified in chars, char is 2 bytes.
        //let's use a 2MiB buffer for starters.
        StringBuilder m_projectFileBuilder = new(capacity: sizeof(char) * 2 * 1024 * 1024);

        string ProjectText2(Assembly assembly, Dictionary<string, string> allAssetsProjectParts, List<ResponseFileData> responseFilesData)
        {
            m_projectFileBuilder.Clear();
            m_asmSearchPaths.Clear();
            m_defines.Clear();

            //so what's the data we got?
            //assembly got lots of data, like:
            //- .NET API version, C# version
            //- defines
            //- assembly references
            //- source files
            //- response file data (can add defines, references, unsafe and other compiler options)
            //  - we can use <CompilerResponseFile> item in project file, no need to parse a thing

            string langVersion = assembly.compilerOptions.LanguageVersion;
            ApiCompatibilityLevel dotnetApiVersion = assembly.compilerOptions.ApiCompatibilityLevel;
            //if this is true in asmdef but there's a response file saying no, which
            //takes precedence?
            bool allowUnsafe = assembly.compilerOptions.AllowUnsafeCode;

            //Is it possible to have multiple rsp files affecting a compilation?
            //maybe we do need custom parsing after all...
            string[] rspFilePaths = assembly.compilerOptions.ResponseFiles;
            string rspItem = string.Empty;
            if (rspFilePaths.Length > 0)
            {
                //TODO: check if nullable is defined. we wanna write it into the project, otherwise
                //omnisharp won't pick it up if it's in the rsp file only.

                rspItem = string.Concat("<CompilerResponseFile>", assembly.compilerOptions.ResponseFiles[0], "</CompilerResponseFile>");
                if (rspFilePaths.Length > 1) Debug.LogWarning("Multiple rsp files affecting compilation");
            }

            foreach (string asmPath in assembly.allReferences)
            {
                string dir = Path.GetDirectoryName(asmPath);
                m_asmSearchPaths.Add(dir);
            }

            m_defines.UnionWith(assembly.defines);

            m_projectFileBuilder.AppendFormat(
@"<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <TargetFramework>netstandard2.1</TargetFramework>
        <LangVersion>{0}</LangVersion>
        <EnableDefaultItems>false</EnableDefaultItems>
        <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
        <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
        <Deterministic>true</Deterministic>
        <OutputPath>Temp</OutputPath>
        <DefineConstants>{1}</DefineConstants>
        <AllowUnsafeBlocks>{2}</AllowUnsafeBlocks>
        <AssemblySearchPaths>
            {3};
            $(AssemblySearchPaths)
        </AssemblySearchPaths>
        {4}
    </PropertyGroup>
    <ItemGroup>",
                langVersion,
                string.Join(';', m_defines),
                allowUnsafe,
                //I wonder which of these 2 is faster
                // asmSearchPathBuilder.ToString()
                string.Join(";", m_asmSearchPaths),
                rspItem
            );

            var references = new List<string>();

            foreach (string file in assembly.sourceFiles)
            {
                var fullFile = m_FileIOProvider.EscapedRelativePathFor(file, ProjectDirectory);
                m_projectFileBuilder.Append("    <Compile Include=\"").Append(fullFile).Append("\" />").Append(k_WindowsNewline);
            }

            // Append additional non-script files that should be included in project generation.
            if (allAssetsProjectParts.TryGetValue(assembly.name, out var additionalAssetsForProject))
                m_projectFileBuilder.Append(additionalAssetsForProject);

            foreach (var rspData in responseFilesData)
            {
                references.AddRange(rspData.FullPathReferences);
            }

            foreach (var thing in assembly.assemblyReferences)
            {
                foreach (var file in thing.sourceFiles)
                {
                    if (!ShouldFileBePartOfSolution(file))
                    {
                        references.Add(thing.outputPath);
                        break;
                    }
                }
            }

            references.AddRange(assembly.compiledAssemblyReferences);
            var allReferences = references;

            foreach (var reference in allReferences)
            {
                string fullReference = Path.IsPathRooted(reference) ? reference : Path.Combine(ProjectDirectory, reference);
                AppendReference(fullReference, m_projectFileBuilder);
            }

            if (0 < assembly.assemblyReferences.Length)
            {
                m_projectFileBuilder.Append("  </ItemGroup>").Append(k_WindowsNewline);
                m_projectFileBuilder.Append("  <ItemGroup>").Append(k_WindowsNewline);

                foreach (Assembly reference in assembly.assemblyReferences)
                {
                    foreach (var file in reference.sourceFiles)
                    {
                        if (ShouldFileBePartOfSolution(file))
                        {
                            m_projectFileBuilder.Append("    <ProjectReference Include=\"").Append(reference.name).Append(GetProjectExtension()).Append("\">").Append(k_WindowsNewline);
                            m_projectFileBuilder.Append("      <Project>{").Append(ProjectGuid(reference.name)).Append("}</Project>").Append(k_WindowsNewline);
                            m_projectFileBuilder.Append("      <Name>").Append(reference.name).Append("</Name>").Append(k_WindowsNewline);
                            m_projectFileBuilder.Append("    </ProjectReference>").Append(k_WindowsNewline);
                            break;
                        }
                    }
                }
            }

            m_projectFileBuilder.Append(ProjectFooter());

            return m_projectFileBuilder.ToString();
        }

        string ProjectText(
            Assembly assembly,
            Dictionary<string, string> allAssetsProjectParts,
            List<ResponseFileData> responseFilesData)
        {
            var projectBuilder = new StringBuilder();
            ProjectHeader(assembly, responseFilesData, projectBuilder);
            var references = new List<string>();

            foreach (string file in assembly.sourceFiles)
            {
                var fullFile = m_FileIOProvider.EscapedRelativePathFor(file, ProjectDirectory);
                projectBuilder.Append("     <Compile Include=\"").Append(fullFile).Append("\" />").Append(k_WindowsNewline);
            }

            // Append additional non-script files that should be included in project generation.
            if (allAssetsProjectParts.TryGetValue(assembly.name, out var additionalAssetsForProject))
                projectBuilder.Append(additionalAssetsForProject);

            var responseRefs = responseFilesData.SelectMany(x => x.FullPathReferences.Select(r => r));
            var internalAssemblyReferences = assembly.assemblyReferences
              .Where(i => !i.sourceFiles.Any(ShouldFileBePartOfSolution)).Select(i => i.outputPath);
            var allReferences =
              assembly.compiledAssemblyReferences
                .Union(responseRefs)
                .Union(references)
                .Union(internalAssemblyReferences);

            foreach (var reference in allReferences)
            {
                string fullReference = Path.IsPathRooted(reference) ? reference : Path.Combine(ProjectDirectory, reference);
                AppendReference(fullReference, projectBuilder);
            }

            if (0 < assembly.assemblyReferences.Length)
            {
                projectBuilder.Append("  </ItemGroup>").Append(k_WindowsNewline);
                projectBuilder.Append("  <ItemGroup>").Append(k_WindowsNewline);
                foreach (Assembly reference in assembly.assemblyReferences.Where(i => i.sourceFiles.Any(ShouldFileBePartOfSolution)))
                {
                    projectBuilder.Append("    <ProjectReference Include=\"").Append(reference.name).Append(GetProjectExtension()).Append("\">").Append(k_WindowsNewline);
                    projectBuilder.Append("      <Project>{").Append(ProjectGuid(reference.name)).Append("}</Project>").Append(k_WindowsNewline);
                    projectBuilder.Append("      <Name>").Append(reference.name).Append("</Name>").Append(k_WindowsNewline);
                    projectBuilder.Append("    </ProjectReference>").Append(k_WindowsNewline);
                }
            }

            projectBuilder.Append(ProjectFooter());
            return projectBuilder.ToString();
        }

        static void AppendReference(string fullReference, StringBuilder projectBuilder)
        {
            //https://github.com/Unity-Technologies/mono/blob/unity-2021.3-mbe/mcs/class/corlib/System.Security/SecurityElement.cs#L294
            var escapedFullPath = SecurityElement.Escape(fullReference);

            //I wonder if this is needed now that msbuild is cross-plat...
            //Whatever they mean by "current behavior", this will need tests:
            //https://github.com/dotnet/msbuild/issues/1024
            // escapedFullPath = escapedFullPath.NormalizePath();
            ReadOnlySpan<char> assemblyName = escapedFullPath.AsSpan();
            int slashIndex = assemblyName.LastIndexOf('/');
            int dotIndex = assemblyName.LastIndexOf('.');
            assemblyName = assemblyName[(slashIndex + 1)..dotIndex];

            projectBuilder.Append("    <Reference Include=\"").Append(assemblyName).Append("\" />\n");
        }

        public string ProjectFile(Assembly assembly)
        {
            var fileBuilder = new StringBuilder(assembly.name);
            fileBuilder.Append(".csproj");
            return Path.Combine(ProjectDirectory, fileBuilder.ToString());
        }

        public string SolutionFile()
        {
            return Path.Combine(ProjectDirectory, $"{m_unityProjectName}.sln");
        }

        //I wonder if it's faster to use hashset vs fucking around with arrays + maybe
        //a fast hash that unity has built in?
        HashSet<string> m_defines = new();
        HashSet<string> m_asmSearchPaths = new();

        void ProjectHeader(
            Assembly assembly,
            List<ResponseFileData> responseFilesData,
            StringBuilder builder
        )
        {
            var otherArguments = GetOtherArgumentsFromResponseFilesData(responseFilesData);

            m_defines.Clear();
            m_defines.UnionWith(assembly.defines);

            //I don't think we need to include this?
            //CompilationPipeline should give us all the defines a dll is compiled with,
            //otherwise what's the point?
            m_defines.UnionWith(EditorUserBuildSettings.activeScriptCompilationDefines);

            foreach (var rspFileData in responseFilesData)
            {
                m_defines.UnionWith(rspFileData.Defines);
            }

            //This allocates a whole fucking lot. Let's see if we can do the same thing
            //without allocating a while fucking lot
            // var defines = new[] { "DEBUG", "TRACE" }
            //         .Concat(assembly.defines)
            //         .Concat(responseFilesData.SelectMany(x => x.Defines))
            //         //do we really need this?
            //         //I mean the compiler should append everything needed for editor and player assemblies
            //         //and CompilationPipeline should tell us, right?
            //         // .Concat(EditorUserBuildSettings.activeScriptCompilationDefines)
            //         .Distinct()
            //         .ToArray();
            var defines = m_defines;

            m_asmSearchPaths.Clear();
            //This kinda sucks because we also iterate over all assembly references when doing AppendReference
            foreach (string asmPath in assembly.allReferences)
            {
                string dir = Path.GetDirectoryName(asmPath);
                m_asmSearchPaths.Add(dir);
            }

            GetProjectHeaderTemplate(
                builder,
                ProjectGuid(assembly.name),
                assembly.name,
                string.Join(";", defines),
                assembly.compilerOptions.LanguageVersion,
                assembly.compilerOptions.AllowUnsafeCode | responseFilesData.Any(x => x.Unsafe),
                GenerateAnalyserItemGroup(RetrieveRoslynAnalyzers(assembly, otherArguments)),
                GenerateRoslynAnalyzerRulesetPath(assembly, otherArguments),
                CompilationPipeline.GetSystemAssemblyDirectories(assembly.compilerOptions.ApiCompatibilityLevel),
                m_asmSearchPaths
            );
        }

        static string GenerateRoslynAnalyzerRulesetPath(Assembly assembly, ILookup<string, string> otherResponseFilesData)
        {
            return GenerateAnalyserRuleSet(otherResponseFilesData["ruleset"].Append(assembly.compilerOptions.RoslynAnalyzerRulesetPath).Where(a => !string.IsNullOrEmpty(a)).Distinct().Select(x => MakeAbsolutePath(x).NormalizePath()).ToArray());
        }

        static string GenerateAnalyserRuleSet(string[] paths)
        {
            return paths.Length == 0
                ? string.Empty
                : $"{Environment.NewLine}{string.Join(Environment.NewLine, paths.Select(a => $"    <CodeAnalysisRuleSet>{a}</CodeAnalysisRuleSet>"))}";
        }

        static string MakeAbsolutePath(string path)
        {
            return Path.IsPathRooted(path) ? path : Path.GetFullPath(path);
        }

        static ILookup<string, string> GetOtherArgumentsFromResponseFilesData(List<ResponseFileData> responseFilesData)
        {
            var paths = responseFilesData.SelectMany(x =>
                {
                    return x.OtherArguments.Where(a => a.StartsWith("/") || a.StartsWith("-"))
                                           .Select(b =>
                    {
                        var index = b.IndexOf(":", StringComparison.Ordinal);
                        if (index > 0 && b.Length > index)
                        {
                            var key = b.Substring(1, index - 1);
                            return new KeyValuePair<string, string>(key, b.Substring(index + 1));
                        }

                        const string warnaserror = "warnaserror";
                        return b.Substring(1).StartsWith(warnaserror)
                            ? new KeyValuePair<string, string>(warnaserror, b.Substring(warnaserror.Length + 1))
                            : default;
                    });
                })
              .Distinct()
              .ToLookup(o => o.Key, pair => pair.Value);
            return paths;
        }

        string[] RetrieveRoslynAnalyzers(Assembly assembly, ILookup<string, string> otherArguments)
        {
            return otherArguments["analyzer"].Concat(otherArguments["a"])
                .SelectMany(x => x.Split(';'))
                .Concat(assembly.compilerOptions.RoslynAnalyzerDllPaths)
                .Select(MakeAbsolutePath)
                .Distinct()
                .ToArray();
        }

        static string GenerateAnalyserItemGroup(string[] paths)
        {
            //   <ItemGroup>
            //      <Analyzer Include="..\packages\Comments_analyser.1.0.6626.21356\analyzers\dotnet\cs\Comments_analyser.dll" />
            //      <Analyzer Include="..\packages\UnityEngineAnalyzer.1.0.0.0\analyzers\dotnet\cs\UnityEngineAnalyzer.dll" />
            //  </ItemGroup>
            if (paths.Length == 0)
            {
                return string.Empty;
            }

            var analyserBuilder = new StringBuilder();
            analyserBuilder.Append("  <ItemGroup>").Append(k_WindowsNewline);
            foreach (var path in paths)
            {
                analyserBuilder.Append($"    <Analyzer Include=\"{path.NormalizePath()}\" />").Append(k_WindowsNewline);
            }

            analyserBuilder.Append("  </ItemGroup>").Append(k_WindowsNewline);
            return analyserBuilder.ToString();
        }

        static string GetSolutionText()
        {
            return string.Join("\r\n", @"", @"Microsoft Visual Studio Solution File, Format Version {0}", @"# Visual Studio {1}", @"{2}", @"Global", @"    GlobalSection(SolutionConfigurationPlatforms) = preSolution", @"        Debug|Any CPU = Debug|Any CPU", @"    EndGlobalSection", @"    GlobalSection(ProjectConfigurationPlatforms) = postSolution", @"{3}", @"    EndGlobalSection", @"    GlobalSection(SolutionProperties) = preSolution", @"        HideSolutionNode = FALSE", @"    EndGlobalSection", @"EndGlobal", @"").Replace("    ", "\t");
        }

        static string GetProjectFooterTemplate()
        {
            return string.Join("\r\n", @"</ItemGroup></Project> ");
        }

        static void GetProjectHeaderTemplate(
            StringBuilder builder,
            string assemblyGUID,
            string assemblyName,
            string defines,
            string langVersion,
            bool allowUnsafe,
            string analyzerBlock,
            string rulesetBlock,
            string[]? systemAssemblyDirs = null,
            HashSet<string>? asmSearchPaths = null
        )
        {
            // string unityPath = Path.GetDirectoryName(EditorApplication.applicationPath);
            // string libraryPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Library"));
            // Debug.Log($"Unity dir: {unityPath}, library dir: {libraryPath}");
            // Debug.Log($"System assembly dirs: {string.Join("\n", systemAssemblyDirs)}");

            StringBuilder asmSearchPathBuilder = new();
            if (asmSearchPaths != null)
            {
                foreach (string path in asmSearchPaths)
                {
                    asmSearchPathBuilder.Append(path).Append(';');
                }
            }

            builder.AppendFormat(
@"<Project Sdk=""Microsoft.NET.Sdk"">
    <PropertyGroup>
        <TargetFramework>netstandard2.1</TargetFramework>
        <LangVersion>{0}</LangVersion>
        <EnableDefaultItems>false</EnableDefaultItems>
        <DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>
        <GenerateAssemblyInfo>false</GenerateAssemblyInfo>
        <Deterministic>true</Deterministic>
        <OutputPath>Temp</OutputPath>
        <DefineConstants>{1}</DefineConstants>
        <AllowUnsafeBlocks>{2}</AllowUnsafeBlocks>
        <AssemblySearchPaths>
            {3}
            $(AssemblySearchPaths)
        </AssemblySearchPaths>
    </PropertyGroup>
    <ItemGroup>",
                langVersion,
                defines,
                allowUnsafe,
                //I wonder which of these 2 is faster
                asmSearchPathBuilder.ToString()
            // string.Join(";", asmSearchPaths)
            ).Append(k_WindowsNewline);
        }

        void SyncSolution(IEnumerable<Assembly> assemblies)
        {
            SyncSolutionFileIfNotChanged(SolutionFile(), SolutionText(assemblies));
        }

        string SolutionText(IEnumerable<Assembly> assemblies)
        {
            var fileversion = "11.00";
            var vsversion = "2010";

            var relevantAssemblies = assemblies;
            string projectEntries = GetProjectEntries(relevantAssemblies);
            string projectConfigurations = string.Join(k_WindowsNewline, relevantAssemblies.Select(i => GetProjectActiveConfigurations(ProjectGuid(i.name))).ToArray());
            return string.Format(GetSolutionText(), fileversion, vsversion, projectEntries, projectConfigurations);
        }

        /// <summary>
        /// Get a Project("{guid}") = "MyProject", "MyProject.csproj", "{projectguid}"
        /// entry for each relevant language
        /// </summary>
        string GetProjectEntries(IEnumerable<Assembly> assemblies)
        {
            var projectEntries = assemblies.Select(i => string.Format(
                SlnProjectEntryTemplate,
                m_GUIDProvider.SolutionGuid,
                i.name,
                Path.GetFileName(ProjectFile(i)),
                ProjectGuid(i.name)
            ));

            return string.Join(k_WindowsNewline, projectEntries.ToArray());
        }

        /// <summary>
        /// Generate the active configuration string for a given project guid
        /// </summary>
        string GetProjectActiveConfigurations(string projectGuid)
        {
            return string.Format(
                SlnProjectConfigurationTemplate,
                projectGuid);
        }

        string ProjectGuid(string assembly)
        {
            return m_GUIDProvider.ProjectGuid(m_unityProjectName, assembly);
        }

        static string ProjectFooter()
        {
            return GetProjectFooterTemplate();
        }

        static string GetProjectExtension()
        {
            return ".csproj";
        }

        void WriteVSCodeSettingsFiles()
        {
            var vsCodeDirectory = Path.Combine(ProjectDirectory, ".vscode");

            if (!m_FileIOProvider.Exists(vsCodeDirectory))
                m_FileIOProvider.CreateDirectory(vsCodeDirectory);

            var vsCodeSettingsJson = Path.Combine(vsCodeDirectory, "settings.json");

            if (!m_FileIOProvider.Exists(vsCodeSettingsJson))
                m_FileIOProvider.WriteAllText(vsCodeSettingsJson, k_SettingsJson);
        }
    }

    public static class SolutionGuidGenerator
    {
        static MD5 mD5 = MD5CryptoServiceProvider.Create();

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
}
