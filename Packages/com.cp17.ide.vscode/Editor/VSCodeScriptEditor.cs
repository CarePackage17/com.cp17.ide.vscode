using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Unity.CodeEditor;
using System.Collections.Generic;

namespace VSCodeEditor
{
    [InitializeOnLoad]
    class NewEditor : IExternalCodeEditor
    {
        List<CodeEditor.Installation> _installations = new();
        static readonly string[] KnownVsCodeInstallPaths = new[]
        {
            "/bin/code",
            "/usr/bin/code"
        };

        static NewEditor()
        {
            UnityEngine.Debug.Log("InitializeOnLoad called us");

            //Here we can create an actual instance of IExternalCodeEditor and register it, then
            //it should show up in the UI.
            NewEditor editor = new();
            CodeEditor.Register(editor);
        }

        NewEditor()
        {
            foreach (string path in KnownVsCodeInstallPaths)
            {
                if (File.Exists(path))
                {
                    CodeEditor.Installation installation = new()
                    {
                        Name = $"New VSCode ({path})",
                        Path = path
                    };
                    _installations.Add(installation);
                }
            }
        }

        //This may not be null, otherwise the preferences window is fucked
        // public CodeEditor.Installation[] Installations => new CodeEditor.Installation[] {};
        public CodeEditor.Installation[] Installations
        {
            get
            {
                UnityEngine.Debug.Log("Somebody asking for installations");
                return new CodeEditor.Installation[] { };
            }
        }

        //This is called when we're selected from the preferences window
        public void Initialize(string editorInstallationPath)
        {
            UnityEngine.Debug.Log($"Initialize called with path {editorInstallationPath}");
        }

        public void OnGUI()
        {
            // GUI.Label(new Rect(0, 0, 100, 30), "Hello");
        }

        public bool OpenProject(string filePath = "", int line = -1, int column = -1)
        {
            UnityEngine.Debug.Log($"OpenProject called with filePath {filePath}, current editor path: {CodeEditor.CurrentEditorPath}");

            //Get currently selected editor installation and open it with whatever args we get
            return CodeEditor.OSOpenFile(CodeEditor.CurrentEditorPath, arguments: "");
            
            // return false;
        }

        public void SyncAll()
        {
            UnityEngine.Debug.Log("SyncAll called");

            //Generate projects/sln and other stuff
        }

        public void SyncIfNeeded(string[] addedFiles, string[] deletedFiles, string[] movedFiles, string[] movedFromFiles, string[] importedFiles)
        {
            UnityEngine.Debug.Log("SyncIfNeeded called");

            //Generate projects/sln and other stuff (if a file that affects compilation changed)
        }

        //This will be called with all sorts of garbage that we've never encountered but unity did at some point.
        //Like any path that was ever registered and then died for example.
        //Still haven't found a way to clear those (probably uninstalling editor, but it's not in EditorPrefs for some reason).
        public bool TryGetInstallationForPath(string editorPath, out CodeEditor.Installation installation)
        {
            UnityEngine.Debug.Log($"TryGetInstallationForPath called with {editorPath}");

            //Check discovered installations from before
            foreach (var inst in _installations)
            {
                if (inst.Path == editorPath)
                {
                    installation = inst;
                    return true;
                }
            }

            //Run discovery again?
            // foreach (string path in KnownVsCodeInstallPaths)
            // {
            //     if (File.Exists(path))
            //     {
            //         installation = new()
            //         {
            //             Name = $"New VSCode ({path})",
            //             Path = path
            //         };

            //         return true;
            //     }
            // }

            //we need to assign something here and hope Unity doesn't use it.
            installation = new()
            {
                Name = "Garbage",
                Path = "/yo/mamas/house"
            };

            return false;
        }
    }

    [InitializeOnLoad]
    public class VSCodeScriptEditor : IExternalCodeEditor
    {
        const string vscode_argument = "vscode_arguments";
        const string vscode_extension = "vscode_userExtensions";
        static readonly GUIContent k_ResetArguments = EditorGUIUtility.TrTextContent("Reset argument");
        List<string> m_affectedFiles = new List<string>(128);

        IDiscovery m_Discoverability;
        IGenerator m_ProjectGeneration;

        static readonly string[] k_SupportedFileNames =
        {
            "code.exe",
            "visualstudiocode.app",
            "visualstudiocode-insiders.app",
            "vscode.app",
            "code.app",
            "code.cmd",
            "code-insiders.cmd",
            "code",
            "com.visualstudio.code",
            "codium"
        };

