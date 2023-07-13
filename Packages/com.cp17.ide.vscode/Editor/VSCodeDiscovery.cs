using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.CodeEditor;

namespace VSCodeEditor
{
    public interface IDiscovery
    {
        CodeEditor.Installation[] PathCallback();
    }

    public class VSCodeDiscovery : IDiscovery
    {
        List<CodeEditor.Installation>? m_Installations;

        public CodeEditor.Installation[] PathCallback()
        {
            if (m_Installations == null)
            {
                m_Installations = new List<CodeEditor.Installation>();
                FindInstallationPaths();
            }
            
            m_Installations.Add(new CodeEditor.Installation()
            {
                Name = "Test",
                Path = "/usr/bin/code"
            });


            return m_Installations.ToArray();
        }

        void FindInstallationPaths()
        {
            string[] possiblePaths =
#if UNITY_EDITOR_OSX
            {
                "/Applications/Visual Studio Code.app",
                "/Applications/Visual Studio Code - Insiders.app"
            };
#elif UNITY_EDITOR_WIN
            {
                GetProgramFiles() + @"/Microsoft VS Code/bin/code.cmd",
                GetProgramFiles() + @"/Microsoft VS Code/Code.exe",
                GetProgramFiles() + @"/Microsoft VS Code Insiders/bin/code-insiders.cmd",
                GetProgramFiles() + @"/Microsoft VS Code Insiders/Code.exe",
                GetLocalAppData() + @"/Programs/Microsoft VS Code/bin/code.cmd",
                GetLocalAppData() + @"/Programs/Microsoft VS Code/Code.exe",
                GetLocalAppData() + @"/Programs/Microsoft VS Code Insiders/bin/code-insiders.cmd",
                GetLocalAppData() + @"/Programs/Microsoft VS Code Insiders/Code.exe",
            };
#elif UNITY_EDITOR_LINUX
            {
                "/usr/bin/code",
                "/usr/share/code/bin/code",
                "/bin/code",
                "/usr/local/bin/code",
                "/var/lib/flatpak/exports/bin/com.visualstudio.code",
                "/snap/current/bin/code",
                "/snap/bin/code",
                "/usr/bin/codium",
                "/var/lib/flatpak/exports/bin/com.vscodium.codium"
            };
#endif

            
            string dir = Environment.CurrentDirectory;
            UnityEngine.Debug.Log("cwd: " + dir);
            DirectoryInfo info = new("/usr/share");
            var infos = info.EnumerateFileSystemInfos();
            foreach (var thing in infos)
            {
                UnityEngine.Debug.Log(thing.FullName);
            }

            int fd = Mono.Unix.Native.Syscall.open("/usr/share/code", Mono.Unix.Native.OpenFlags.O_PATH);
            if (fd == -1)
            {
                UnityEngine.Debug.Log($"open failed with {Mono.Unix.Native.Syscall.GetLastError()}");
            }
            else
            {
                UnityEngine.Debug.Log("open succeeded");
                Mono.Unix.Native.Syscall.close(fd);
            }

            if (Mono.Unix.Native.Syscall.stat("/usr/share/", out Mono.Unix.Native.Stat _) != 0)
            {
                UnityEngine.Debug.Log($"don't exist wtf: {Mono.Unix.Native.Syscall.GetLastError()}");
            }
            if (Mono.Unix.Native.Syscall.stat("/usr/share/code/", out Mono.Unix.Native.Stat _) != 0)
            {
                UnityEngine.Debug.Log($"don't exist wtf: {Mono.Unix.Native.Syscall.GetLastError()}");
            }

            var existingPaths = possiblePaths.Where(VSCodeExists).ToList();

            //So there's a problem here. This works as expected on net6.0 on Linux
            //but not on Unity Mono (sigh). Maybe Mono.Posix can help?
            // if (File.Exists("/usr/bin/code")) Debug.Log("Yo I exist");
            // if (new FileInfo("/usr/bin/code").Exists) Debug.Log("Yo I exist");
            UnityEngine.Debug.Log($"existing paths: {string.Join('\n', existingPaths)}");
            if (!existingPaths.Any())
            {
                return;
            }

            var lcp = GetLongestCommonPrefix(existingPaths);
            switch (existingPaths.Count)
            {
                case 1:
                    {
                        var path = existingPaths.First();
                        m_Installations = new List<CodeEditor.Installation>
                    {
                        new CodeEditor.Installation
                        {
                            Path = path,
                            Name = path.Contains("Insiders")
                                ? "Visual Studio Code Insiders"
                                : "Visual Studio Code"
                        }
                    };
                        break;
                    }
                case 2 when existingPaths.Any(path => !(path.Substring(lcp.Length).Contains("/") || path.Substring(lcp.Length).Contains("\\"))):
                    {
                        goto case 1;
                    }
                default:
                    {
                        m_Installations = existingPaths.Select(path => new CodeEditor.Installation
                        {
                            Name = $"Visual Studio Code Insiders ({path.Substring(lcp.Length)})",
                            Path = path
                        }).ToList();

                        break;
                    }
            }
        }

#if UNITY_EDITOR_WIN
        //Environment.GetFolderPath(SpecialFolder) might be a better fit here
        static string GetProgramFiles()
        {
            return Environment.GetEnvironmentVariable("ProgramFiles")?.Replace("\\", "/");
        }

        static string GetLocalAppData()
        {
            return Environment.GetEnvironmentVariable("LOCALAPPDATA")?.Replace("\\", "/");
        }
#endif

        static string GetLongestCommonPrefix(List<string> paths)
        {
            var baseLength = paths.First().Length;
            for (var pathIndex = 1; pathIndex < paths.Count; pathIndex++)
            {
                baseLength = Math.Min(baseLength, paths[pathIndex].Length);
                for (var i = 0; i < baseLength; i++)
                {
                    if (paths[pathIndex][i] == paths[0][i]) continue;

                    baseLength = i;
                    break;
                }
            }

            return paths[0].Substring(0, baseLength);
        }

        static bool VSCodeExists(string path)
        {
#if UNITY_EDITOR_OSX
            return System.IO.Directory.Exists(path);
#else
            if (Mono.Unix.UnixFileSystemInfo.TryGetFileSystemEntry(path, out var entry))
            {
                int ret = Mono.Unix.Native.Syscall.stat(path, out Mono.Unix.Native.Stat buf);
                if (ret == 0)
                {
                    UnityEngine.Debug.Log("stat says yes");
                }
                var err = Mono.Unix.Native.Syscall.GetLastError();
                UnityEngine.Debug.Log($"{entry.FullName} exists: {entry.Exists}, stat err: {err}");
            }

            return new FileInfo(path).Exists;
#endif
        }
    }
}
