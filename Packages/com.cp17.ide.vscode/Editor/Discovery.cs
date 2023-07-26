using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Unity.CodeEditor;

static class Discovery
    {
        static readonly string[] KnownVsCodeInstallFolders = new[]
        {
            #if UNITY_EDITOR_LINUX
            "/bin/",
            "/usr/bin/",
            "/var/lib/flatpak/exports/bin/",
            #endif
            #if UNITY_EDITOR_WIN
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
            #endif
            #if UNITY_EDITOR_OSX
            "/Applications/"
            #endif
        };
        static readonly string[] KnownVsCodeExecutableNames = new[]
        {
            #if UNITY_EDITOR_LINUX
            "code",
            "codium",
            "com.visualstudio.code",
            "com.vscodium.codium"
            #endif
            #if UNITY_EDITOR_WIN
            "code.exe",
            "code.cmd",
            "code-insiders.cmd",
            #endif
            #if UNITY_EDITOR_OSX
            "visualstudiocode.app",
            "visualstudiocode-insiders.app",
            "vscode.app",
            "code.app",
            #endif
        };

        internal static Task<List<CodeEditor.Installation>> DiscoverVsCodeInstallsAsync()
        {
            //This doesn't need the Unity native API, so we can run it on the thread pool.
            return Task.Run(() =>
            {
                List<CodeEditor.Installation> installations = new();

                foreach (string folder in KnownVsCodeInstallFolders)
                {
                    foreach (string fileName in KnownVsCodeExecutableNames)
                    {
                        string finalPath = Path.Combine(folder, fileName);
                        if (File.Exists(finalPath))
                        {
                            //This can be made prettier, I'm sure.
                            ProcessStartInfo info = new(finalPath, "--version");
                            info.RedirectStandardOutput = true;
                            info.UseShellExecute = false;
                            Process vsCodeProcess = Process.Start(info);
                            string output = vsCodeProcess.StandardOutput.ReadLine();

                            CodeEditor.Installation installation = new()
                            {
                                Name = $"VS Code {output} ({finalPath})",
                                Path = finalPath
                            };
                            installations.Add(installation);
                        }
                    }
                }

                return installations;
            });
        }
    }