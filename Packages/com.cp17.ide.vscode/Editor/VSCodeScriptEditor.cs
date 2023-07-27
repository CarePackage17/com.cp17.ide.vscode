using System;
using System.IO;
using System.Linq;
using System.Diagnostics;
using UnityEditor;
using UnityEngine;
using Unity.CodeEditor;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace VSCodeEditor
{
    [InitializeOnLoad]
    class NewEditor : IExternalCodeEditor
    {
        //For some reason using the previous string ("unity_project_generation_flag") resulted in weird behavior on domain reloads
        //(settings being toggled between all on and all off). Maybe there's some other code fucking with those?
        //Anyway, using our own key sidesteps the issue, so let's go on with it.
        public const string CsprojGenerationSettingsKey = "com.cp17.ide.vscode.csproj-generation-settings";
        const string ArgumentsSettingsKey = "com.cp17.ide.vscode.arguments";
        const string ExtensionsSettingsKey = "com.cp17.ide.vscode.extensions";

        //https://code.visualstudio.com/docs/editor/command-line#_launching-from-command-line
        const string DefaultArgument = "$(ProjectPath) -g $(File):$(Line):$(Column)";
        static readonly string UnityProjectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        List<CodeEditor.Installation>? _installations;
        ProjectGeneration _projectGenerator;
        Task<List<CodeEditor.Installation>> _discoveryTask;

        ProjectGenerationFlag ProjectGenerationSettings
        {
            get => (ProjectGenerationFlag)EditorPrefs.GetInt(CsprojGenerationSettingsKey, defaultValue: 0);
            set => EditorPrefs.SetInt(CsprojGenerationSettingsKey, (int)value);
        }

        string LaunchArguments
        {
            get => EditorPrefs.GetString(ArgumentsSettingsKey, DefaultArgument);
            set => EditorPrefs.SetString(ArgumentsSettingsKey, value);
        }

        static string HandledExtensionsString
        {
            get => EditorPrefs.GetString(ExtensionsSettingsKey, string.Join(";", DefaultExtensions));
            set => EditorPrefs.SetString(ExtensionsSettingsKey, value);
        }

        static IEnumerable<string> DefaultExtensions
        {
            get
            {
                string[] customExtensions = new[] { "json", "asmdef", "log", "rsp" };
                return EditorSettings.projectGenerationBuiltinExtensions
                    .Concat(EditorSettings.projectGenerationUserExtensions)
                    .Concat(customExtensions)
                    .Distinct();
            }
        }

        static NewEditor()
        {
            Verbose.Log("InitializeOnLoad called us");
            Verbose.Log($"Current gen settings: {(ProjectGenerationFlag)EditorPrefs.GetInt(CsprojGenerationSettingsKey, defaultValue: 0)}");

            //Here we can create an actual instance of IExternalCodeEditor and register it, then
            //it should show up in the UI.
            NewEditor editor = new();
            CodeEditor.Register(editor);
        }

        NewEditor()
        {
            _projectGenerator = new(UnityProjectPath);
            _projectGenerator.OnlyJobified = true;
            _projectGenerator.GenerateAll(true);

            _discoveryTask = Discovery.DiscoverVsCodeInstallsAsync();
        }

        //This may not return null, otherwise the preferences window is fucked
        public CodeEditor.Installation[] Installations
        {
            get
            {
                Verbose.Log("Unity asking for installations");
                return _installations!.ToArray();
            }
        }

        //This is called when one of our installations is selected from the preferences window
        public void Initialize(string editorInstallationPath)
        {
            Verbose.Log($"Initialize called with path {editorInstallationPath}");
        }

        //https://docs.unity3d.com/Manual/gui-Basics.html
        //Unfortunately there doesn't seem to be a UI Toolkit way of doing this as of 2021.3.
        public void OnGUI()
        {
            LaunchArguments = EditorGUILayout.TextField("External Script Editor Args", LaunchArguments);

            //maybe localization should use this instead?
            //https://docs.unity3d.com/2021.3/Documentation/ScriptReference/L10n.html
            if (GUILayout.Button(EditorGUIUtility.TrTextContent("Reset argument"), GUILayout.Width(120)))
            {
                LaunchArguments = DefaultArgument;
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
            RegenerateProjectFilesButton();
            EditorGUI.indentLevel--;

            //TODO: maybe a reset button for this like for editor args?
            HandledExtensionsString = EditorGUILayout.TextField(new GUIContent("Extensions handled: "), HandledExtensionsString);
        }

        void SettingsButton(ProjectGenerationFlag preference, string optionText, string tooltip)
        {
            ProjectGenerationFlag currentSettings = ProjectGenerationSettings;

            bool prefEnabled = currentSettings.HasFlag(preference);
            bool newValue = EditorGUILayout.Toggle(new GUIContent(optionText, tooltip), prefEnabled);
            if (newValue != prefEnabled)
            {
                ProjectGenerationSettings = currentSettings ^ preference;
            }
        }

        void RegenerateProjectFilesButton()
        {
            var rect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(new GUILayoutOption[] { }));
            rect.width = 252;
            if (GUI.Button(rect, "Regenerate project files"))
            {
                _projectGenerator.OnlyJobified = false;
                _projectGenerator.Sync();
            }

            var anotherRect = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect(new GUILayoutOption[] { }));
            anotherRect.width = 252;
            if (GUI.Button(anotherRect, "Only JobifiedSync"))
            {
                _projectGenerator.OnlyJobified = true;
                _projectGenerator.Sync();
            }
        }

        bool ExtensionHandledByUs(ReadOnlySpan<char> filePath)
        {
            var extension = Path.GetExtension(filePath);
            if (!extension.IsEmpty)
            {
                //we don't want the dot when doing the comparison, so advance by 1 char
                extension = extension[1..];
            }

            if (!HandledExtensionsString.Split(';', StringSplitOptions.RemoveEmptyEntries).Contains(extension.ToString()))
            {
                return false;
            }

            return true;
        }

        //Called when somebody double-clicks a script file (and others with extensions we handle?)
        //or uses the "Assets > Open C# Project" menu item.
        public bool OpenProject(string filePath = "", int line = -1, int column = -1)
        {
            //Default argument is the project folder. Since "Assets > Open C# Project" will not pass
            //us a file name, we set this as a fallback.
            string args = UnityProjectPath;

            if (!string.IsNullOrEmpty(filePath))
            {
                if (!ExtensionHandledByUs(filePath)) return false;

                //This does QuoteForProcessStart internally, so we don't need to do it later.
                args = CodeEditor.ParseArgument(LaunchArguments, filePath, line, column);
            }

            //Not sure if we need to quote the editor path too. TODO: test on Windows.
            string editorPath = CodeEditor.QuoteForProcessStart(CodeEditor.CurrentEditorPath);
            UnityEngine.Debug.Log($"Opening editor at {editorPath} with {args}");

            return CodeEditor.OSOpenFile(editorPath, args);
        }

        public void SyncAll()
        {
            Verbose.Log("SyncAll called");

            _projectGenerator.Sync();
        }

        public void SyncIfNeeded(string[] addedFiles, string[] deletedFiles, string[] movedFiles, string[] movedFromFiles, string[] importedFiles)
        {
            Verbose.Log("SyncIfNeeded called");

            _projectGenerator.Sync();
        }

        //This will be called with all sorts of garbage that we've never encountered but unity did at some point.
        //Like any path that was ever registered and then died for example.
        //Still haven't found a way to clear those (probably uninstalling editor, but it's not in EditorPrefs for some reason).
        public bool TryGetInstallationForPath(string editorPath, out CodeEditor.Installation installation)
        {
            Verbose.Log($"TryGetInstallationForPath called with {editorPath}");

            //The task has been started before, but now we need the result. Since we can't change the signature of this
            //method (part of IExternalCodeEditor) we use .Result instead of await.
            //Not sure if this does a lot for editor interactivity; need to measure to see if it's worth the extra complexity.
            if (_installations == null)
            {
                _installations = _discoveryTask.Result;
            }

            //Check discovered installations from before
            foreach (var inst in _installations)
            {
                if (inst.Path == editorPath)
                {
                    installation = inst;
                    return true;
                }
            }

            installation = default;
            return false;
        }
    }

    static class Verbose
    {
        [Conditional("CP17_VSCODE_VERBOSE")]
        public static void Log(string message)
        {
            UnityEngine.Debug.Log(message);
        }
    }
}