        const string DefaultArgument = "\"$(ProjectPath)\" -g \"$(File)\":$(Line):$(Column)";
        static string DefaultApp => EditorPrefs.GetString("kScriptsDefaultApp");

        string Arguments
        {
            get => EditorPrefs.GetString(vscode_argument, DefaultArgument);
            set => EditorPrefs.SetString(vscode_argument, value);
        }

        static string[] defaultExtensions
        {
            get
            {
                var customExtensions = new[] { "json", "asmdef", "log" };
                return EditorSettings.projectGenerationBuiltinExtensions
                    .Concat(EditorSettings.projectGenerationUserExtensions)
                    .Concat(customExtensions)
                    .Distinct().ToArray();
            }
        }

        static string[] HandledExtensions
        {
            get
            {
                return HandledExtensionsString
                    .Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.TrimStart('.', '*'))
                    .ToArray();
            }
        }

        static string HandledExtensionsString
        {
            get => EditorPrefs.GetString(vscode_extension, string.Join(";", defaultExtensions));
            set => EditorPrefs.SetString(vscode_extension, value);
        }

        public bool TryGetInstallationForPath(string editorPath, out CodeEditor.Installation installation)
        {
            var lowerCasePath = editorPath.ToLower();
            var filename = Path.GetFileName(lowerCasePath).Replace(" ", "");
            var installations = Installations;
            bool pretending = false;

            if (!k_SupportedFileNames.Contains(filename))
            {
                installation = default;
                return false;
            }

            if (/*!installations.Any()*/ installations.Length == 0)
            {
                //uh what? if discovery finds no installation this just pretends we have one?
                installation = new CodeEditor.Installation
                {
                    Name = "Visual Studio Code",
                    Path = editorPath
                };
                pretending = true;
            }
            else
            {
                try
                {
                    installation = installations.First(inst => inst.Path == editorPath);
                }
                catch (InvalidOperationException)
                {
                    installation = new CodeEditor.Installation
                    {
                        Name = "Visual Studio Code",
                        Path = editorPath
                    };
                    pretending = true;
                }
            }

            UnityEngine.Debug.Log($"Found VSCode installation for path {editorPath}, pretending: {pretending}");

            return true;
        }

        public void OnGUI()
        {
            Arguments = EditorGUILayout.TextField("External Script Editor Args", Arguments);
            if (GUILayout.Button(k_ResetArguments, GUILayout.Width(120)))
            {
                Arguments = DefaultArgument;
            }

            if (GUILayout.Button("Clear CodeEditor Installations", GUILayout.Width(240)))
            {
                var simpleton = Unity.CodeEditor.CodeEditor.Editor;
                var externalCodeEditor = simpleton.CurrentCodeEditor;
                CodeEditor.Unregister(externalCodeEditor);
                Dictionary<string, string> installations = simpleton.GetFoundScriptEditorPaths();
                UnityEngine.Debug.Log($"Clearing Installations: {string.Join(',', installations)}");

                //TODO: GetInstalltionForPath, then Unregister
                // EditorPrefs.DeleteAll();
            }

            EditorGUILayout.LabelField("Generate .csproj files for:");
            EditorGUI.indentLevel++;
            SettingsButton(ProjectGenerationFlag.Embedded, "Embedded packages", "");
            SettingsButton(ProjectGenerationFlag.Local, "Local packages", "");
            SettingsButton(ProjectGenerationFlag.Registry, "Registry packages", "");
            SettingsButton(ProjectGenerationFlag.Git, "Git packages", "");
            SettingsButton(ProjectGenerationFlag.BuiltIn, "Built-in packages", "");
            SettingsButton(ProjectGenerationFlag.LocalTarBall, "Local tarball", "");
            SettingsButton(ProjectGenerationFlag.Unknown, "Packages from unknown sources", "");
            RegenerateProjectFiles();
            EditorGUI.indentLevel--;

            HandledExtensionsString = EditorGUILayout.TextField(new GUIContent("Extensions handled: "), HandledExtensionsString);
        }

        void RegenerateProjectFiles()
        {
            var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(new GUILayoutOption[] { }));
            rect.width = 252;
            if (GUI.Button(rect, "Regenerate project files"))
            {
                m_ProjectGeneration.OnlyJobified = false;
                m_ProjectGeneration.Sync();
            }

