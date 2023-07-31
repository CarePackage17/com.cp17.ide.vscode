using System;
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
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft VS Code"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Microsoft VS Code", "bin")
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
                        string version = GetVsCodeVersion(finalPath);

                        //Unity uses '/' as a delimiter to create submenus, which we don't want in this case.
                        //We use a different Unicode char that looks kinda like a slash, as suggested here:
                        //https://discussions.unity.com/t/can-genericmenu-item-content-display/63119/4
                        string displayPath = finalPath.Replace("/", "\u200A\u2044");
                        CodeEditor.Installation installation = new()
                        {
                            Name = $"VS Code {version} ({displayPath})",
                            Path = finalPath
                        };
                        installations.Add(installation);
                    }
                }
            }

            return installations;
        });
    }

    static string GetVsCodeVersion(string vsCodeExePath)
    {
        #if UNITY_EDITOR_WIN
        //On Windows the Code.exe binary does not accept command line args directly; code.cmd seems to handle that.
        //I guess we should ignore Code.exe here and only do something for the .cmd file.
        if (!vsCodeExePath.AsSpan().EndsWith(".cmd"))
        {
            return "";
        }
        #endif

        ProcessStartInfo info = new(vsCodeExePath, "--version")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using Process vsCodeProcess = Process.Start(info);

        //At the time of writing vscode --version returned three lines: version, commit hash and CPU architecture.
        //We only care about displaying the version, so reading the first line is enough.
        string output = vsCodeProcess.StandardOutput.ReadLine();

        return output;
    }
}
