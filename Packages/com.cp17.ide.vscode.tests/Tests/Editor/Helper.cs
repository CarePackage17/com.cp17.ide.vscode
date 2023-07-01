using UnityEditor.Compilation;

namespace VSCodeEditor.Tests
{
    public static class Helper
    {
        public static string GetLangVersion()
        {
            var languageVersion = new ScriptCompilerOptions().LanguageVersion;
            return languageVersion;
        }
    }
}