            var anotherRect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(new GUILayoutOption[] { }));
            anotherRect.width = 252;
            if (GUI.Button(anotherRect, "Only JobifiedSync"))
            {
                m_ProjectGeneration.OnlyJobified = true;
                m_ProjectGeneration.Sync();
            }
        }

        void SettingsButton(ProjectGenerationFlag preference, string guiMessage, string toolTip)
        {
            var prevValue = m_ProjectGeneration.AssemblyNameProvider.ProjectGenerationFlag.HasFlag(preference);
            var newValue = EditorGUILayout.Toggle(new GUIContent(guiMessage, toolTip), prevValue);
            if (newValue != prevValue)
            {
                m_ProjectGeneration.AssemblyNameProvider.ToggleProjectGeneration(preference);
            }
        }

        public void CreateIfDoesntExist()
        {
            if (!m_ProjectGeneration.SolutionExists())
            {
                m_ProjectGeneration.Sync();
            }
        }

        public void SyncIfNeeded(string[] addedFiles, string[] deletedFiles, string[] movedFiles, string[] movedFromFiles, string[] importedFiles)
        {
            string added = string.Join(',', addedFiles);
            string deleted = string.Join(',', deletedFiles);
            string moved = string.Join(',', movedFiles);
            string movedFrom = string.Join(',', movedFromFiles);
            string imported = string.Join(',', importedFiles);

            UnityEngine.Debug.Log($"{nameof(SyncIfNeeded)} called with addedFiles: {added}, deletedFiles: {deleted}, movedFiles: {moved}, movedFromFiles: {movedFrom}, importedFiles: {imported}");
            (m_ProjectGeneration.AssemblyNameProvider as IPackageInfoCache)?.ResetPackageInfoCache();

            m_affectedFiles.Clear();
            m_affectedFiles.AddRange(addedFiles);
            m_affectedFiles.AddRange(deletedFiles);
            m_affectedFiles.AddRange(movedFiles);
            m_affectedFiles.AddRange(movedFromFiles);
            m_ProjectGeneration.SyncIfNeeded(m_affectedFiles, importedFiles);
        }

        public void SyncAll()
        {
            (m_ProjectGeneration.AssemblyNameProvider as IPackageInfoCache)?.ResetPackageInfoCache();
            AssetDatabase.Refresh();
            m_ProjectGeneration.Sync();
        }

        public bool OpenProject(string path, int line, int column)
        {
            if (path != "" && (!SupportsExtension(path) || !File.Exists(path))) // Assets - Open C# Project passes empty path here
            {
                return false;
            }

            if (line == -1)
                line = 1;
            if (column == -1)
                column = 0;

            string arguments;
            if (Arguments != DefaultArgument)
            {
                arguments = m_ProjectGeneration.ProjectDirectory != path
                    ? CodeEditor.ParseArgument(Arguments, path, line, column)
                    : m_ProjectGeneration.ProjectDirectory;
            }
            else
            {
                arguments = $@"""{m_ProjectGeneration.ProjectDirectory}""";
                if (m_ProjectGeneration.ProjectDirectory != path && path.Length != 0)
                {
                    arguments += $@" -g ""{path}"":{line}:{column}";
                }
            }

            if (Application.platform == RuntimePlatform.OSXEditor)
            {
                return OpenOSX(arguments);
            }

            var app = DefaultApp;
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = app,
                    Arguments = arguments,
                    WindowStyle = app.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                    CreateNoWindow = true,
                    UseShellExecute = true,
                }
            };

            process.Start();
            return true;
        }

        static bool OpenOSX(string arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "open",
                    Arguments = $"-n \"{DefaultApp}\" --args {arguments}",
                    UseShellExecute = true,
                }
            };

            process.Start();
            return true;
        }

        static bool SupportsExtension(string path)
        {
            var extension = Path.GetExtension(path);
            if (string.IsNullOrEmpty(extension))
                return false;
            return HandledExtensions.Contains(extension.TrimStart('.'));
        }

        public CodeEditor.Installation[] Installations => m_Discoverability.PathCallback();

        public VSCodeScriptEditor(IDiscovery discovery, IGenerator projectGeneration)
        {
            m_Discoverability = discovery;
            m_ProjectGeneration = projectGeneration;
        }

        static VSCodeScriptEditor()
        {
            var editor = new VSCodeScriptEditor(new VSCodeDiscovery(), new ProjectGeneration(Directory.GetParent(Application.dataPath).FullName));
            CodeEditor.Register(editor);

            if (IsVSCodeInstallation(CodeEditor.CurrentEditorInstallation))
            {
                editor.CreateIfDoesntExist();
            }
        }

        static bool IsVSCodeInstallation(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }

            var lowerCasePath = path.ToLower();
            var filename = Path
                .GetFileName(lowerCasePath.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar))
                .Replace(" ", "");
            return k_SupportedFileNames.Contains(filename);
        }

        public void Initialize(string editorInstallationPath) { }
    }
}
