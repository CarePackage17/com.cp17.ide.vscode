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
using UnityEngine.Profiling;
using Unity.Profiling;
using Unity.Jobs;
using Unity.Collections;
using UnityEditor.PackageManager;

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
        static ProfilerMarker s_syncMarker = new($"{nameof(VSCodeEditor)}.{nameof(ProjectGeneration)}.{nameof(Sync)}");
        static ProfilerMarker s_genMarker = new($"{nameof(VSCodeEditor)}.{nameof(ProjectGeneration)}.{nameof(GenerateAndWriteSolutionAndProjects)}");
        static ProfilerMarker s_jobifiedSyncMarker = new($"{nameof(VSCodeEditor)}.{nameof(ProjectGeneration)}.{nameof(JobifiedSync)}");

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
        ""**/*.asset"":true,
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

        readonly string m_ProjectName;
        readonly IAssemblyNameProvider m_AssemblyNameProvider;
        readonly IFileIO m_FileIOProvider;
        readonly IGUIDGenerator m_GUIDProvider;

        public ProjectGeneration(string tempDirectory)
            : this(tempDirectory, new AssemblyNameProvider(), new FileIOProvider(), new GUIDProvider()) { }

        public ProjectGeneration(string tempDirectory, IAssemblyNameProvider assemblyNameProvider, IFileIO fileIO, IGUIDGenerator guidGenerator)
        {
            ProjectDirectory = tempDirectory.NormalizePath();
            m_ProjectName = Path.GetFileName(ProjectDirectory);
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
            Profiler.BeginSample("SolutionSynchronizerSync");
            SetupProjectSupportedExtensions();

            if (!HasFilesBeenModified(affectedFiles, reimportedFiles))
            {
                Profiler.EndSample();
                return false;
            }

            var assemblies = m_AssemblyNameProvider.GetAssemblies(ShouldFileBePartOfSolution);
            var allProjectAssemblies = assemblies.ToList();
            SyncSolution(allProjectAssemblies);

            var allAssetProjectParts = GenerateAllAssetProjectParts();

            var affectedNames = affectedFiles.Select(asset => m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset)).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Split(new[] { ".dll" }, StringSplitOptions.RemoveEmptyEntries)[0]);
            var reimportedNames = reimportedFiles.Select(asset => m_AssemblyNameProvider.GetAssemblyNameFromScriptPath(asset)).Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name.Split(new[] { ".dll" }, StringSplitOptions.RemoveEmptyEntries)[0]);
            var affectedAndReimported = new HashSet<string>(affectedNames.Concat(reimportedNames));

            foreach (var assembly in allProjectAssemblies)
            {
                if (!affectedAndReimported.Contains(assembly.name))
                    continue;

                SyncProject(assembly, allAssetProjectParts, ParseResponseFileData(assembly));
            }

            Profiler.EndSample();
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

        static void OnGeneratedCSProjectFiles()
        {
            foreach (var method in GetPostProcessorCallbacks(nameof(OnGeneratedCSProjectFiles)))
            {
                method.Invoke(null, Array.Empty<object>());
            }
        }

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
            using (s_syncMarker.Auto())
            {
                SetupProjectSupportedExtensions();
                GenerateAndWriteSolutionAndProjects();

                OnGeneratedCSProjectFiles();
            }

            using (s_jobifiedSyncMarker.Auto())
            {
                JobifiedSync();
            }
        }

        void JobifiedSync()
        {
            //This generates a lot of garbage, but we can't avoid it.
            //It's the only way to get this data as of 2021 LTS.
            Assembly[] assemblies = CompilationPipeline.GetAssemblies();

            //These are always the same, so don't need to be inside the loop
            FixedString64Bytes compileFormatString = new("<Compile Include=\"{0}\" />\n");
            FixedString64Bytes referenceFormatString = new("<Reference Include=\"{0}\" />\n");
            FixedString64Bytes itemGroupFormatString = new("<ItemGroup>\n{0}\n{1}\n</ItemGroup>\n");
            FixedString64Bytes projectElement = new("<Project Sdk=\"Microsoft.NET.Sdk\">\n");
            FixedString32Bytes projectEndElement = new("</Project>\n");
            FixedString32Bytes itemGroupElement = new("<ItemGroup>\n");
            FixedString32Bytes itemGroupEndElement = new("</ItemGroup>\n");
            FixedString32Bytes trueStr = new("true");
            FixedString32Bytes falseStr = new("false");
            FixedString4096Bytes propertyGroupFormatString =
                new("<PropertyGroup>\n" +
                        "<TargetFramework>netstandard2.1</TargetFramework>\n" + //make this configurable as well
                        "<LangVersion>{0}</LangVersion>\n" +
                        "<EnableDefaultItems>false</EnableDefaultItems>\n" +
                        "<DisableImplicitFrameworkReferences>true</DisableImplicitFrameworkReferences>\n" +
                        "<GenerateAssemblyInfo>false</GenerateAssemblyInfo>\n" +
                        "<Deterministic>true</Deterministic>\n" +
                        "<OutputPath>Temp</OutputPath>\n" +
                        "<AllowUnsafeBlocks>{1}</AllowUnsafeBlocks>\n" +
                        "<AssemblySearchPaths>{2}</AssemblySearchPaths>\n" +
                        "<DefineConstants>{3}</DefineConstants>\n" +
                    "</PropertyGroup>\n");

            //It'd be nicer if this was NativeArray but it doesn't like tuples :(
            List<(JobHandle, GenerateProjectJob)> jobList = new(assemblies.Length);

            for (int i = 0; i < assemblies.Length; i++)
            {
                Assembly assembly = assemblies[i];

                //Skip empty assemblies, they don't need a csproj
                if (assembly.sourceFiles.Length == 0) continue;

                //Check settings if we should do anything at all for this assembly.
                //I wonder if it's enough to just check the first source file path to see if it's in a package;
                //I mean you can't have source files outside the package dir, can you? (what about asmref?)
                var packageInfo = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(assembly.sourceFiles[0]);
                if (packageInfo != null)
                {
                    PackageSource source = packageInfo.source;

                    //check if user settings exclude sources, then skip processing assembly if it's excluded
                    //assembly is excluded when its source is excluded in user settings
                    //source excluded in settings when it doesn't have projectgenerationflag
                    if (!IsAssemblyIncluded(source, m_AssemblyNameProvider.ProjectGenerationFlag))
                    {
                        Debug.Log($"{assembly.name} is from source {source} and should be excluded");
                    }
                }

                ApiCompatibilityLevel apiCompatLevel = assembly.compilerOptions.ApiCompatibilityLevel;
                string projectGuid = ProjectGuid(assembly.name);
                string[] systemReferenceDirs = CompilationPipeline.GetSystemAssemblyDirectories(apiCompatLevel);
                string[] csDefines = assembly.defines;
                string[] csSourceFiles = assembly.sourceFiles;
                string[] csRefs = assembly.compiledAssemblyReferences;
                Assembly[] maybeAsmdefReferences = assembly.assemblyReferences;
                string langVersion = assembly.compilerOptions.LanguageVersion;
                bool unsafeCode = assembly.compilerOptions.AllowUnsafeCode;

                //so it turns out that NativeText is a container and it can't be in NativeArrays...
                //well, then let's do one level of NativeText with internal separator chars.
                //defines and search paths by ';', source files by ':'
                NativeText defines = new(4096, Allocator.TempJob);
                NativeText sourceFiles = new(8192, Allocator.TempJob);
                NativeText searchPaths = new(8192, Allocator.TempJob);
                NativeArray<FixedString4096Bytes> refs = new(csRefs.Length, Allocator.TempJob);
                NativeArray<ProjectReference> projectRefs = new(maybeAsmdefReferences.Length, Allocator.TempJob);
                NativeText projectTextOutput = new(32 * 1024, Allocator.TempJob);
                NativeList<int> searchPathHashes = new(64, Allocator.Temp);

                int refIndex = 0;
                foreach (string reference in csRefs)
                {
                    //the references we get here are full paths to dll files.
                    //for sdk-style msbuild we just need the module names without the dll extension, but
                    //the directory it's in needs to be added to the search path.
                    var refFileName = Path.GetFileNameWithoutExtension(reference.AsSpan());

                    //this will have duplicates. use hashing to get rid of them
                    //we could do deduplication inside the job (if that ran on a worker it'd not block main as much)
                    var searchPath = Path.GetDirectoryName(reference);
                    int hash = searchPath.GetHashCode();
                    if (!searchPathHashes.Contains(hash))
                    {
                        searchPathHashes.Add(hash);
                        searchPaths.Append(searchPath);
                        searchPaths.Append(';');
                    }

                    //come on, why can't we init a fixedstring from span :(
                    refs[refIndex] = new(refFileName.ToString());
                    refIndex++;
                }

                //Add this to the end
                searchPaths.Append("$(AssemblySearchPaths)");

                foreach (string filePath in csSourceFiles)
                {
                    //problem: we get paths like Packages/... but those don't exist on the file system;
                    //try this: https://docs.unity3d.com/Manual/upm-assets.html
                    //It does get absolute paths (Unity uses MonoIO to remap), we need relative to project dir (and old code does that)
                    string absolutePath = Path.GetFullPath(filePath);
                    string relativeToProject = Path.GetRelativePath(ProjectDirectory, absolutePath);
                    sourceFiles.Append(relativeToProject);

                    //We concat with an illegal file path char in msbuild (any illegal windows path char should do)
                    //https://learn.microsoft.com/en-us/windows/win32/fileio/naming-a-file#naming-conventions
                    sourceFiles.Append(':');
                }

                foreach (string define in csDefines)
                {
                    //Defines need to be in this format for the final output anyway.
                    defines.Append(define);
                    defines.Append(';');
                }

                //These references are the ones set up via asmdef -> we want a project reference
                //here. I think the assembly name should be enough? That's how we generate project names
                //anyway, right?
                int refIndex2 = 0;
                foreach (Assembly a in maybeAsmdefReferences)
                {
                    projectRefs[refIndex2] = new(a.name, ProjectGuid(a.name));
                    refIndex2++;
                }

                GenerateProjectJob generateJob = new()
                {
                    assemblyName = new(assembly.name),
                    assemblyReferences = refs,
                    defines = defines,
                    files = sourceFiles,
                    output = projectTextOutput,
                    compileFormatString = compileFormatString,
                    referenceFormatString = referenceFormatString,
                    assemblySearchPaths = searchPaths,
                    itemGroupFormatString = itemGroupFormatString,
                    propertyGroupFormatString = propertyGroupFormatString,
                    langVersion = new(langVersion),
                    unsafeCode = unsafeCode ? trueStr : falseStr,
                    projectReferences = projectRefs,
                    projectElement = projectElement,
                    projectEndElement = projectEndElement,
                    itemGroupElement = itemGroupElement,
                    itemGroupEndElement = itemGroupEndElement,
                    projectReferenceStrings = new()
                    {
                        nameFormatString = new("<Name>{0}</Name>"),
                        projectFormatString = new("<Project>{0}</Project>"),
                        projectReferenceStart = new("<ProjectReference Include=\"{0}.csproj\">"),
                        projectReferenceEnd = new("</ProjectReference>"),
                        // guidFormatString = new("{0}-{1}-{2}-{3}-{4}"),
                    }
                };

                WriteToFileJob writeJob = new()
                {
                    content = projectTextOutput,
                    filePath = new FixedString4096Bytes(Path.Combine(ProjectDirectory, $"{assembly.name}.csproj"))
                };

                var projHandle = generateJob.Schedule();
                var handle = writeJob.Schedule(projHandle);

                jobList.Add((handle, generateJob));

                //references and defines that are in here need to be parsed out, otherwise
                //intellisense won't pick them up even if the compiler will (same for nullable, it
                //needs to go into the csproj proper)
                string[] rspFilePaths = assembly.compilerOptions.ResponseFiles;
                StringBuilder rspStrings = new();

                //I wonder how we can have multiple response files affecting compilation...
                //that'd be good for testing.
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

                    //TODO: add path to rsp file into csproj as well so compiler picks it up (only 1st though)
                    Debug.Log(rspStrings.ToString());
                }
            }

            //complete all the jobs
            //dispose all the things
            foreach ((JobHandle handle, var jobData) in jobList)
            {
                handle.Complete();

                jobData.defines.Dispose();
                jobData.files.Dispose();
                jobData.assemblySearchPaths.Dispose();
                jobData.assemblyReferences.Dispose();
                jobData.projectReferences.Dispose();
                jobData.output.Dispose();
            }
        }

        public bool SolutionExists()
        {
            return m_FileIOProvider.Exists(SolutionFile());
        }

        void SetupProjectSupportedExtensions()
        {
            m_ProjectSupportedExtensions = m_AssemblyNameProvider.ProjectSupportedExtensions;
            Debug.Log($"Project supported extensions: {string.Join('\n', m_ProjectSupportedExtensions)}");
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
            return Path.Combine(ProjectDirectory, $"{m_ProjectName}.sln");
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
            return m_GUIDProvider.ProjectGuid(m_ProjectName, assembly);
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
